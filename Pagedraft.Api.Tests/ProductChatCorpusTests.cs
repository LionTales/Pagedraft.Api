using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The guides corpus as the retrieval layer reads it (chatbot phase A, c1).
///
/// <para>Two populations on purpose. The REAL shipped corpus proves the frontmatter parser works on
/// the actual files (a parser pinned only against hand-written fixtures passes forever while the real
/// files drift), and TEMP fixtures drive the edge cases the real corpus deliberately does not
/// contain: a missing directory, an empty one, a file with no frontmatter, a <c>lang</c> that
/// disagrees with its filename.</para>
///
/// <para>ANTI-VACUITY IS EXPLICIT HERE. Every "no document is bad" assertion below is preceded by an
/// assertion that the population is non-empty, and the real-corpus facts pin the EXACT file count.
/// <see cref="GuidesCorpusReader"/> returns an empty collection on a missing directory by design, so
/// a fixture-loading regression (a moved corpus, a broken csproj copy) would otherwise green every
/// one of these tests while proving nothing. That exact failure has shipped in this codebase before.</para>
///
/// <para>Deterministic: no model, no network, no GPU.</para>
/// </summary>
public class ProductChatCorpusTests
{
    /// <summary>The corpus as it ships today. A change to this number is a corpus change and should be
    /// a deliberate edit here, not a silent one.</summary>
    private const int ShippedGuideCount = 15;

    // ─── The real shipped corpus ────────────────────────────────────────────────────────────────

    internal static string RealGuidesDirectory()
    {
        var readme = FindUpward(Path.Combine("Pagedraft.Api", "Content", "guides", "README.md"));
        return Path.GetDirectoryName(readme)!;
    }

    internal static GuidesCorpus LoadRealCorpus()
    {
        var corpus = new GuidesCorpusReader(RealGuidesDirectory(), NullLoggerFor<GuidesCorpusReader>()).Load();
        Assert.Null(corpus.Fault);
        Assert.True(corpus.Documents.Count == ShippedGuideCount,
            $"Expected {ShippedGuideCount} shipped guides, loaded {corpus.Documents.Count} from " +
            $"{corpus.ResolvedDirectory} ({corpus.UnparseableFileCount} rejected). Every other assertion in " +
            "this file is only meaningful against the real corpus, so this count is checked FIRST.");
        return corpus;
    }

    [Fact]
    public void TheShippedCorpus_LoadsEveryFile_WithNothingRejected()
    {
        var corpus = LoadRealCorpus();

        Assert.Equal(0, corpus.UnparseableFileCount);
        Assert.True(corpus.CanGround);
    }

    /// <summary>
    /// Frontmatter parsing against the real files. The population is pinned by
    /// <see cref="LoadRealCorpus"/> before anything is asserted ABOUT it.
    /// </summary>
    [Fact]
    public void EveryShippedGuide_HasAnId_ALanguage_AndABodyWithoutItsFrontmatter()
    {
        var documents = LoadRealCorpus().Documents;

        foreach (var doc in documents)
        {
            Assert.False(string.IsNullOrWhiteSpace(doc.Id), $"{doc.FileName}: empty id.");
            Assert.True(doc.Lang is "he" or "en", $"{doc.FileName}: unexpected lang '{doc.Lang}'.");
            Assert.False(string.IsNullOrWhiteSpace(doc.Body), $"{doc.FileName}: empty body.");
            // The body is the file MINUS the frontmatter block: the fence and the keys must be gone,
            // or the model would be handed the metadata as if it were guide prose.
            Assert.DoesNotContain("---", doc.Body.Split('\n').FirstOrDefault() ?? "", StringComparison.Ordinal);
            Assert.False(doc.Body.StartsWith("id:", StringComparison.Ordinal), $"{doc.FileName}: frontmatter leaked into the body.");
            Assert.NotEmpty(doc.Headings);
        }
    }

    /// <summary>
    /// The exact frontmatter fields the todo names, read off a real file rather than a fixture.
    /// </summary>
    [Fact]
    public void AShippedGuide_ParsesItsIdStageAudienceUpdatedAndLang()
    {
        var doc = Assert.Single(LoadRealCorpus().Documents, d => d.FileName == "50-export.md");

        Assert.Equal("export", doc.Id);
        Assert.Equal("export", doc.Stage);
        Assert.Equal("author", doc.Audience);
        // Wave 3 / w4 rewrote this guide's availability section when the export SCREEN shipped, and moved
        // the stamp with it. The field is asserted for its FORMAT and its freshness relative to the file it
        // describes; a guide edit that leaves this date behind is the drift worth catching.
        Assert.Equal("2026-08-09", doc.Updated);
        Assert.Equal("en", doc.Lang);
        Assert.Equal(50, doc.NumericPrefix);
        Assert.Contains("Exporting your book", doc.Headings);
    }

    /// <summary>
    /// THE CITATION IS LANGUAGE-NEUTRAL. Both halves of an en/he pair carry the SAME frontmatter id,
    /// which is what lets a Hebrew answer grounded in an English guide cite the same id an English
    /// answer would. If the corpus ever gave the Hebrew sibling its own id, every citation would
    /// silently become language-dependent.
    /// </summary>
    [Fact]
    public void EveryHebrewGuide_SharesItsIdWithAnEnglishTwin_ExceptTheIndexWhichHasNone()
    {
        var documents = LoadRealCorpus().Documents;
        var english = documents.Where(d => d.Lang == "en").ToList();
        var hebrew = documents.Where(d => d.Lang == "he").ToList();

        Assert.Equal(8, english.Count);   // 7 stage guides + README
        Assert.Equal(7, hebrew.Count);    // README has no Hebrew sibling

        var englishIds = english.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var he in hebrew)
            Assert.Contains(he.Id, englishIds);

        // README is the English-only document the selector must handle without treating it as a
        // parse error. Named explicitly so this stays true rather than merely happening to be true.
        Assert.Contains(english, d => d.Id == "guides-index" && d.FileName == "README.md");
        Assert.DoesNotContain(hebrew, d => d.Id == "guides-index");
        Assert.Equal(int.MaxValue, GuideFrontmatter.NumericPrefixOf("README.md"));
    }

    /// <summary>
    /// THE CROSS-REPO MIRROR, PINNED ON THE SIDE THAT CAN ACTUALLY MOVE.
    ///
    /// <para>The client renders a citation chip with the guide's own TITLE rather than its slug, from
    /// a hand-maintained map in the OTHER repo
    /// (<c>pagedraft-client/src/app/core/i18n/chat-strings.ts</c>, <c>GUIDE_TITLES_HE</c> /
    /// <c>GUIDE_TITLES_EN</c>), whose docstring claims the titles are the H1 each guide file actually
    /// carries. That claim has already gone stale once: e1's copy-edit renamed two H1s and the client
    /// map kept naming the retired stage, so the chip cited a document by a title the document no
    /// longer had.</para>
    ///
    /// <para>The client's own spec pins its map against a hardcoded COPY of these H1s, which catches
    /// an edit to the map but cannot catch a rename HERE - the two repos ship on separate PRs and no
    /// client test can read this directory. This test is the missing half, and it is deliberately on
    /// this side: renaming a guide's H1 is the edit that causes the drift, so the failure must land in
    /// the change that causes it. If you are here because this test failed, you renamed a title; update
    /// BOTH maps in the client repo and the pin in its <c>chat-strings.spec.ts</c> in the same
    /// shipment.</para>
    ///
    /// <para>Hebrew note: five of the <c>.he.md</c> files open with a "# DRAFT Hebrew stage
    /// vocabulary..." banner INSIDE the frontmatter fence, so it is not part of the body and never
    /// reaches <c>Headings</c>. The first heading below is the real title.</para>
    /// </summary>
    [Fact]
    public void EveryShippedGuidesFirstH1_IsWhatTheClientsCitationTitleMapMirrors()
    {
        var documents = LoadRealCorpus().Documents;

        // filename -> the H1 the client's GUIDE_TITLES_* map must carry for this document's id+lang.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["00-workflow-overview.md"]              = "How the work flows",
            ["00-workflow-overview.he.md"]           = "איך העבודה מתקדמת",
            ["10-import.md"]                         = "Importing your manuscript",
            ["10-import.he.md"]                      = "ייבוא כתב היד",
            ["20-book-setup-and-intelligence.md"]    = "What PageDraft knows about your book",
            ["20-book-setup-and-intelligence.he.md"] = "מה PageDraft יודע על הספר שלכם",
            ["30-chapter-editing-passes.md"]         = "The chapter editing passes",
            ["30-chapter-editing-passes.he.md"]      = "מעברי העריכה על פרק",
            ["40-whole-book-review.md"]              = "The developmental review",
            ["40-whole-book-review.he.md"]           = "העריכה ההתפתחותית",
            ["50-export.md"]                         = "Exporting your book",
            ["50-export.he.md"]                      = "ייצוא הספר",
            ["90-faq.md"]                            = "Questions the work raises",
            ["90-faq.he.md"]                         = "שאלות שהעבודה מעלה",
            ["README.md"]                            = "PageDraft guides",
        };

        // Non-vacuity: this table has to cover the whole shipped corpus, not whichever files happen
        // to still be here, or a deleted guide would silently stop being pinned.
        Assert.Equal(ShippedGuideCount, expected.Count);
        Assert.Equal(
            documents.Select(d => d.FileName).OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            expected.Keys.OrderBy(f => f, StringComparer.Ordinal).ToArray());

        foreach (var doc in documents)
        {
            Assert.True(doc.Headings.Count > 0, $"{doc.FileName}: no headings, so it has no title to mirror.");
            Assert.True(expected[doc.FileName] == doc.Headings[0],
                $"{doc.FileName}: its first H1 is now '{doc.Headings[0]}', pinned here as " +
                $"'{expected[doc.FileName]}'. A guide title changed. The client repo renders this title " +
                "on the citation chip from its own copy in " +
                "pagedraft-client/src/app/core/i18n/chat-strings.ts (GUIDE_TITLES_HE / GUIDE_TITLES_EN) " +
                "and pins it in chat-strings.spec.ts; update both there and the line above here, in the " +
                "same shipment, or the chip will cite this guide by a title it no longer carries.");
        }
    }

    // ─── Frontmatter edge cases, on fixtures ────────────────────────────────────────────────────

    [Fact]
    public void AFileWithNoFrontmatterFence_IsRejectedWithAReason_NotSilentlyAccepted()
    {
        var (doc, reason) = GuideFrontmatter.Parse("broken.md", "# Just a heading\n\nSome prose.");

        Assert.Null(doc);
        Assert.Contains("fence", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileWithNoClosingFence_IsRejected()
    {
        var (doc, reason) = GuideFrontmatter.Parse("broken.md", "---\nid: x\nlang: en\n\n# Heading");

        Assert.Null(doc);
        Assert.Contains("closing", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The id is the CITATION the contract promises, so a document that cannot be cited must not be
    /// able to reach an answer. Everything else degrades instead.
    /// </summary>
    [Fact]
    public void AFileWithNoId_IsRejected_ButAFileMissingTheOptionalFieldsIsNot()
    {
        var (noId, reason) = GuideFrontmatter.Parse("x.md", "---\nstage: s\nlang: en\n---\n\n# H\n");
        Assert.Null(noId);
        Assert.Contains("id", reason, StringComparison.OrdinalIgnoreCase);

        var (sparse, _) = GuideFrontmatter.Parse("x.md", "---\nid: only-id\n---\n\n# H\n\nbody\n");
        Assert.NotNull(sparse);
        Assert.Equal("only-id", sparse!.Id);
        Assert.Equal("", sparse.Stage);
        Assert.Equal("en", sparse.Lang);   // no lang and no .he.md suffix, so the filename fallback wins
    }

    /// <summary>
    /// THE AUTHORITATIVE SIGNAL IS <c>lang</c>, the filename is a cross-check (d1 item 1). Pinned in
    /// the direction that matters: when the two disagree the frontmatter wins.
    /// </summary>
    [Fact]
    public void TheFrontmatterLang_OutranksTheFilenameSuffix_WhenTheyDisagree()
    {
        var (heByName, _) = GuideFrontmatter.Parse("00-x.he.md", "---\nid: x\nlang: en\n---\n\n# H\n\nbody\n");
        Assert.Equal("en", heByName!.Lang);

        var (enByName, _) = GuideFrontmatter.Parse("00-x.md", "---\nid: x\nlang: he\n---\n\n# H\n\nbody\n");
        Assert.Equal("he", enByName!.Lang);

        // ... and the filename is still the fallback when the authoritative field is absent.
        var (noLang, _) = GuideFrontmatter.Parse("00-x.he.md", "---\nid: x\n---\n\n# H\n\nbody\n");
        Assert.Equal("he", noLang!.Lang);
    }

    [Fact]
    public void OnlyH1AndH2_CountAsHeadings()
    {
        var (doc, _) = GuideFrontmatter.Parse(
            "x.md", "---\nid: x\nlang: en\n---\n\n# Title\n\n## Section\n\n### Deep\n\nprose # not a heading\n");

        Assert.Equal(new[] { "Title", "Section" }, doc!.Headings);
    }

    // ─── The reader's own fail states ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE FAIL-SAFE PRECONDITION. A missing directory must produce a FAULT, not merely an empty
    /// list: an empty list reads exactly like "a corpus with nothing relevant in it", and the whole
    /// point of the refusal path is that the two are different.
    /// </summary>
    [Fact]
    public void AMissingDirectory_FaultsAsGuidesUnavailable_AndLogsWhy()
    {
        var logger = new CapturingLogger<GuidesCorpusReader>();
        var missing = Path.Combine(Path.GetTempPath(), "pagedraft-guides-missing-" + Guid.NewGuid().ToString("N"));

        var corpus = new GuidesCorpusReader(missing, logger).Load();

        Assert.Equal(ProductChatFaults.GuidesUnavailable, corpus.Fault);
        Assert.False(corpus.CanGround);
        Assert.Empty(corpus.Documents);
        var warning = Assert.Single(logger.AtLeast(LogLevel.Warning));
        Assert.Contains(missing, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryWithNoParseableFile_FaultsAsGuidesEmpty_AndNamesTheRejectedFile()
    {
        using var dir = new TempCorpus();
        dir.Write("not-a-guide.md", "# no frontmatter here\n");

        var logger = new CapturingLogger<GuidesCorpusReader>();
        var corpus = new GuidesCorpusReader(dir.Path, logger).Load();

        Assert.Equal(ProductChatFaults.GuidesEmpty, corpus.Fault);
        Assert.Equal(1, corpus.UnparseableFileCount);
        Assert.Contains(logger.AtLeast(LogLevel.Warning), m => m.Contains("not-a-guide.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// A PARTIAL load still answers, but it must not do so silently: a corpus that quietly dropped a
    /// guide answers confidently while missing content, which is the shape of failure this feature is
    /// least able to afford.
    /// </summary>
    [Fact]
    public void APartiallyParseableDirectory_LoadsWhatItCan_AndWarnsAboutWhatItDropped()
    {
        using var dir = new TempCorpus();
        dir.Write("10-good.md", "---\nid: good\nlang: en\n---\n\n# Good\n\nbody\n");
        dir.Write("20-bad.md", "no frontmatter\n");

        var logger = new CapturingLogger<GuidesCorpusReader>();
        var corpus = new GuidesCorpusReader(dir.Path, logger).Load();

        Assert.Null(corpus.Fault);
        Assert.True(corpus.CanGround);
        Assert.Single(corpus.Documents);
        Assert.Equal(1, corpus.UnparseableFileCount);
        Assert.Contains(logger.AtLeast(LogLevel.Warning), m => m.Contains("20-bad.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ALangThatDisagreesWithTheFilename_IsLoadedAnyway_ButWarnedAbout()
    {
        using var dir = new TempCorpus();
        dir.Write("10-mismatch.he.md", "---\nid: mismatch\nlang: en\n---\n\n# H\n\nbody\n");

        var logger = new CapturingLogger<GuidesCorpusReader>();
        var corpus = new GuidesCorpusReader(dir.Path, logger).Load();

        Assert.Single(corpus.Documents);
        Assert.Equal("en", corpus.Documents[0].Lang);
        Assert.Contains(logger.AtLeast(LogLevel.Warning), m => m.Contains("10-mismatch.he.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// A successful load is cached (the guides cannot change while the process runs); a FAULTED one is
    /// not, so a deployment that is fixed recovers without a restart.
    /// </summary>
    [Fact]
    public void ASuccessfulLoadIsCached_AndAFaultedOneIsRetried()
    {
        using var dir = new TempCorpus();
        var reader = new GuidesCorpusReader(dir.Path, NullLoggerFor<GuidesCorpusReader>());

        Assert.Equal(ProductChatFaults.GuidesEmpty, reader.Load().Fault);

        // The fault was not cached: writing a guide makes the very next call succeed.
        dir.Write("10-good.md", "---\nid: good\nlang: en\n---\n\n# Good\n\nbody\n");
        Assert.True(reader.Load().CanGround);

        // And the success IS cached: deleting the file does not un-ground an already-loaded corpus.
        File.Delete(Path.Combine(dir.Path, "10-good.md"));
        Assert.True(reader.Load().CanGround);
    }

    // ─── The shipping contract ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE PUBLISH CONTRACT, pinned where it is cheap. The reader resolves
    /// <c>{ContentRootPath}/Content/guides</c>, which only works from a <c>dotnet publish</c> output
    /// because the csproj copies the corpus there. Nothing in the C# says so, and <c>.md</c> is not a
    /// default Web SDK content item, so deleting that one line would leave every test in this file
    /// green (they read the SOURCE tree) while a published deployment refused every question.
    /// </summary>
    [Fact]
    public void TheCsproj_StillShipsTheGuidesToTheOutputAndPublishDirectories()
    {
        var csproj = File.ReadAllText(FindUpward(Path.Combine("Pagedraft.Api", "Pagedraft.Api.csproj")));

        var include = Assert.Single(
            csproj.Split('\n'),
            l => l.Contains("Content\\guides", StringComparison.Ordinal) && l.Contains("<Content", StringComparison.Ordinal));

        Assert.Contains("CopyToOutputDirectory=\"PreserveNewest\"", include, StringComparison.Ordinal);
        Assert.Contains("CopyToPublishDirectory=\"PreserveNewest\"", include, StringComparison.Ordinal);
        Assert.Contains("*.md", include, StringComparison.Ordinal);
    }

    /// <summary>The directory the production constructor resolves is the one the csproj ships to,
    /// relative to the content root and nowhere else. No test-only path, no upward search.</summary>
    [Fact]
    public void TheProductionDirectory_IsContentGuides_UnderTheContentRoot()
        => Assert.Equal("Content/guides", GuidesCorpusReader.RelativeDirectory);

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────

    internal static ILogger<T> NullLoggerFor<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    internal static string FindUpward(string relativeSubPath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeSubPath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate " + relativeSubPath + " above " + AppContext.BaseDirectory);
    }

    private sealed class TempCorpus : IDisposable
    {
        public string Path { get; }

        public TempCorpus()
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

/// <summary>
/// A minimal capturing <see cref="ILogger{T}"/>. The product-chat fail-safe paths are required to be
/// OBSERVABLE, so "it logged why" is an assertion here rather than a hope.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
    }

    public IReadOnlyList<string> AtLeast(LogLevel level)
    {
        lock (_entries) return _entries.Where(e => e.Level >= level).Select(e => e.Message).ToList();
    }

    public IReadOnlyList<string> At(LogLevel level)
    {
        lock (_entries) return _entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
    }
}
