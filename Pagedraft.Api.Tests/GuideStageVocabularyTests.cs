using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE OVERVIEW GUIDE'S FIVE-STAGE LIST AND THE STAGE SPINE MUST NAME THE SAME STAGE THE SAME WAY.
///
/// <para>WHY THIS FILE EXISTS. The first-run orientation overlay's body is not copy - it is a real section of
/// the shipped <c>workflow-overview</c> guide, fetched through the guides endpoint and rendered inside the
/// book dashboard (<c>first-run-orientation.component.ts</c>). The stage spine renders directly beneath it.
/// So the guide's numbered stage list and the spine's stage names are TWO RENDERINGS OF ONE VOCABULARY IN ONE
/// VIEWPORT, and the w8 live-browser gate caught them disagreeing: the overlay said
/// <c>4. **מעברי עריכה על פרק.**</c> while the spine, three lines down, said <c>עריכת פרק</c>. Same screen,
/// same book, two names for stage 4. English was already consistent, which is what made the Hebrew half look
/// like a translation choice rather than the defect it was.</para>
///
/// <para>WHAT IS ASSERTED, AND WHAT IS NOT. This pins the whole list, in both languages, against the canonical
/// names below - not the one line that was wrong. A test written against stage 4 alone would be walked past by
/// the next rename, which is the failure mode that produced this one.</para>
///
/// <para>THE CANONICAL NAMES ARE A CROSS-REPO MIRROR whose ONE source of truth is the client's
/// <c>pagedraft-client/src/app/shared/stage-spine/stage-spine.copy.ts</c> (<c>STAGE_NAMES</c>), which is
/// owner-dictated and native-swept (2026-08-11). Two tests below read that table, and they cover different
/// halves of the join:</para>
/// <list type="bullet">
/// <item><see cref="TheOverviewGuidesStageList_NamesEveryStageExactlyAsTheSpineDoes"/> compares the shipped
/// guide - which lives in THIS repo and is the thing that drifted - against the mirror.</item>
/// <item><see cref="TheCanonicalMirror_StillMatchesTheClientsStageNames"/> compares the mirror against the
/// client's own file, READ AS DATA off disk when the two repos are checked out as siblings. That is what
/// closes a coordinated rename in the client (its copy file and <c>guides-strings.ts</c> moved together, so
/// its own <c>stage-label-agreement.spec.ts</c> stays green) and it has a stated limit: see that test.</item>
/// </list>
///
/// <para>The client half of the same join is <c>stage-label-agreement.spec.ts</c>, which pins the spine's names
/// against the <c>/help</c> index's stage-group headings (the third and fourth Hebrew renderings the gate
/// found). Deterministic: no model, no network, no GPU.</para>
/// </summary>
public class GuideStageVocabularyTests
{
    private readonly ITestOutputHelper _output;

    public GuideStageVocabularyTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The exact path, from the directory that holds both repos, of the file this table mirrors. Spelled once
    /// and quoted into every failure message here, so a reader who lands on one never has to guess which file
    /// in the other repo the assertion is about.
    /// </summary>
    private const string ClientStageNamesPath = "pagedraft-client/src/app/shared/stage-spine/stage-spine.copy.ts";

    /// <summary>One stage, by the spine's own id, in both languages.</summary>
    private sealed record CanonicalStage(string Id, string He, string En)
    {
        public string NameIn(string lang) => lang == "he" ? He : En;
    }

    /// <summary>
    /// The five stage names as the spine renders them, in canonical order, keyed by the spine's stage id.
    ///
    /// <para>PROVENANCE: a MIRROR of <c>STAGE_NAMES</c> in <c>pagedraft-client/src/app/shared/stage-spine/
    /// stage-spine.copy.ts</c>, owner-dictated and native-swept on 2026-08-11. That file is the source; this
    /// is a copy the API side can compile against. Never edit this table to make a test pass - either the
    /// guide in this repo drifted (fix the guide) or the client renamed a stage (change it there first, then
    /// here, in the same shipment).</para>
    /// </summary>
    private static readonly IReadOnlyList<CanonicalStage> CanonicalStages = new[]
    {
        new CanonicalStage("import",         "ייבוא",            "Import"),
        new CanonicalStage("briefs",         "תקצירי ספר",       "Book briefs"),
        new CanonicalStage("review",         "עריכה התפתחותית",  "Developmental review"),
        new CanonicalStage("chapter-passes", "עריכת פרק",        "Chapter editing passes"),
        new CanonicalStage("export",         "ייצוא",            "Export"),
    };

    /// <summary>
    /// One item of the overview guide's numbered stage list: <c>N. **Name.** prose</c>. The name is the bold
    /// lead; the trailing full stop belongs to the sentence, not to the name.
    /// </summary>
    private static readonly Regex StageListItem = new(@"^\s*(\d)\.\s+\*\*(.+?)\.?\*\*", RegexOptions.Multiline);

    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public void TheOverviewGuidesStageList_NamesEveryStageExactlyAsTheSpineDoes(string lang)
    {
        var names = OverviewStageNames(lang);

        // Anti-vacuity: an empty or short list would green every comparison below without proving anything,
        // and this corpus is loaded off disk by a csproj content glob that has silently emptied before.
        Assert.Equal(CanonicalStages.Count, names.Count);

        for (var i = 0; i < CanonicalStages.Count; i++)
        {
            var canonical = CanonicalStages[i].NameIn(lang);
            Assert.True(canonical == names[i],
                $"The {lang} workflow-overview guide names stage {i + 1} ('{CanonicalStages[i].Id}') " +
                $"'{names[i]}', but the stage spine renders it '{canonical}'. These two are drawn in ONE " +
                "viewport (the first-run overlay sits directly above the spine), so a difference here is two " +
                "names for one stage on one screen. Change the guide, or change the canonical mirror in this " +
                $"file together with STAGE_NAMES in {ClientStageNamesPath} - never only one of the three.");
        }
    }

    /// <summary>
    /// THE MIRROR, CHECKED AGAINST THE FILE IT MIRRORS - the half no process used to hold.
    ///
    /// <para>WHAT THIS CLOSES. Renaming a stage in the client's <c>STAGE_NAMES</c> AND in its
    /// <c>guides-strings.ts</c> <c>STAGE_LABELS_*</c> together leaves the client's own
    /// <c>stage-label-agreement.spec.ts</c> green (it compares those two files to each OTHER, and both moved)
    /// while the table above and the guide it checks both stay put - so before this test the first-run overlay
    /// and the app disagreed on screen again with nothing red in either repo. That is the w8 / F1 defect
    /// reproduced against a green suite on both sides, and the live-browser gate was the only thing that could
    /// see it.</para>
    ///
    /// <para>WHY READING THE FILE IS SAFE ENOUGH. It is read as DATA, not compiled: one regex over an object
    /// literal whose shape is pinned by the parse itself. If the file is present but does not yield exactly
    /// the five expected stage ids, this FAILS rather than passing - a parse that silently returned nothing
    /// would be the vacuity this test exists to prevent, and a client refactor that reshapes the table is
    /// exactly the change that should force someone to look at this join.</para>
    ///
    /// <para>THE LIMIT, STATED PLAINLY, IN THREE PARTS. (1) No test process can read both repos, so this is a
    /// DEVELOPER MACHINE pin, not a CI pin: it needs the two repos checked out as siblings under one parent
    /// directory, and the API's own CI (<c>.github/workflows/ci.yml</c>) clones this repo alone. (2) When the
    /// client REPO is absent this test PASSES, after writing the reason to the test output - xUnit v2 has no
    /// dynamic skip and this suite is not going to grow a package dependency for one test, so a reader who
    /// wants to know whether the pin actually looked has to read that line. It does NOT pass when the repo is
    /// present and only the FILE is missing: that is a moved target rather than an absent checkout, and it
    /// would otherwise turn the pin into a no-op that greens forever on the only machines it runs on.
    /// <c>TheClientStageNamesParser_ReadsTheShapeItExpects</c> is what keeps the reading half honest in the
    /// runs where the file is absent.
    /// (3) The client's own CI cannot see this file at all, so a client-only PR renaming a stage in both
    /// client files still goes green there; it turns red the first time anyone runs the API suite with both
    /// repos on disk. That is later than the change that causes it, and loud and named when it arrives, which
    /// is strictly more than the nothing it replaces.</para>
    /// </summary>
    [Fact]
    public void TheCanonicalMirror_StillMatchesTheClientsStageNames()
    {
        var path = FindClientStageNamesFile();
        if (path == null)
        {
            // final-r01: an absent sibling REPO is the expected CI shape and skips. An absent FILE inside a
            // present repo is a moved target, and a pin whose target moved is a pin that passes forever
            // without looking - so that fails here rather than skipping.
            var clientRoot = FindClientRepoRoot();
            Assert.True(clientRoot == null,
                $"The client repo IS checked out beside this one ({clientRoot}), but " +
                $"{ClientStageNamesPath} is not there. This pin has stopped comparing anything: either the " +
                "client moved or renamed STAGE_NAMES' file (point ClientStageNamesPath at its new home) or " +
                "that constant has a typo. Skipping here would make the whole cross-repo mirror a silent " +
                "no-op on every machine that has both repos, which is the only place it ever runs.");

            _output.WriteLine(
                $"SKIPPED, AND THE MIRROR IS THEREFORE UNVERIFIED IN THIS RUN: {ClientStageNamesPath} is not " +
                "on disk, so the client repo is not checked out beside this one. Expected in CI, which clones " +
                "the API repo alone; on a developer machine the two repos sit under one parent directory and " +
                "this test does the real comparison. xUnit v2 cannot report a dynamic skip, so this run " +
                "counts as a pass.");
            return;
        }

        _output.WriteLine($"Comparing the canonical mirror against {path}.");
        var client = ParseClientStageNames(File.ReadAllText(path));

        // NON-VACUITY FIRST, both directions: a regex that matched nothing, or a table that grew or lost a
        // stage, must fail here rather than green every per-stage comparison below.
        var expectedIds = CanonicalStages.Select(s => s.Id).ToList();
        var parsedIds = client.Keys.ToList();
        Assert.True(
            expectedIds.OrderBy(i => i, StringComparer.Ordinal).SequenceEqual(parsedIds.OrderBy(i => i, StringComparer.Ordinal)),
            $"STAGE_NAMES in {path} no longer holds exactly the stages this file mirrors. " +
            $"Mirrored here: [{string.Join(", ", expectedIds)}]. Read from that file: " +
            $"[{string.Join(", ", parsedIds)}]. Either a stage was added or removed in the client (mirror it " +
            "here, and add it to the workflow-overview guide's numbered list in BOTH languages), or the " +
            "literal's shape changed and the regex in ParseClientStageNames needs updating.");

        foreach (var stage in CanonicalStages)
        {
            var (he, en) = client[stage.Id];
            Assert.True(stage.He == he,
                $"Stage '{stage.Id}' is called '{he}' in STAGE_NAMES ({ClientStageNamesPath}) but is " +
                $"mirrored here as '{stage.He}'. The client renamed a stage. The Hebrew first-run overlay " +
                "renders the workflow-overview guide's stage list directly above the spine, so until that " +
                "guide and this table move too, one screen shows two names for one stage - the w8 / F1 " +
                "defect. Update this table AND Pagedraft.Api/Content/guides/00-workflow-overview.he.md.");
            Assert.True(stage.En == en,
                $"Stage '{stage.Id}' is called '{en}' in STAGE_NAMES ({ClientStageNamesPath}) but is " +
                $"mirrored here as '{stage.En}'. The client renamed a stage. Update this table AND " +
                "Pagedraft.Api/Content/guides/00-workflow-overview.md, or the first-run overlay and the " +
                "spine beneath it will name one stage two ways on one screen.");
        }
    }

    /// <summary>
    /// THE READING HALF, PINNED WHERE THE REAL FILE IS NOT REACHABLE.
    ///
    /// <para>The mirror test above passes silently when the client repo is absent, which is every CI run, so
    /// the regexes it depends on would otherwise be exercised only on developer machines and could rot in a
    /// branch for as long as nobody looked. This runs everywhere. It pins the parser's CONTRACT - the literal
    /// shape it reads, the block boundary that keeps it out of the file's other stage-keyed maps, and a loud
    /// failure when the declaration is missing - against a sample, and deliberately holds no stage NAMES: the
    /// names have exactly one mirror in this file and a second copy here would be one more thing to drift.</para>
    /// </summary>
    [Fact]
    public void TheClientStageNamesParser_ReadsTheShapeItExpects()
    {
        // The literal's real shape, followed by the decoy: stage-spine.copy.ts holds several maps keyed by
        // the same stage ids, so an unbounded entry regex would read STAGE_EXPLANATION's text as a name.
        const string sample = """
            export const STAGE_NAMES: Record<SpineStageId, Bi> = {
              'import': { he: 'ייבוא', en: 'Import' },
              'chapter-passes': { he: 'עריכת פרק', en: 'Chapter editing passes' },
            };

            export const STAGE_EXPLANATION: Record<SpineStageId, Bi> = {
              'import': { he: 'לא שם של שלב', en: 'Not a stage name' },
            };
            """;

        var parsed = ParseClientStageNames(sample);

        Assert.Equal(new[] { "chapter-passes", "import" }, parsed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(("ייבוא", "Import"), parsed["import"]);
        Assert.Equal(("עריכת פרק", "Chapter editing passes"), parsed["chapter-passes"]);

        // A file that no longer declares the table must FAIL, not parse to an empty dictionary that would
        // green the mirror comparison by having nothing to compare.
        var missing = Record.Exception(() => ParseClientStageNames("export const SOMETHING_ELSE = { 'import': { he: 'x', en: 'y' } };"));
        Assert.NotNull(missing);
        Assert.Contains("STAGE_NAMES", missing!.Message, StringComparison.Ordinal);

        // And a block boundary that ran past the literal must fail by NAME, not as a dictionary's duplicate-key
        // ArgumentException, because the reader has to be told which side is wrong.
        var overrun = Record.Exception(() => ParseClientStageNames(sample.Replace("};", "}", StringComparison.Ordinal) + "\n};"));
        Assert.NotNull(overrun);
        Assert.Contains("same stage id twice", overrun!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bold lead of each item in the shipped overview guide's numbered stage list, in list order.
    ///
    /// <para>SOURCE, STATED HONESTLY: the guide files in this repo's SOURCE tree
    /// (<c>ProductChatCorpusTests.RealGuidesDirectory</c> walks upward to find them), read through
    /// <c>GuidesCorpusReader</c> - the same reader and the same frontmatter/body split the server uses, but
    /// NOT the copy the csproj content glob ships to the output directory. So this cannot go red if that glob
    /// stops copying; <c>ProductChatCorpusTests.TheCsproj_StillShipsTheGuidesToTheOutputAndPublishDirectories</c>
    /// is the test that covers the glob, and <c>ProductChatCorpusTests.TheProductionDirectory_IsContentGuides_
    /// UnderTheContentRoot</c> pins the path the production constructor resolves.</para>
    /// </summary>
    private static IReadOnlyList<string> OverviewStageNames(string lang)
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        var overview = corpus.Documents.Single(d => d.Id == "workflow-overview" && d.Lang == lang);

        return StageListItem.Matches(overview.Body)
            .Select(m => (Order: int.Parse(m.Groups[1].Value), Name: m.Groups[2].Value.Trim()))
            // The guide has a second numbered list (the practical working order), whose items are plain
            // sentences with no bold lead, so the regex does not reach it. Ordering by the item's own number
            // keeps this reading the STAGE list even if a later section grows a bold-led list of its own.
            .Where(x => x.Order is >= 1 and <= 5)
            .OrderBy(x => x.Order)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>
    /// <c>'stage-id': { he: '...', en: '...' },</c> - one entry of the client's <c>STAGE_NAMES</c> literal.
    /// Quotes are single in that file (a convention, held by .editorconfig's <c>quote_type = single</c> and
    /// by nothing executable - the client repo runs no linter) and no stage name contains one. If that ever
    /// changes, <see cref="TheClientStageNamesParser_ReadsTheShapeItExpects"/> is what fails, loudly.
    /// </summary>
    private static readonly Regex ClientStageEntry = new(
        @"'(?<id>[a-z-]+)'\s*:\s*\{\s*he:\s*'(?<he>[^']*)'\s*,\s*en:\s*'(?<en>[^']*)'\s*\}");

    /// <summary>
    /// The <c>STAGE_NAMES</c> object literal, from its declaration to the first closing <c>};</c>. Bounded so
    /// the entry regex cannot wander into one of the file's other stage-keyed maps (STAGE_EXPLANATION and the
    /// rest use the same ids).
    /// </summary>
    private static readonly Regex ClientStageNamesBlock = new(
        @"export const STAGE_NAMES[^=]*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);

    private static IReadOnlyDictionary<string, (string He, string En)> ParseClientStageNames(string source)
    {
        var block = ClientStageNamesBlock.Match(source);
        Assert.True(block.Success,
            $"Could not find the `export const STAGE_NAMES = {{ ... }};` declaration in {ClientStageNamesPath}. " +
            "It is the source of the canonical stage-name mirror in this file; if it moved or was renamed, " +
            "this test cannot verify the mirror and must be pointed at wherever it now lives.");

        var entries = ClientStageEntry.Matches(block.Groups["body"].Value)
            .Select(m => (Id: m.Groups["id"].Value, He: m.Groups["he"].Value, En: m.Groups["en"].Value))
            .ToList();

        // Named rather than left to ToDictionary's ArgumentException: a repeated id means the block boundary
        // slipped and the entry regex is reading one of the file's OTHER stage-keyed maps (STAGE_EXPLANATION
        // and the rest use the same ids), which would silently compare the mirror against prose.
        var duplicates = entries.GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            $"Read the same stage id twice from the STAGE_NAMES block in {ClientStageNamesPath}: " +
            $"[{string.Join(", ", duplicates)}]. The block boundary in ClientStageNamesBlock is no longer " +
            "stopping at that literal's closing brace, so this is reading a different map in the same file.");

        return entries.ToDictionary(e => e.Id, e => (e.He, e.En), StringComparer.Ordinal);
    }

    /// <summary>
    /// The client's stage-name file, if the two repos are checked out as siblings, or null. Walks UP from the
    /// test assembly looking for the path at each level, which finds it whatever the API repo is called and
    /// however deep the build output sits, and returns null rather than throwing when it is simply not there.
    /// </summary>
    private static string? FindClientStageNamesFile()
    {
        var relative = ClientStageNamesPath.Replace('/', Path.DirectorySeparatorChar);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The client repo's root beside this one, or null. final-r01: the skip branch above used to infer
    /// "the client repo is not checked out" from "the file is not there", and those are two different
    /// states. If the repo IS on disk and only the FILE moved - a client refactor, or a typo in
    /// <see cref="ClientStageNamesPath"/> - the pin becomes a permanent silent no-op that passes on every
    /// developer machine forever, which is the one failure a cross-repo pin cannot afford. That case now
    /// fails; only a genuinely absent sibling repo skips.
    /// </summary>
    private static string? FindClientRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "pagedraft-client");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
