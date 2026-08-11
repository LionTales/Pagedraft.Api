using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The READ-ONLY guides serving contract (chatbot phase A.2, c1): the index a client builds its help
/// page from, and the single-guide content the reader renders.
///
/// <para>Driven against the REAL shipped corpus wherever the contract is about the corpus, and against
/// temp fixtures only for states the real corpus deliberately cannot be in (a missing directory, a
/// document with no heading). Same split, and the same reason, as
/// <see cref="ProductChatCorpusTests"/>.</para>
///
/// <para>ANTI-VACUITY IS THE POINT OF <see cref="TheIndex_IsNotEmpty_AndCarriesTheKnownShippedGuides"/>.
/// This endpoint returns an empty list very cheaply - a corpus that failed to copy to the output
/// directory produces one, and so does a filter bug - and an "it did not error" assertion would go
/// green through both. So the index test pins a real minimum count AND names shipped ids, in both
/// languages. That exact copy-to-output failure has shipped in this codebase before.</para>
///
/// <para>Deterministic: no model, no network, no GPU, no database.</para>
/// </summary>
public class GuidesEndpointTests
{
    // ─── Harness ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A controller over the real shipped corpus, with a live request/response pair so the
    /// cache headers this contract promises are actually observable.</summary>
    private static GuidesController RealController(out CapturingLogger<GuidesController> logger)
        => ControllerOver(ProductChatCorpusTests.RealGuidesDirectory(), out logger);

    private static GuidesController RealController()
        => ControllerOver(ProductChatCorpusTests.RealGuidesDirectory(), out _);

    private static GuidesController ControllerOver(string directory, out CapturingLogger<GuidesController> logger)
    {
        logger = new CapturingLogger<GuidesController>();
        var reader = new GuidesCorpusReader(directory, ProductChatCorpusTests.NullLoggerFor<GuidesCorpusReader>());
        return new GuidesController(reader, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static GuideListResponseDto OkIndex(GuidesController controller, string? language = null)
    {
        var action = controller.List(language);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<GuideListResponseDto>(ok.Value);
    }

    private static GuideContentDto OkGuide(GuidesController controller, string id, string? language = null)
    {
        var action = controller.Get(id, language);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<GuideContentDto>(ok.Value);
    }

    // ─── The index ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE VACUITY GUARD. Proves the corpus behind the endpoint is genuinely populated, by count and
    /// by name, in both languages - not merely that the call did not throw.
    /// </summary>
    [Fact]
    public void TheIndex_IsNotEmpty_AndCarriesTheKnownShippedGuides()
    {
        var index = OkIndex(RealController());

        Assert.Null(index.Fault);
        Assert.Equal(index.Guides.Count, index.Count);
        // A real floor, not "> 0": seven bilingual stage guides plus the English-only index page is 15,
        // and anything materially below that means the corpus did not ship.
        Assert.True(index.Count >= 15,
            $"The guides index served only {index.Count} document(s). The shipped corpus is 7 guides x 2 " +
            "languages plus README.md. A count below that almost always means Content/guides did not reach " +
            "the output directory (Pagedraft.Api.csproj Content include), not that a guide was deleted.");

        // Named ids, so a filter that silently dropped a stage would fail here rather than pass on a count.
        foreach (var id in new[] { "workflow-overview", "import", "book-setup-and-intelligence",
                                   "chapter-editing-passes", "whole-book-review", "export", "faq" })
        {
            Assert.Contains(index.Guides, g => g.Id == id && g.Language == "en");
            Assert.Contains(index.Guides, g => g.Id == id && g.Language == "he");
        }

        Assert.Contains(index.Guides, g => g.Id == "guides-index" && g.Language == "en");
    }

    /// <summary>Every field the contract promises is present and non-empty on every row, so a client
    /// can render an index without fetching a single body.</summary>
    [Fact]
    public void EveryIndexRow_CarriesIdStageAudienceLanguageTitleUpdatedAndOrder()
    {
        var index = OkIndex(RealController());
        Assert.NotEmpty(index.Guides);

        foreach (var g in index.Guides)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Id), "an index row has no id");
            Assert.False(string.IsNullOrWhiteSpace(g.Stage), $"{g.Id}: no stage");
            Assert.False(string.IsNullOrWhiteSpace(g.Audience), $"{g.Id}: no audience");
            Assert.False(string.IsNullOrWhiteSpace(g.Title), $"{g.Id}: no title");
            Assert.False(string.IsNullOrWhiteSpace(g.Updated), $"{g.Id}: no updated stamp");
            Assert.True(g.Language is "he" or "en", $"{g.Id}: unexpected language '{g.Language}'");
        }
    }

    /// <summary>
    /// THE TITLE IS DERIVED FROM THE FIRST H1, because the frontmatter has no <c>title</c> key. Pinned
    /// on real documents in both languages: if this ever stops matching the guide's own H1, the help
    /// page is naming documents something they are not called.
    /// </summary>
    [Fact]
    public void TheTitle_IsTheGuidesOwnFirstH1_InBothLanguages()
    {
        var index = OkIndex(RealController());

        Assert.Equal("Questions the work raises",
            Assert.Single(index.Guides, g => g.Id == "faq" && g.Language == "en").Title);
        Assert.Equal("שאלות שהעבודה מעלה",
            Assert.Single(index.Guides, g => g.Id == "faq" && g.Language == "he").Title);
        Assert.Equal("Exporting your book",
            Assert.Single(index.Guides, g => g.Id == "export" && g.Language == "en").Title);
        Assert.Equal("ייצוא הספר",
            Assert.Single(index.Guides, g => g.Id == "export" && g.Language == "he").Title);
    }

    /// <summary>A document with no heading at all still gets a name. The fallback is the id, never an
    /// empty string, because an unnamed row in a list is unusable.</summary>
    [Fact]
    public void AGuideWithNoHeading_FallsBackToItsIdAsTheTitle()
    {
        using var dir = new TempGuides();
        dir.Write("10-plain.md", "---\nid: plain\nstage: s\nlang: en\n---\n\njust prose, no heading\n");

        var index = OkIndex(ControllerOver(dir.Path, out _));

        Assert.Equal("plain", Assert.Single(index.Guides).Title);
    }

    /// <summary>The language filter narrows to real siblings rather than to translations, and an
    /// unrecognized tag narrows to nothing instead of falling back to English.</summary>
    [Fact]
    public void TheIndex_CanBeNarrowedToOneLanguage()
    {
        var hebrew = OkIndex(RealController(), "he");
        Assert.NotEmpty(hebrew.Guides);
        Assert.All(hebrew.Guides, g => Assert.Equal("he", g.Language));
        Assert.Equal(7, hebrew.Count);   // README.md ships English-only

        var english = OkIndex(RealController(), "en-US");   // a browser locale still resolves
        Assert.NotEmpty(english.Guides);
        Assert.All(english.Guides, g => Assert.Equal("en", g.Language));
        Assert.Equal(8, english.Count);

        Assert.Empty(OkIndex(RealController(), "fr").Guides);
    }

    /// <summary>The index arrives in the corpus's authored workflow order, so a client never has to
    /// know the filenames to present the stages in sequence.</summary>
    [Fact]
    public void TheIndex_IsOrderedByTheGuidesOwnNumericPrefix()
    {
        var order = OkIndex(RealController(), "en").Guides.Select(g => g.Order).ToList();

        Assert.NotEmpty(order);
        Assert.Equal(order.OrderBy(o => o).ToList(), order);
        Assert.Equal(0, order[0]);                       // 00-workflow-overview
        Assert.Equal(int.MaxValue, order[^1]);           // README.md has no numeric prefix, so it sorts last
    }

    // ─── One guide ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGuidesContent_IsItsMarkdownBody_WithTheFrontmatterAlreadyStripped()
    {
        var guide = OkGuide(RealController(), "export", "en");

        Assert.Equal("export", guide.Id);
        Assert.Equal("en", guide.Language);
        Assert.Equal("Exporting your book", guide.Title);
        Assert.StartsWith("# Exporting your book", guide.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("id: export", guide.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("lang: en", guide.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// HEBREW IS THE SIBLING FILE, NOT A TRANSLATION. Asserted on the CONTENT rather than on a 200 and
    /// a language field: a server that answered every request with the English document and relabelled
    /// it would pass a status-code test and fail this one.
    /// </summary>
    [Fact]
    public void AskingForHebrew_ReturnsTheHeSiblingsOwnText_NotTheEnglishDocument()
    {
        var hebrew = OkGuide(RealController(), "faq", "he");
        var english = OkGuide(RealController(), "faq", "en");

        Assert.Equal("he", hebrew.Language);
        Assert.Equal("faq", hebrew.Id);
        Assert.Equal("שאלות שהעבודה מעלה", hebrew.Title);
        Assert.StartsWith("# שאלות שהעבודה מעלה", hebrew.Body, StringComparison.Ordinal);
        // A real Hebrew section heading from 90-faq.he.md, so the assertion is about the document's own
        // prose and not merely about the presence of Hebrew characters somewhere.
        Assert.Contains("## איך מריצים מעבר על פרק?", hebrew.Body, StringComparison.Ordinal);

        // ... and it is emphatically not the English body wearing a Hebrew label.
        Assert.DoesNotContain("Questions the work raises", hebrew.Body, StringComparison.Ordinal);
        Assert.NotEqual(english.Body, hebrew.Body);
        Assert.Contains("Questions the work raises", english.Body, StringComparison.Ordinal);
    }

    /// <summary>No language means the corpus's own default, which is English. Stated rather than
    /// guessed, because the client always sends one and a bare URL must still resolve.</summary>
    [Fact]
    public void OmittingTheLanguage_ServesEnglish()
        => Assert.Equal("en", OkGuide(RealController(), "faq").Language);

    [Fact]
    public void AnUnknownId_Is404_WithNoFilesystemDetail()
    {
        var controller = RealController(out var logger);

        var result = Assert.IsType<NotFoundObjectResult>(controller.Get("no-such-guide", "en").Result);
        var body = Assert.IsType<GuideNotFoundDto>(result.Value);

        Assert.Equal("guideNotFound", body.Error);
        Assert.Empty(body.AvailableLanguages);

        // Nothing about the disk goes out on the wire...
        var serialized = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.DoesNotContain("Content", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guides", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".md", serialized, StringComparison.OrdinalIgnoreCase);

        // ... and the miss is still observable to an operator, with the id and the language.
        Assert.Contains(logger.AtLeast(LogLevel.Warning),
            m => m.Contains("no-such-guide", StringComparison.Ordinal) && m.Contains("'en'", StringComparison.Ordinal));
    }

    /// <summary>
    /// PATH TRAVERSAL IS NOT REACHABLE, BY CONSTRUCTION. The id is looked up in the already-parsed
    /// in-memory corpus and is never joined to a path, so a traversal-shaped id is just a string that
    /// matches no document. Several shapes, including the encoded and the mixed-separator ones, all
    /// land on the same ordinary 404 with no file content anywhere in the response.
    /// </summary>
    [Theory]
    [InlineData("../../appsettings")]
    [InlineData("../../appsettings.json")]
    [InlineData("..%2F..%2Fappsettings.json")]
    [InlineData("....//....//appsettings.json")]
    [InlineData("..\\..\\Program")]
    [InlineData("/etc/passwd")]
    [InlineData("../README")]
    [InlineData("C:\\Windows\\win.ini")]
    public void ATraversalShapedId_Is404_NotAFile(string id)
    {
        var controller = RealController();

        var action = controller.Get(id, "en");

        // Not an Ok of any kind: no document, and therefore no body, came back.
        Assert.Null(action.Value);
        var result = Assert.IsType<NotFoundObjectResult>(action.Result);
        var body = Assert.IsType<GuideNotFoundDto>(result.Value);
        Assert.Equal("guideNotFound", body.Error);
    }

    /// <summary>
    /// An id that ships only in the other language is a DIFFERENT fact from an unknown id, and the
    /// reader can act on it ("read it in English") rather than showing a dead end. README.md is the
    /// one English-only document in the corpus, which is what makes this reachable at all.
    /// </summary>
    [Fact]
    public void AnIdThatExistsOnlyInAnotherLanguage_Says_So_AndNamesTheLanguagesItHas()
    {
        var result = Assert.IsType<NotFoundObjectResult>(RealController().Get("guides-index", "he").Result);
        var body = Assert.IsType<GuideNotFoundDto>(result.Value);

        Assert.Equal("guideLanguageUnavailable", body.Error);
        Assert.Equal(new[] { "en" }, body.AvailableLanguages);
    }

    // ─── Cache headers ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BothEndpoints_SendAStrongETagAndAPublicMaxAge()
    {
        var index = RealController();
        OkIndex(index);
        Assert.Equal($"public, max-age={GuidesController.CacheMaxAgeSeconds}",
            index.Response.Headers[HeaderNames.CacheControl].ToString());
        var indexTag = index.Response.Headers[HeaderNames.ETag].ToString();
        Assert.StartsWith("\"", indexTag, StringComparison.Ordinal);
        Assert.EndsWith("\"", indexTag, StringComparison.Ordinal);

        var content = RealController();
        OkGuide(content, "faq", "he");
        Assert.Equal($"public, max-age={GuidesController.CacheMaxAgeSeconds}",
            content.Response.Headers[HeaderNames.CacheControl].ToString());
        Assert.NotEqual(indexTag, content.Response.Headers[HeaderNames.ETag].ToString());
    }

    /// <summary>The validator is CONTENT-derived: two languages of the same guide are different
    /// documents and must not share an ETag, or a language switch would serve a 304 of the wrong text.</summary>
    [Fact]
    public void TheContentETag_DiffersPerLanguage()
    {
        var hebrew = RealController();
        OkGuide(hebrew, "faq", "he");
        var english = RealController();
        OkGuide(english, "faq", "en");

        Assert.NotEqual(hebrew.Response.Headers[HeaderNames.ETag].ToString(),
                        english.Response.Headers[HeaderNames.ETag].ToString());
    }

    [Fact]
    public void AMatchingIfNoneMatch_Gets304_AndStillCarriesTheValidator()
    {
        var first = RealController();
        OkGuide(first, "faq", "en");
        var etag = first.Response.Headers[HeaderNames.ETag].ToString();

        var second = RealController();
        second.Request.Headers[HeaderNames.IfNoneMatch] = etag;
        var action = second.Get("faq", "en");

        var status = Assert.IsType<StatusCodeResult>(action.Result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
        Assert.Equal(etag, second.Response.Headers[HeaderNames.ETag].ToString());

        // A stale validator is not a 304.
        var third = RealController();
        third.Request.Headers[HeaderNames.IfNoneMatch] = "\"0000000000000000\"";
        Assert.IsType<OkObjectResult>(third.Get("faq", "en").Result);
    }

    /// <summary>RFC 9110 13.1.2 specifies If-None-Match against the WEAK comparison function that
    /// 8.8.3.2 defines, so an
    /// intermediary is allowed to forward our strong tag weakened (a CDN that transforms the body,
    /// for instance) and a client replaying that weakened tag must still get a 304, not a full body.</summary>
    [Fact]
    public void AWeakenedIfNoneMatch_StillGets304()
    {
        var first = RealController();
        OkGuide(first, "faq", "en");
        var etag = first.Response.Headers[HeaderNames.ETag].ToString();

        var second = RealController();
        second.Request.Headers[HeaderNames.IfNoneMatch] = "W/" + etag;
        var action = second.Get("faq", "en");

        var status = Assert.IsType<StatusCodeResult>(action.Result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
        Assert.Equal(etag, second.Response.Headers[HeaderNames.ETag].ToString());

        // A weakened STALE validator is still not a 304: weak comparison must not degrade into
        // matching everything just because it strips the W/ prefix.
        var third = RealController();
        third.Request.Headers[HeaderNames.IfNoneMatch] = "W/\"0000000000000000\"";
        Assert.IsType<OkObjectResult>(third.Get("faq", "en").Result);
    }

    [Fact]
    public void TheIndexAlsoHonoursIfNoneMatch()
    {
        var first = RealController();
        OkIndex(first, "he");
        var etag = first.Response.Headers[HeaderNames.ETag].ToString();

        var second = RealController();
        second.Request.Headers[HeaderNames.IfNoneMatch] = etag;
        var status = Assert.IsType<StatusCodeResult>(second.List("he").Result);

        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    // ─── The corpus is unreachable ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A MISSING CORPUS IS A FAULT, NOT AN EMPTY INDEX. Same distinction the chat's grounding contract
    /// rests on: a help page that rendered an empty list because a deployment lost its Content folder
    /// would look like a product that ships no documentation.
    /// </summary>
    [Fact]
    public void AMissingCorpus_Is503WithAFaultCode_AndIsNotCached()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pagedraft-guides-missing-" + Guid.NewGuid().ToString("N"));
        var controller = ControllerOver(missing, out var logger);

        var result = Assert.IsType<ObjectResult>(controller.List(null).Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        var body = Assert.IsType<GuideListResponseDto>(result.Value);
        Assert.Equal(ProductChatFaults.GuidesUnavailable, body.Fault);
        Assert.Empty(body.Guides);
        Assert.Equal(0, body.Count);
        Assert.Equal("no-store", controller.Response.Headers[HeaderNames.CacheControl].ToString());
        Assert.NotEmpty(logger.AtLeast(LogLevel.Warning));

        var content = ControllerOver(missing, out _);
        var contentResult = Assert.IsType<ObjectResult>(content.Get("faq", "he").Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, contentResult.StatusCode);
        Assert.Equal("no-store", content.Response.Headers[HeaderNames.CacheControl].ToString());
    }

    [Fact]
    public void ADirectoryWithNothingParseable_Is503WithTheEmptyFault()
    {
        using var dir = new TempGuides();
        dir.Write("not-a-guide.md", "# no frontmatter here\n");

        var result = Assert.IsType<ObjectResult>(ControllerOver(dir.Path, out _).List(null).Result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(ProductChatFaults.GuidesEmpty, Assert.IsType<GuideListResponseDto>(result.Value).Fault);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGuides : IDisposable
    {
        public string Path { get; }

        public TempGuides()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagedraft-guides-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Write(string name, string text) => File.WriteAllText(System.IO.Path.Combine(Path, name), text);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
