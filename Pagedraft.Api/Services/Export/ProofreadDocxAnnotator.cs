using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Pagedraft.Api.Services.Export;

/// <summary>Output of <see cref="ProofreadDocxAnnotator.Annotate"/>.</summary>
/// <param name="Docx">The annotated package; the INPUT array itself when nothing was placed.</param>
/// <param name="Placed">Pending suggestions written as a comment plus tracked change(s).</param>
/// <param name="Unanchored">
/// Pending suggestions that could not be placed: no match, an ambiguous match the contexts do not settle,
/// a span crossing a paragraph or touching text that is not plain body text (a field result, an existing
/// tracked insertion, a tab or break), or a span overlapping one placed before it.
/// <c>Placed + Unanchored</c> is always the number of pending suggestions.
/// </param>
public sealed record ProofreadAnnotationResult(byte[] Docx, int Placed, int Unanchored);

/// <summary>
/// Writes pending proofread suggestions into a DOCX as Word tracked changes (<c>w:del</c> / <c>w:ins</c>)
/// with one ranged Word comment per suggestion, using the Open XML SDK only.
///
/// <para>WHY HAND-WRITTEN XML: DocIO's <c>TrackChanges = true</c> records NOTHING for API edits on the shipped
/// 32.1.24 (five operations measured, 0 revisions each; plan whole-book-proofread-and-annotated-docx-2026-10-11).
/// Mixing DocIO comments with Open XML revisions on one package is avoided, so this writes both.</para>
///
/// <para>ANCHORING: a suggestion's <c>OriginalText</c> and contexts are on the NORMALIZED axis of
/// <c>SourceTextSnapshot</c> while the DOCX is raw, so the body's paragraphs are joined with a line break,
/// normalized through <see cref="NormalizedTextMap"/> (the server's own rules, mirrored), and searched there.
/// The rule is the client's <c>suggestion-anchor.service.ts</c> <c>relocateOne</c>: a unique match wins, several
/// matches are settled by <c>ContextBefore</c>/<c>ContextAfter</c> inside a 60-character window, anything
/// else is counted as unanchored rather than guessed. Offsets are not trusted: the export's paragraphs and
/// the snapshot need not share a coordinate system.</para>
///
/// <para>Only pending suggestions (<c>Outcome == null</c>) are written: an Accepted one is already in the
/// text, Dismissed/Reverted/Superseded ones are not proposals any more. They are taken in
/// <c>StartOffset</c> order and one that overlaps an earlier placed one is dropped (counted unanchored).</para>
/// </summary>
public sealed partial class ProofreadDocxAnnotator
{
    /// <summary>Same window as the client's <c>CONTEXT_WINDOW</c>.</summary>
    internal const int ContextWindow = 60;

    private readonly ILogger<ProofreadDocxAnnotator> _logger;

    public ProofreadDocxAnnotator(ILogger<ProofreadDocxAnnotator> logger)
    {
        _logger = logger;
    }

    public ProofreadAnnotationResult Annotate(
        byte[] docx, IReadOnlyList<AnalysisSuggestion> suggestions, string language, DateTime stamp)
    {
        ArgumentNullException.ThrowIfNull(docx);

        var pending = (suggestions ?? Array.Empty<AnalysisSuggestion>())
            .Where(s => s.Outcome == null)
            .OrderBy(s => s.StartOffset ?? int.MaxValue)
            .ThenBy(s => s.OrderIndex)
            .ToList();
        if (pending.Count == 0)
            return new ProofreadAnnotationResult(docx, 0, 0);

        using var stream = new MemoryStream();
        stream.Write(docx, 0, docx.Length);
        stream.Position = 0;

        var placed = new List<Edit>();
        var unanchored = 0;
        using (var package = WordprocessingDocument.Open(stream, true))
        {
            var main = package.MainDocumentPart;
            var body = main?.Document?.Body;
            if (main == null || body == null)
                return new ProofreadAnnotationResult(docx, 0, pending.Count);

            var text = DocumentText.Build(body);
            foreach (var suggestion in pending)
            {
                var edit = Resolve(suggestion, text, out var reason);
                if (edit != null && placed.Any(p => p.Conflicts(edit)))
                {
                    edit = null;
                    reason = "overlaps-placed";
                }
                if (edit == null)
                {
                    unanchored++;
                    _logger.LogDebug(
                        "Proofread annotation: suggestion {SuggestionId} not placed ({Reason})", suggestion.Id, reason);
                    continue;
                }
                placed.Add(edit);
            }

            // Nothing to write: hand back the caller's own bytes, untouched.
            if (placed.Count == 0)
                return new ProofreadAnnotationResult(docx, 0, unanchored);

            WriteEdits(main, text, placed, language, stamp);
        }

        return new ProofreadAnnotationResult(stream.ToArray(), placed.Count, unanchored);
    }

    // ---------------------------------------------------------------- resolution

    private static Edit? Resolve(AnalysisSuggestion s, DocumentText text, out string reason)
    {
        var map = text.Map;
        var original = TextNormalization.NormalizeTextForAnalysis(s.OriginalText ?? string.Empty) ?? string.Empty;
        var suggested = s.SuggestedText ?? string.Empty;

        if (original.Length == 0)
            return ResolveInsertion(s, text, suggested, out reason);

        var matches = FindAll(map.Text, original);
        int? at = matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => string.IsNullOrEmpty(s.ContextBefore) && string.IsNullOrEmpty(s.ContextAfter)
                ? null
                : PickBestMatch(matches, original.Length, map.Text, s),
        };
        if (at == null)
        {
            reason = matches.Count == 0 ? "no-match" : "ambiguous";
            return null;
        }

        var (rawStart, rawEnd) = map.ToRawSpan(at.Value, original.Length);
        if (!text.IsPlaceableSpan(rawStart, rawEnd))
        {
            reason = "not-plain-body-text";
            return null;
        }

        reason = string.Empty;
        return new Edit(s, rawStart, rawEnd, original, suggested);
    }

    /// <summary>
    /// A pure insertion has no text to find, so it is anchored by its contexts: the concatenation
    /// <c>ContextBefore + ContextAfter</c> must occur exactly once, and the insertion goes between them.
    /// </summary>
    private static Edit? ResolveInsertion(AnalysisSuggestion s, DocumentText text, string suggested, out string reason)
    {
        var before = s.ContextBefore ?? string.Empty;
        var probe = before + (s.ContextAfter ?? string.Empty);
        if (suggested.Length == 0 || probe.Length == 0)
        {
            reason = "empty";
            return null;
        }

        var hits = FindAll(text.Map.Text, probe);
        if (hits.Count != 1)
        {
            reason = hits.Count == 0 ? "no-match" : "ambiguous";
            return null;
        }

        var point = text.Map.ToRawInsertionPoint(hits[0] + before.Length);
        if (!text.IsPlaceablePoint(point))
        {
            reason = "not-plain-body-text";
            return null;
        }

        reason = string.Empty;
        return new Edit(s, point, point, string.Empty, suggested);
    }

    private static List<int> FindAll(string haystack, string needle)
    {
        var positions = new List<int>();
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0)
        {
            positions.Add(idx);
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }
        return positions;
    }

    /// <summary>
    /// The client's <c>pickBestMatch</c>: +2 when <c>ContextBefore</c> occurs in the 60 characters before
    /// the hit, +2 when <c>ContextAfter</c> occurs in the 60 after; the FIRST highest-scoring hit wins, and
    /// no hit scoring above zero means ambiguous (null), never an arbitrary pick.
    /// </summary>
    internal static int? PickBestMatch(IReadOnlyList<int> positions, int needleLength, string text, AnalysisSuggestion s)
    {
        var bestPos = positions[0];
        var bestScore = int.MinValue;
        foreach (var pos in positions)
        {
            var score = 0;
            if (!string.IsNullOrEmpty(s.ContextBefore))
            {
                var windowStart = Math.Max(0, pos - ContextWindow);
                if (text[windowStart..pos].Contains(s.ContextBefore, StringComparison.Ordinal)) score += 2;
            }
            if (!string.IsNullOrEmpty(s.ContextAfter))
            {
                var afterStart = pos + needleLength;
                var afterEnd = Math.Min(text.Length, afterStart + ContextWindow);
                if (text[afterStart..afterEnd].Contains(s.ContextAfter, StringComparison.Ordinal)) score += 2;
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestPos = pos;
            }
        }
        return bestScore <= 0 ? null : bestPos;
    }

    // ---------------------------------------------------------------- writing

    private static void WriteEdits(
        MainDocumentPart main, DocumentText text, List<Edit> edits, string language, DateTime stamp)
    {
        var author = ProofreadAnnotationStrings.Author(language);
        var commentsPart = main.WordprocessingCommentsPart ?? main.AddNewPart<WordprocessingCommentsPart>();
        commentsPart.Comments ??= new W.Comments();

        var nextCommentId = MaxWordId(commentsPart.Comments) + 1;
        var nextRevisionId = Math.Max(MaxWordId(main.Document!), MaxWordId(commentsPart.Comments)) + 1;

        // Ids are handed out in document order so comment 1 is the first one the author meets.
        foreach (var edit in edits.OrderBy(e => e.RawStart))
        {
            edit.CommentId = nextCommentId++;
            if (edit.Original.Length > 0) edit.DeletionId = nextRevisionId++;
            if (edit.Suggested.Length > 0) edit.InsertionId = nextRevisionId++;
            commentsPart.Comments.Append(BuildComment(edit, language, author, stamp));
        }

        // Placed right to left: splitting a w:t keeps the ORIGINAL node as the left fragment, so every
        // character reference held by an edit further left is still valid when its turn comes.
        foreach (var edit in edits.OrderByDescending(e => e.RawStart))
            Place(text, edit, author, stamp);

        main.Document!.Save();
        commentsPart.Comments.Save();
    }

    private static W.Comment BuildComment(Edit edit, string language, string author, DateTime stamp)
    {
        var lines = new List<string>
        {
            ProofreadAnnotationStrings.SuggestionLine(language, edit.Original, edit.Suggested),
        };
        var category = ProofreadAnnotationStrings.CategoryLine(language, edit.Suggestion.Category);
        if (category != null) lines.Add(category);
        if (!string.IsNullOrWhiteSpace(edit.Suggestion.Explanation)) lines.Add(edit.Suggestion.Explanation.Trim());

        var rtl = ProofreadAnnotationStrings.IsHebrew(language);
        var comment = new W.Comment
        {
            Id = edit.CommentId.ToString(),
            Author = author,
            Initials = ProofreadAnnotationStrings.Initials,
            Date = stamp,
        };
        for (var i = 0; i < lines.Count; i++)
        {
            var paragraph = new W.Paragraph();
            if (rtl) paragraph.Append(new W.ParagraphProperties(new W.BiDi()));
            if (i == 0) paragraph.Append(new W.Run(new W.AnnotationReferenceMark()));
            var run = new W.Run();
            if (rtl) run.Append(new W.RunProperties(new W.RightToLeftText()));
            run.Append(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            paragraph.Append(run);
            comment.Append(paragraph);
        }
        return comment;
    }

    /// <summary>Highest numeric <c>w:id</c> anywhere under <paramref name="root"/>, or -1.</summary>
    private static int MaxWordId(OpenXmlElement root)
    {
        const string wordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var max = -1;
        foreach (var element in root.Descendants())
        {
            foreach (var attribute in element.GetAttributes())
            {
                if (attribute.LocalName == "id" && attribute.NamespaceUri == wordNs
                    && int.TryParse(attribute.Value, out var id) && id > max)
                    max = id;
            }
        }
        return max;
    }

    /// <summary>One suggestion resolved to a raw span (or, for a pure insertion, a raw point).</summary>
    private sealed class Edit
    {
        public Edit(AnalysisSuggestion suggestion, int rawStart, int rawEnd, string original, string suggested)
        {
            Suggestion = suggestion;
            RawStart = rawStart;
            RawEnd = rawEnd;
            Original = original;
            Suggested = suggested;
        }

        public AnalysisSuggestion Suggestion { get; }
        public int RawStart { get; }
        public int RawEnd { get; }
        public string Original { get; }
        public string Suggested { get; }
        public int CommentId { get; set; }
        public int DeletionId { get; set; }
        public int InsertionId { get; set; }

        public bool IsInsertion => RawStart == RawEnd;

        /// <summary>
        /// Spans conflict when they share a character; a point conflicts with a span it touches at either
        /// edge or lies inside, and with another point at the same place. Touching spans do not conflict.
        /// </summary>
        public bool Conflicts(Edit other)
        {
            if (!IsInsertion && !other.IsInsertion)
                return RawStart < other.RawEnd && other.RawStart < RawEnd;
            if (IsInsertion && other.IsInsertion)
                return RawStart == other.RawStart;
            var (point, span) = IsInsertion ? (this, other) : (other, this);
            return span.RawStart <= point.RawStart && point.RawStart <= span.RawEnd;
        }
    }
}
