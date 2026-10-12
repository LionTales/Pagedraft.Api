using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Export;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE PREMISE THE ANNOTATED EXPORT IS BUILT ON (plan whole-book-proofread-and-annotated-docx-2026-10-11, c4).
///
/// The annotated book export annotates each unit's DOCX buffer on its own (each unit has its own Active
/// Proofread result, and its own suggestions only make sense against its own text) and only THEN hands the
/// buffers to <see cref="BookAssemblyService.AssembleDocx"/>, which merges them with DocIO
/// <c>ImportContent(UseDestinationStyles)</c> and re-saves through DocIO. That is only sound if DocIO carries
/// hand-written Open XML <c>w:del</c>/<c>w:ins</c> and comments THROUGH an import and a save: none dropped,
/// none duplicated, and the comment ids of the second buffer (which restart at 0, exactly like the first's)
/// re-numbered rather than colliding. If this test ever goes red, the design must switch to
/// annotate-AFTER-assembly (walk the assembled body and attribute paragraphs to units by count).
///
/// Inputs come off the REAL <see cref="SfdtConversionService"/>, the writer the export uses, not a hand-built
/// package, so the premise is measured on the shape production assembles.
/// </summary>
public class ProofreadAnnotatedAssemblyPremiseTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly DateTime Stamp = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly ProofreadDocxAnnotator _annotator = new(NullLogger<ProofreadDocxAnnotator>.Instance);
    private readonly SfdtConversionService _sfdt = new();

    [Fact]
    public void ImportContent_keeps_every_revision_and_comment_of_separately_annotated_buffers()
    {
        var first = _annotator.Annotate(
            Chapter("רוני הלכה לבית הספר היום.", "היא פגשה את דני ליד השער."),
            new[]
            {
                Sug("לבית הספר", "לביה\"ס", 10, "קיצור מקובל."),
                Sug("השער", "השערים", 40, "צורת רבים."),
            },
            "he", Stamp);
        var second = _annotator.Annotate(
            Chapter("הוא הלך הביתה ואמר שלום לכולם.", "בערב ירד גשם חזק על העיר."),
            new[]
            {
                Sug("שלום", "שלומות", 20, "צורת רבים."),
                Sug("חזק ", "", 45, "מיותר."),
            },
            "he", Stamp);
        Assert.Equal((2, 0), (first.Placed, first.Unanchored));
        Assert.Equal((2, 0), (second.Placed, second.Unanchored));

        // Both inputs number their comments from 0: a collision is possible only if the import does not renumber.
        Assert.Equal(new[] { "0", "1" }, CommentIds(first.Docx));
        Assert.Equal(new[] { "0", "1" }, CommentIds(second.Docx));

        int RevisionsOf(byte[] docx)
        {
            using var d = new WordDocument(new MemoryStream(docx), FormatType.Docx);
            return d.Revisions.Count;
        }
        var expectedRevisions = RevisionsOf(first.Docx) + RevisionsOf(second.Docx);
        Assert.Equal(7, expectedRevisions); // 2 x (del + ins) + (del + ins) + del

        var assembled = new BookAssemblyService().AssembleDocx(new[] { first.Docx, second.Docx });

        using (var reloaded = new WordDocument(new MemoryStream(assembled), FormatType.Docx))
        {
            Assert.Equal(expectedRevisions, reloaded.Revisions.Count);
            Assert.Equal(4, reloaded.Comments.Count);
            // The import keeps the annotator's authorship on the second buffer's revisions too.
            Assert.All(reloaded.Revisions.Cast<Revision>(), r => Assert.Equal("PageDraft הגהה", r.Author));
        }

        // Ids: four distinct comments, each with exactly one range start, one range end and one reference.
        var ids = CommentIds(assembled);
        Assert.Equal(4, ids.Length);
        Assert.Equal(4, ids.Distinct().Count());
        var body = Part(assembled, "word/document.xml");
        foreach (var id in ids)
        {
            Assert.Single(body.Descendants(W + "commentRangeStart"), e => (string?)e.Attribute(W + "id") == id);
            Assert.Single(body.Descendants(W + "commentRangeEnd"), e => (string?)e.Attribute(W + "id") == id);
            Assert.Single(body.Descendants(W + "commentReference"), e => (string?)e.Attribute(W + "id") == id);
        }

        // And the merged document still means the right thing: accept-all is the corrected manuscript.
        using var accepted = new WordDocument(new MemoryStream(assembled), FormatType.Docx);
        foreach (var revision in accepted.Revisions.Cast<Revision>().ToList()) revision.Accept();
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(accepted.GetText());
        Assert.Contains("ליד השערים.", text, StringComparison.Ordinal);
        Assert.Contains("לביה\"ס היום", text, StringComparison.Ordinal);
        Assert.Contains("שלומות לכולם", text, StringComparison.Ordinal);
        Assert.Contains("גשם על העיר", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The common production shape since comments are written only when they add information: a buffer with
    /// tracked changes and NO comments part, assembled ahead of one that has a comment. The revisions of both
    /// survive, the one comment survives once, and no comment appears that nobody wrote.
    /// </summary>
    [Fact]
    public void ImportContent_keeps_the_revisions_of_a_buffer_with_no_comments_part()
    {
        var plain = _annotator.Annotate(
            Chapter("רוני הלכה לבית הספר היום.", "היא פגשה את דני ליד השער."),
            new[] { Sug("לבית הספר", "לביה\"ס", 10, ""), Sug("השער", "השערים", 40, "") },
            "he", Stamp);
        var mixed = _annotator.Annotate(
            Chapter("הוא הלך הביתה ואמר שלום לכולם.", "בערב ירד גשם חזק על העיר."),
            new[] { Sug("שלום", "שלומות", 20, "צורת רבים."), Sug("חזק ", "", 45, "") },
            "he", Stamp);
        Assert.Equal(2, plain.Placed);
        Assert.Equal(2, mixed.Placed);
        Assert.Null(PartOrNull(plain.Docx, "word/comments.xml"));
        Assert.Single(CommentIds(mixed.Docx));

        var assembled = new BookAssemblyService().AssembleDocx(new[] { plain.Docx, mixed.Docx });

        using (var reloaded = new WordDocument(new MemoryStream(assembled), FormatType.Docx))
        {
            Assert.Equal(7, reloaded.Revisions.Count); // (del + ins) x2 + (del + ins) + del
            Assert.Equal(1, reloaded.Comments.Count);
        }
        var body = Part(assembled, "word/document.xml");
        Assert.Single(body.Descendants(W + "commentReference"));

        using var accepted = new WordDocument(new MemoryStream(assembled), FormatType.Docx);
        foreach (var revision in accepted.Revisions.Cast<Revision>().ToList()) revision.Accept();
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(accepted.GetText());
        Assert.Contains("לביה\"ס היום", text, StringComparison.Ordinal);
        Assert.Contains("ליד השערים.", text, StringComparison.Ordinal);
        Assert.Contains("שלומות לכולם", text, StringComparison.Ordinal);
        Assert.Contains("גשם על העיר", text, StringComparison.Ordinal);
    }

    private static XDocument? PartOrNull(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private byte[] Chapter(params string[] paragraphs) =>
        _sfdt.ConvertSfdtToDocx(SfdtConversionService.CreateMinimalSfdtFromText(string.Join("\n", paragraphs)));

    // Every suggestion carries an explanation: only an explained (or ktiv-male) suggestion gets a comment, and
    // the premise is about comments surviving the import as much as revisions.
    private static AnalysisSuggestion Sug(string original, string suggested, int start, string explanation) => new()
    {
        Id = Guid.NewGuid(),
        OriginalText = original,
        SuggestedText = suggested,
        StartOffset = start,
        EndOffset = start + original.Length,
        Explanation = explanation,
    };

    private static string[] CommentIds(byte[] docx) =>
        Part(docx, "word/comments.xml").Root!.Elements(W + "comment")
            .Select(c => (string?)c.Attribute(W + "id") ?? string.Empty).ToArray();

    private static XDocument Part(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"{name} missing");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
