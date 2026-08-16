using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE BYTE-IDENTITY PIN for every prompt surface <see cref="PromptFactory"/> composes.
///
/// WHY IT EXISTS. The prompt strings are the subject of standing measured gates (proofread gold,
/// linguistic gold, the whole-book review coverage runs). A verdict recorded against a composed
/// prompt belongs to THAT EXACT STRING, not to its intent, so a "tidied" space, a re-indented raw
/// string literal, a reordered interpolation, or an editor stripping a trailing newline silently
/// invalidates numbers nobody will re-measure. Those edits are invisible to every behavioural test
/// in the suite, because every one of them asserts on a SUBSTRING or a shape.
///
/// WHAT IT PINS. One SHA-256 (plus the char length, so a failure says whether the string grew or
/// merely changed) per composed surface, over the WHOLE composed string - system messages, the two
/// GetPrompt slots for every AiTaskType, every AnalysisType instruction with and without a fully
/// populated AnalysisContext (so the context preamble and every Format* helper are covered), both
/// per-chunk builders in both arm states, and every whole-book review surface including the three
/// map-reduce shapes and the anchor allowlist.
///
/// HOW TO READ A FAILURE. This test failing does NOT mean "update the pin". It means a composed
/// prompt changed. If the change was intended, the measured numbers that prompt carries are now
/// stale and must be RE-MEASURED before the pin is re-stamped; if it was not intended (a refactor,
/// a file split, a line-ending or indentation slip), the change is the bug and the pin is right.
/// On mismatch the actual manifest is written to the path named in the failure message, so
/// re-stamping is a copy, not a transcription.
///
/// WHAT KEEPS "EVERY" HONEST. The manifest's enum axes are mechanical, but its method set is
/// hand-written, and a hand-written list cannot hold a closed-set claim by itself - least of all now
/// that PromptFactory is five partial-class files, none of which contains this pin.
/// <see cref="EveryPromptReturningMember_AppearsInTheManifest"/> re-derives the surface set from the
/// type by reflection and fails if any prompt-returning member is missing from the manifest.
/// </summary>
public class PromptFactoryByteIdentityPinTests
{
    // ── Deterministic inputs. Every value here is arbitrary but FROZEN: changing one changes the
    //    manifest without any prompt having changed, which would make this pin a liar. ──────────

    private static readonly Guid FixedBookId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FixedChapterId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FixedSceneId = new("33333333-3333-3333-3333-333333333333");

    private const string MetricsJson = """
        {
          "syntaxMetrics": { "sentenceCount": 8, "averageSentenceLength": 15.0, "complexSentences": 2, "shortestSentence": 4, "longestSentence": 30 },
          "morphologyMetrics": { "wordCount": 120, "uniqueWords": 90, "averageWordLength": 4.5, "lexicalDensity": 0.75 },
          "styleMetrics": { "formality": "literary", "readability": 0.8, "voiceBalance": "active" },
          "grammaticalityScore": 0.95,
          "summary": "Baseline.",
          "deviations": [],
          "consistencyIssues": []
        }
        """;

    private static CharacterRegister BuildRegister() => new()
    {
        Characters = new List<CharacterRegisterEntry>
        {
            new() { Name = "Dana", Gender = "female", Role = "protagonist", Description = "A cartographer.", Aliases = new[] { "Dan", "D." } },
            new() { Name = "Yoav", Gender = "male", Role = "antagonist", Description = "Her brother.", Aliases = new[] { "Yo" } },
            new() { Name = "Mira", Gender = "female", Role = "supporting", Description = "A neighbour." },
            new() { Name = "Eitan", Gender = "male", Role = "minor" },
        }
    };

    private static StyleProfileData BuildStyleProfile() => new()
    {
        DominantTone = "lyrical",
        Pov = "third-limited",
        TensePattern = "past",
        VocabularyLevel = "literary",
        DialogueStyle = "natural",
        RecurringMotifs = new[] { "maps", "rain", "keys" },
        AverageSentenceLength = 14.5,
        FormalityScore = 0.62,
    };

    private static ChapterBrief BuildChapterBrief() => new()
    {
        Title = "The Cartographer",
        Order = 3,
        Summary = "Dana finds the map.",
        PlotEvents = new[] { "Dana finds the map", "Yoav lies" },
        CharacterStates = new[]
        {
            new ChapterCharacterState { Name = "Dana", State = "determined", EmotionalArc = "hope to doubt" },
            new ChapterCharacterState { Name = "Yoav", State = "evasive" },
        },
        ThematicMarkers = new[] { "inheritance" },
        ToneNotes = "tense",
        OpenThreads = new[] { "who drew the map" },
    };

    private static BookBrief BuildBookBrief() => new()
    {
        Genre = "literary",
        SubGenre = "family saga",
        TargetAudience = "adult",
        LiteratureLevel = 7,
        Themes = new[] { "memory", "inheritance" },
        Synopsis = "A family reads a map it did not draw.",
    };

    /// <summary>
    /// A context with EVERY optional field populated, so the field mask in GetRelevantFields decides
    /// what renders rather than the fixture's sparseness. A sparse fixture would let a whole section
    /// disappear from a prompt without moving this pin.
    /// </summary>
    private static AnalysisContext BuildFullContext(AnalysisType type, AnalysisScope scope) => new()
    {
        TargetText = "The analyzed text under test.",
        PrecedingContext = "What came immediately before.",
        FollowingContext = "What comes immediately after.",
        Characters = BuildRegister(),
        StyleProfile = BuildStyleProfile(),
        ChapterBrief = BuildChapterBrief(),
        BookBrief = BuildBookBrief(),
        ChapterStyleBaseline = new ChapterStyleProfile { MetricsJson = MetricsJson },
        BookStyleAverages = BuildStyleProfile(),
        Scope = scope,
        AnalysisType = type,
        BookId = FixedBookId,
        ChapterId = FixedChapterId,
        SceneId = FixedSceneId,
    };

    private static PromptFactory Unconfigured() => new();

    private static PromptFactory WithOverlapLicence() =>
        new(Options.Create(new ProofreadPromptOptions { OverlapReferentLicence = true }));

    private static readonly string[] Languages = { "he", "en" };

    private static readonly string[] ReviewDimensions =
        { "plot", "character", "pacing", "tone", "theme", "continuity", "unknown-dimension" };

    /// <summary>
    /// Builds "key\tlength\tsha256" for every composed surface, in a fixed order. The key names the
    /// call, not the file it lives in, so moving a template between partial-class files cannot move
    /// the manifest.
    /// </summary>
    private static string BuildManifest()
    {
        var rows = new List<string>();

        void Pin(string key, string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            rows.Add($"{key}\t{value.Length}\t{Convert.ToHexString(bytes)}");
        }

        var f = Unconfigured();

        // ── Pipeline prompts: both slots, every task type, both languages ──
        foreach (var task in Enum.GetValues<AiTaskType>().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        foreach (var lang in Languages)
        {
            var (system, instruction) = f.GetPrompt(task, lang);
            Pin($"GetPrompt/{task}/{lang}/system", system);
            Pin($"GetPrompt/{task}/{lang}/instruction", instruction);
        }

        // ── Unified analysis instructions, bare and context-enriched at every scope ──
        foreach (var type in Enum.GetValues<AnalysisType>().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            foreach (var lang in Languages)
            {
                Pin($"GetAnalysisPrompt/{type}/{lang}", f.GetAnalysisPrompt(type, lang));
                Pin($"GetAnalysisPrompt/{type}/{lang}/nullContext", f.GetAnalysisPrompt(type, lang, null));

                foreach (var scope in Enum.GetValues<AnalysisScope>())
                    Pin($"GetAnalysisPrompt/{type}/{lang}/full/{scope}",
                        f.GetAnalysisPrompt(type, lang, BuildFullContext(type, scope)));
            }
        }

        // ── Per-chunk proofread: both arm states x with/without each optional section ──
        foreach (var lang in Languages)
        {
            foreach (var (armName, factory) in new (string, PromptFactory)[] { ("armOff", f), ("armOn", WithOverlapLicence()) })
            {
                Pin($"BuildProofreadChunkPrompt/{lang}/{armName}/bare",
                    factory.BuildProofreadChunkPrompt(lang, null, null));
                Pin($"BuildProofreadChunkPrompt/{lang}/{armName}/register",
                    factory.BuildProofreadChunkPrompt(lang, BuildRegister(), null));
                Pin($"BuildProofreadChunkPrompt/{lang}/{armName}/overlap",
                    factory.BuildProofreadChunkPrompt(lang, null, "  The sentence that came before.  "));
                Pin($"BuildProofreadChunkPrompt/{lang}/{armName}/both",
                    factory.BuildProofreadChunkPrompt(lang, BuildRegister(), "  The sentence that came before.  "));
            }
        }

        // ── Per-chunk line edit: first / middle / last chunk pick different context sources ──
        foreach (var lang in Languages)
        {
            var ctx = BuildFullContext(AnalysisType.LineEdit, AnalysisScope.Chapter);
            Pin($"BuildLineEditChunkPrompt/{lang}/first",
                f.BuildLineEditChunkPrompt(lang, ctx, "local before", "local after", isFirstChunk: true, isLastChunk: false));
            Pin($"BuildLineEditChunkPrompt/{lang}/middle",
                f.BuildLineEditChunkPrompt(lang, ctx, "local before", "local after", isFirstChunk: false, isLastChunk: false));
            Pin($"BuildLineEditChunkPrompt/{lang}/last",
                f.BuildLineEditChunkPrompt(lang, ctx, "local before", "local after", isFirstChunk: false, isLastChunk: true));
            Pin($"BuildLineEditChunkPrompt/{lang}/only",
                f.BuildLineEditChunkPrompt(lang, ctx, null, null, isFirstChunk: true, isLastChunk: true));
        }

        // ── Standalone instruction surfaces ──
        foreach (var lang in Languages)
        {
            Pin($"GetCharacterExtractionPrompt/{lang}", f.GetCharacterExtractionPrompt(lang));

            Pin($"GetExplainSuggestionPrompt/{lang}/withReason",
                f.GetExplainSuggestionPrompt("the original text", "the suggested text", "a stated reason", lang));
            Pin($"GetExplainSuggestionPrompt/{lang}/nullReason",
                f.GetExplainSuggestionPrompt("the original text", "the suggested text", null, lang));

            Pin($"GetStructuredChapterBriefPrompt/{lang}", f.GetStructuredChapterBriefPrompt(lang));
            Pin($"GetStructuredChapterBriefPromptSeededWithUserSummary/{lang}/seeded",
                f.GetStructuredChapterBriefPromptSeededWithUserSummary(lang, "  The author's own summary of the chapter.  "));
            Pin($"GetStructuredChapterBriefPromptSeededWithUserSummary/{lang}/blank",
                f.GetStructuredChapterBriefPromptSeededWithUserSummary(lang, "   "));
        }

        // ── Whole-book review: per-dimension, single-combined, and the three map-reduce shapes ──
        foreach (var lang in Languages)
        {
            foreach (var dim in ReviewDimensions)
                Pin($"BuildBookReviewPrompt/{dim}/{lang}", f.BuildBookReviewPrompt(dim, lang));

            Pin($"BuildBookReviewCombinedPrompt/{lang}", f.BuildBookReviewCombinedPrompt(lang));

            Pin($"BuildChapterAnchorAllowlistRule/{lang}/empty",
                f.BuildChapterAnchorAllowlistRule(lang, Array.Empty<int>()));
            Pin($"BuildChapterAnchorAllowlistRule/{lang}/orders",
                f.BuildChapterAnchorAllowlistRule(lang, new[] { 5, 2, 2, 11, 0 }));

            Pin($"BuildBookReviewWindowPrompt/{lang}/first",
                f.BuildBookReviewWindowPrompt(lang, 1, 4, 0, 5));
            Pin($"BuildBookReviewWindowPrompt/{lang}/middle",
                f.BuildBookReviewWindowPrompt(lang, 3, 4, 11, 16));

            Pin($"BuildBookReviewSynthesisPrompt/{lang}", f.BuildBookReviewSynthesisPrompt(lang));
            Pin($"BuildBookReviewContinuityReducePrompt/{lang}", f.BuildBookReviewContinuityReducePrompt(lang));
        }

        // ── The BookReview character-register rendering (its own cap/priority rules) ──
        Pin("FormatCharactersForBookReview", PromptFactory.FormatCharactersForBookReview(BuildRegister()));

        return string.Join("\n", rows);
    }

    [Fact]
    public void EveryComposedPromptSurface_IsByteIdenticalToItsPin()
    {
        var actual = BuildManifest();

        if (!string.Equals(actual, ExpectedManifest, StringComparison.Ordinal))
        {
            var dump = Path.Combine(Path.GetTempPath(), "promptfactory-byte-identity-actual.txt");
            File.WriteAllText(dump, actual);
            Assert.Fail(
                "A composed prompt changed. This is not a pin to update - it is a measurement to redo.\n" +
                "If the change was intended, RE-MEASURE the gates that prompt carries before re-stamping.\n" +
                "If it was not (a refactor, a file split, a line-ending or indentation slip), the change is the bug.\n" +
                $"Actual manifest written to: {dump}\n" +
                FirstDifference(ExpectedManifest, actual));
        }
    }

    // ── THE SURFACE ORACLE ───────────────────────────────────────────────────────────────────────
    //
    // BuildManifest's ENUM axes (AiTaskType, AnalysisType, AnalysisScope) are mechanical and cannot
    // go stale. Its METHOD set is a HAND-WRITTEN list of calls, sitting under a class docstring that
    // makes a CLOSED-SET claim - "every prompt surface PromptFactory composes". A hand-authored list
    // cannot keep a closed-set claim on its own.
    //
    // WHAT RAISED THE RISK. PromptFactory is now five partial-class files (PromptFactory.cs,
    // .AnalysisTemplates.cs, .BookTemplates.cs, .BookReview.cs, .BookReviewMapReduce.cs) and NONE of
    // them contains this pin. Adding a prompt method now means opening a file with no visible tie to
    // the manifest, so a new surface would be silently unpinned and the suite would stay green.
    //
    // WHAT THIS ORACLE DOES. It re-derives the surface set from the TYPE, by reflection, and asserts
    // the hand-written manifest covers all of it. It never SKIPS a member it cannot account for: an
    // unaccounted member is a failure, because "skip what you do not recognise" is precisely how the
    // hand-written list would rot.
    //
    // SCOPE: invocable members declared ON PromptFactory that are public or internal - methods
    // (property accessors included, so a future `public string XyzPrompt => Compose(...)` is caught
    // as get_XyzPrompt) and constructors. FIELDS are out of scope by decision, not by oversight: a
    // field STORES a fragment and composes nothing, and every fragment reaches an author only inside
    // a composed surface this manifest already pins (the four internal Proofread* / Overlap* consts
    // reach it through BuildProofreadChunkPrompt, pinned in both arm states).

    /// <summary>
    /// The DELIBERATE "no" verdicts: public/internal members of <see cref="PromptFactory"/> that are
    /// not prompt surfaces, each with the reason it is not one. Listing them explicitly is the point -
    /// a reader can tell a considered exclusion from a member nobody looked at, and a member that is
    /// in neither this list nor the manifest fails the oracle rather than passing quietly.
    /// Keyed by member name (both constructors share the name ".ctor").
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotPromptSurfaces =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".ctor"] =
                "The two constructors. They take CONFIGURATION (the ProofreadPromptOptions overlap-licence arm) " +
                "and compose no text; the text that configuration selects is pinned through " +
                "BuildProofreadChunkPrompt/*/armOff and /armOn, which this manifest builds from both ctors.",

            ["RendersCharacterRegister"] =
                "A PREDICATE over AnalysisType (returns bool), not a prompt. It decides whether a caller bothers " +
                "to fetch a character register; the text a register produces once fetched is pinned under " +
                "GetAnalysisPrompt/*/full/* and BuildProofreadChunkPrompt/*/register.",
        };

    /// <summary>
    /// Every public/internal member of <see cref="PromptFactory"/> that RETURNS a prompt - a string,
    /// or the (SystemMessage, Instruction) tuple GetPrompt returns - must appear in the manifest as a
    /// key prefix. Anything else must carry an explicit reason in <see cref="NotPromptSurfaces"/>.
    /// </summary>
    [Fact]
    public void EveryPromptReturningMember_AppearsInTheManifest()
    {
        var declared = typeof(PromptFactory)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OfType<MethodBase>()
            // public or internal. Private/protected helpers are implementation detail: they reach an
            // author only through a public surface, which this oracle already requires to be pinned.
            .Where(m => m.IsPublic || m.IsAssembly)
            .Where(m => m.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .ToList();

        // A reflection oracle that enumerated NOTHING would pass every assertion below in silence,
        // and that is exactly the failure mode that makes an oracle blind. Floor it.
        Assert.True(
            declared.Count > 0,
            "The surface oracle discovered NO public or internal members on PromptFactory. That is the " +
            "oracle being blind, not the type being empty - check the BindingFlags and the accessibility " +
            "filter above before believing this run.");

        static bool ReturnsAPrompt(MethodBase m) =>
            m is MethodInfo mi &&
            (mi.ReturnType == typeof(string) || mi.ReturnType == typeof(ValueTuple<string, string>));

        var surfaceNames = declared
            .Where(ReturnsAPrompt)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            surfaceNames.Count > 0,
            "The surface oracle discovered NO prompt-returning members on PromptFactory, so every check " +
            "below is vacuous. PromptFactory composes prompts; a run that finds none has a broken " +
            "discovery step (return-type comparison, accessibility filter, or DeclaredOnly), not a " +
            "prompt-free factory.");

        // Every non-surface must carry a written reason. THROW, never skip: a member nobody classified
        // is the one that quietly leaves a prompt unpinned.
        var unclassified = declared
            .Where(m => !ReturnsAPrompt(m))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(n => !NotPromptSurfaces.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "PromptFactory has public/internal member(s) this oracle has no verdict for: " +
            string.Join(", ", unclassified) + ".\n" +
            "If a member composes a prompt, give it a probe in BuildManifest (and re-measure the gates " +
            "that prompt carries before stamping its row). If it does not, add it to NotPromptSurfaces " +
            "with the reason it is not a prompt surface.");

        // A stale exclusion is rot in the other direction: a reason written for a member that no longer
        // exists reads as coverage while covering nothing.
        var declaredNames = declared.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var staleExclusions = NotPromptSurfaces.Keys
            .Where(n => !declaredNames.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            staleExclusions.Count == 0,
            "NotPromptSurfaces names member(s) that no longer exist on PromptFactory: " +
            string.Join(", ", staleExclusions) + ". Remove the entry; a reason for an absent member is " +
            "an exclusion list pretending to have looked at something.");

        // Manifest rows are "key \t length \t sha256"; a surface is covered when some key IS its name
        // or starts with "<name>/". The trailing slash matters: without it
        // GetStructuredChapterBriefPromptSeededWithUserSummary's rows would falsely cover
        // GetStructuredChapterBriefPrompt.
        var manifestKeys = BuildManifest()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split('\t')[0])
            .ToList();

        Assert.True(
            manifestKeys.Count > 0,
            "BuildManifest produced no rows, so the coverage check below cannot fail. Fix the manifest " +
            "before reading this test as green.");

        var unpinned = surfaceNames
            .Where(name => !manifestKeys.Any(k =>
                string.Equals(k, name, StringComparison.Ordinal) ||
                k.StartsWith(name + "/", StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            unpinned.Count == 0,
            "PromptFactory composes prompt surface(s) that BuildManifest never calls, so their bytes are " +
            "UNPINNED and can drift without this suite going red: " + string.Join(", ", unpinned) + ".\n" +
            "Add a probe to BuildManifest keyed \"<MemberName>/...\" covering the argument shapes that " +
            "change the composed text, then stamp its rows into ExpectedManifest.");
    }

    /// <summary>Names the first differing manifest ROW, so a failure points at a surface, not at a diff of 200 lines.</summary>
    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<missing>";
            var al = i < a.Length ? a[i] : "<missing>";
            if (!string.Equals(el, al, StringComparison.Ordinal))
                return $"First differing row {i}:\n  pinned: {el}\n  actual: {al}";
        }
        return "No row differs (trailing content only).";
    }

    /// <summary>
    /// STAMPED 2026-08-15 against the pre-split PromptFactory.cs, immediately before the file was
    /// broken into partial-class partners. Each row is key \t char-length \t sha256-of-utf8.
    /// Joined explicitly with \n rather than written as one multi-line literal, so a checkout that
    /// rewrites this file's line endings cannot move the pin.
    /// </summary>
    private static readonly string ExpectedManifest = string.Join("\n", new[]
    {
        "GetPrompt/AnalysisRepair/he/system\t168\t45130835902EA3187BB088C036AF02725ABE98FAA889E32E7849CB2B85E1DF1E",
        "GetPrompt/AnalysisRepair/he/instruction\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetPrompt/AnalysisRepair/en/system\t168\t45130835902EA3187BB088C036AF02725ABE98FAA889E32E7849CB2B85E1DF1E",
        "GetPrompt/AnalysisRepair/en/instruction\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetPrompt/BookReview/he/system\t422\t104809ED5998BE600AEF38EE6A00D37CB0E7AC23EC0ACA4C7C4607EC0F26528A",
        "GetPrompt/BookReview/he/instruction\t0\tE3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        "GetPrompt/BookReview/en/system\t240\t362705C2BBAFBEE517BAA0B6CE2D59F853554278F7FBE8DBDBA0398E79F0A7A0",
        "GetPrompt/BookReview/en/instruction\t0\tE3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        "GetPrompt/GenericChat/he/system\t142\tE45BE367259EB07B34358E5FAE91D760A824A5BD9F72E3DA2ADBBB9FB5B27110",
        "GetPrompt/GenericChat/he/instruction\t35\t58818979D19B6C29AD531FBD0303C47B8B2741161563D47FCF2B94ACCC22546A",
        "GetPrompt/GenericChat/en/system\t211\t84B72DB4A0B31A19C6B65F664A7F536A160935A49C7BA44EE6488FA74042E60D",
        "GetPrompt/GenericChat/en/instruction\t44\t130D26FBBD1C136C0FE69DF753A21544FC6D36867E6249F5ADFC79E9E051CB96",
        "GetPrompt/LineEdit/he/system\t140\t016A09CE9FDBFC08FF9A6C51B2E565A1E9481421F6D35AF424A87B3E4E6EA70A",
        "GetPrompt/LineEdit/he/instruction\t1160\t6A262A13C309464C2C998A423844EC03E152D2F4C9B232487D552A0DA6922030",
        "GetPrompt/LineEdit/en/system\t202\tA0F3C74DEBA2EADEA6178BE5A57A536D65364DAF6A32729367EB99399BF25607",
        "GetPrompt/LineEdit/en/instruction\t1504\tBA7B1D7FCA2944C57422B84D8F24E6013F0D9CBC7146AAFBDE2537D14E156151",
        "GetPrompt/LinguisticAnalysis/he/system\t422\t104809ED5998BE600AEF38EE6A00D37CB0E7AC23EC0ACA4C7C4607EC0F26528A",
        "GetPrompt/LinguisticAnalysis/he/instruction\t168\t14CEF296C3A3061F1E8CAC7E06A95189FB32B1A32B77890127BE3C015EAD5EE5",
        "GetPrompt/LinguisticAnalysis/en/system\t240\t362705C2BBAFBEE517BAA0B6CE2D59F853554278F7FBE8DBDBA0398E79F0A7A0",
        "GetPrompt/LinguisticAnalysis/en/instruction\t196\tF071C7B2350EBBA3EDAD63F144240D08B3A3BEDED7E10B422922A537B70891FE",
        "GetPrompt/ProductChat/he/system\t1082\t6831FA13A9130A1C39B3EEF366857747E5BA3CA03FF39104B43E4F9F96C64432",
        "GetPrompt/ProductChat/he/instruction\t0\tE3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        "GetPrompt/ProductChat/en/system\t1429\tAF9251B0291228E5CA4425FC1B18B8FFF247DA9A08030E995C7AC62E60181F00",
        "GetPrompt/ProductChat/en/instruction\t0\tE3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        "GetPrompt/Proofread/he/system\t168\t45130835902EA3187BB088C036AF02725ABE98FAA889E32E7849CB2B85E1DF1E",
        "GetPrompt/Proofread/he/instruction\t707\t52202EF23CCE0DDF7DC86CBB9A79FA7BC9669C5935D3A1A271B736A027297A8A",
        "GetPrompt/Proofread/en/system\t190\t23D3C684EBD5BB54702CEB0046677A1164E9C4CAF8A6FF779C2A2594A1E92142",
        "GetPrompt/Proofread/en/instruction\t465\tEFFF9BC2F48B654512E11525EE7A01CADAE56912CB43B8E80AF9523468331578",
        "GetPrompt/Summarization/he/system\t422\t104809ED5998BE600AEF38EE6A00D37CB0E7AC23EC0ACA4C7C4607EC0F26528A",
        "GetPrompt/Summarization/he/instruction\t76\t968BE2A3B2B3BC1427FEF9A3F77E6FF72FB0B4B5A1B8FC0579600F6062D4FCCD",
        "GetPrompt/Summarization/en/system\t240\t362705C2BBAFBEE517BAA0B6CE2D59F853554278F7FBE8DBDBA0398E79F0A7A0",
        "GetPrompt/Summarization/en/instruction\t97\tD60FE9B8027CCD2FA49327513C596AC8D026C690451D59183CED672CF04B4B4A",
        "GetPrompt/TermRepair/he/system\t168\t45130835902EA3187BB088C036AF02725ABE98FAA889E32E7849CB2B85E1DF1E",
        "GetPrompt/TermRepair/he/instruction\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetPrompt/TermRepair/en/system\t168\t45130835902EA3187BB088C036AF02725ABE98FAA889E32E7849CB2B85E1DF1E",
        "GetPrompt/TermRepair/en/instruction\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetPrompt/Translation/he/system\t142\tE45BE367259EB07B34358E5FAE91D760A824A5BD9F72E3DA2ADBBB9FB5B27110",
        "GetPrompt/Translation/he/instruction\t35\t58818979D19B6C29AD531FBD0303C47B8B2741161563D47FCF2B94ACCC22546A",
        "GetPrompt/Translation/en/system\t211\t84B72DB4A0B31A19C6B65F664A7F536A160935A49C7BA44EE6488FA74042E60D",
        "GetPrompt/Translation/en/instruction\t44\t130D26FBBD1C136C0FE69DF753A21544FC6D36867E6249F5ADFC79E9E051CB96",
        "GetAnalysisPrompt/BookOverview/he\t497\t75A8DA2B6CD7994C169EE06B5A79B412E3B943C0799172E4E9647C9D89AAE4BF",
        "GetAnalysisPrompt/BookOverview/he/nullContext\t497\t75A8DA2B6CD7994C169EE06B5A79B412E3B943C0799172E4E9647C9D89AAE4BF",
        "GetAnalysisPrompt/BookOverview/he/full/Book\t497\t75A8DA2B6CD7994C169EE06B5A79B412E3B943C0799172E4E9647C9D89AAE4BF",
        "GetAnalysisPrompt/BookOverview/he/full/Chapter\t497\t75A8DA2B6CD7994C169EE06B5A79B412E3B943C0799172E4E9647C9D89AAE4BF",
        "GetAnalysisPrompt/BookOverview/he/full/Scene\t497\t75A8DA2B6CD7994C169EE06B5A79B412E3B943C0799172E4E9647C9D89AAE4BF",
        "GetAnalysisPrompt/BookOverview/en\t607\tE42A3345B79B08C32CDEE05651226B598B6F1B63DF2E84B33864975D7D2BCFEC",
        "GetAnalysisPrompt/BookOverview/en/nullContext\t607\tE42A3345B79B08C32CDEE05651226B598B6F1B63DF2E84B33864975D7D2BCFEC",
        "GetAnalysisPrompt/BookOverview/en/full/Book\t607\tE42A3345B79B08C32CDEE05651226B598B6F1B63DF2E84B33864975D7D2BCFEC",
        "GetAnalysisPrompt/BookOverview/en/full/Chapter\t607\tE42A3345B79B08C32CDEE05651226B598B6F1B63DF2E84B33864975D7D2BCFEC",
        "GetAnalysisPrompt/BookOverview/en/full/Scene\t607\tE42A3345B79B08C32CDEE05651226B598B6F1B63DF2E84B33864975D7D2BCFEC",
        "GetAnalysisPrompt/BookReview/he\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetAnalysisPrompt/BookReview/he/nullContext\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetAnalysisPrompt/BookReview/he/full/Book\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetAnalysisPrompt/BookReview/he/full/Chapter\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetAnalysisPrompt/BookReview/he/full/Scene\t16\t386DF05AF961A4B95F63601C74AE8D36D6A2EA3B86B8B805F323EFF59751F2F0",
        "GetAnalysisPrompt/BookReview/en\t19\tCE0E33F79452E61C378AA83A18FC53EC9C65FFAF3B1BA9BDDE9F9116A5351BE8",
        "GetAnalysisPrompt/BookReview/en/nullContext\t19\tCE0E33F79452E61C378AA83A18FC53EC9C65FFAF3B1BA9BDDE9F9116A5351BE8",
        "GetAnalysisPrompt/BookReview/en/full/Book\t19\tCE0E33F79452E61C378AA83A18FC53EC9C65FFAF3B1BA9BDDE9F9116A5351BE8",
        "GetAnalysisPrompt/BookReview/en/full/Chapter\t19\tCE0E33F79452E61C378AA83A18FC53EC9C65FFAF3B1BA9BDDE9F9116A5351BE8",
        "GetAnalysisPrompt/BookReview/en/full/Scene\t19\tCE0E33F79452E61C378AA83A18FC53EC9C65FFAF3B1BA9BDDE9F9116A5351BE8",
        "GetAnalysisPrompt/CharacterAnalysis/he\t596\tDE3E0A71FD465508A4431BE99AD7559C44574518E6662601440AC3C827B1FB6A",
        "GetAnalysisPrompt/CharacterAnalysis/he/nullContext\t596\tDE3E0A71FD465508A4431BE99AD7559C44574518E6662601440AC3C827B1FB6A",
        "GetAnalysisPrompt/CharacterAnalysis/he/full/Book\t596\tDE3E0A71FD465508A4431BE99AD7559C44574518E6662601440AC3C827B1FB6A",
        "GetAnalysisPrompt/CharacterAnalysis/he/full/Chapter\t596\tDE3E0A71FD465508A4431BE99AD7559C44574518E6662601440AC3C827B1FB6A",
        "GetAnalysisPrompt/CharacterAnalysis/he/full/Scene\t596\tDE3E0A71FD465508A4431BE99AD7559C44574518E6662601440AC3C827B1FB6A",
        "GetAnalysisPrompt/CharacterAnalysis/en\t740\t2C1EF68C0EB28A4C92D09F68684B6CFF710775E5230D01EB56ADCD8136E1A349",
        "GetAnalysisPrompt/CharacterAnalysis/en/nullContext\t740\t2C1EF68C0EB28A4C92D09F68684B6CFF710775E5230D01EB56ADCD8136E1A349",
        "GetAnalysisPrompt/CharacterAnalysis/en/full/Book\t740\t2C1EF68C0EB28A4C92D09F68684B6CFF710775E5230D01EB56ADCD8136E1A349",
        "GetAnalysisPrompt/CharacterAnalysis/en/full/Chapter\t740\t2C1EF68C0EB28A4C92D09F68684B6CFF710775E5230D01EB56ADCD8136E1A349",
        "GetAnalysisPrompt/CharacterAnalysis/en/full/Scene\t740\t2C1EF68C0EB28A4C92D09F68684B6CFF710775E5230D01EB56ADCD8136E1A349",
        "GetAnalysisPrompt/Custom/he\t35\t58818979D19B6C29AD531FBD0303C47B8B2741161563D47FCF2B94ACCC22546A",
        "GetAnalysisPrompt/Custom/he/nullContext\t35\t58818979D19B6C29AD531FBD0303C47B8B2741161563D47FCF2B94ACCC22546A",
        "GetAnalysisPrompt/Custom/he/full/Book\t35\t58818979D19B6C29AD531FBD0303C47B8B2741161563D47FCF2B94ACCC22546A",
        "GetAnalysisPrompt/Custom/he/full/Chapter\t35\t58818979D19B6C29AD531FBD0303C47B8B2741161563D47FCF2B94ACCC22546A",
        "GetAnalysisPrompt/Custom/he/full/Scene\t35\t58818979D19B6C29AD531FBD0303C47B8B2741161563D47FCF2B94ACCC22546A",
        "GetAnalysisPrompt/Custom/en\t44\t130D26FBBD1C136C0FE69DF753A21544FC6D36867E6249F5ADFC79E9E051CB96",
        "GetAnalysisPrompt/Custom/en/nullContext\t44\t130D26FBBD1C136C0FE69DF753A21544FC6D36867E6249F5ADFC79E9E051CB96",
        "GetAnalysisPrompt/Custom/en/full/Book\t44\t130D26FBBD1C136C0FE69DF753A21544FC6D36867E6249F5ADFC79E9E051CB96",
        "GetAnalysisPrompt/Custom/en/full/Chapter\t44\t130D26FBBD1C136C0FE69DF753A21544FC6D36867E6249F5ADFC79E9E051CB96",
        "GetAnalysisPrompt/Custom/en/full/Scene\t44\t130D26FBBD1C136C0FE69DF753A21544FC6D36867E6249F5ADFC79E9E051CB96",
        "GetAnalysisPrompt/LineEdit/he\t1160\t6A262A13C309464C2C998A423844EC03E152D2F4C9B232487D552A0DA6922030",
        "GetAnalysisPrompt/LineEdit/he/nullContext\t1160\t6A262A13C309464C2C998A423844EC03E152D2F4C9B232487D552A0DA6922030",
        "GetAnalysisPrompt/LineEdit/he/full/Book\t1998\t74C847C318D3738459B56DB6A48A76B102B311E70C52A4F4AAB076AE3422900E",
        "GetAnalysisPrompt/LineEdit/he/full/Chapter\t1998\t74C847C318D3738459B56DB6A48A76B102B311E70C52A4F4AAB076AE3422900E",
        "GetAnalysisPrompt/LineEdit/he/full/Scene\t1998\t74C847C318D3738459B56DB6A48A76B102B311E70C52A4F4AAB076AE3422900E",
        "GetAnalysisPrompt/LineEdit/en\t1504\tBA7B1D7FCA2944C57422B84D8F24E6013F0D9CBC7146AAFBDE2537D14E156151",
        "GetAnalysisPrompt/LineEdit/en/nullContext\t1504\tBA7B1D7FCA2944C57422B84D8F24E6013F0D9CBC7146AAFBDE2537D14E156151",
        "GetAnalysisPrompt/LineEdit/en/full/Book\t2342\t762691F15AF32A672F46ACFB87081D31A0FCA0416FA4A694ECBF3656DDA407AA",
        "GetAnalysisPrompt/LineEdit/en/full/Chapter\t2342\t762691F15AF32A672F46ACFB87081D31A0FCA0416FA4A694ECBF3656DDA407AA",
        "GetAnalysisPrompt/LineEdit/en/full/Scene\t2342\t762691F15AF32A672F46ACFB87081D31A0FCA0416FA4A694ECBF3656DDA407AA",
        "GetAnalysisPrompt/LinguisticAnalysis/he\t7814\t8F75811229E54501DB5B887D1FB1732E35F219BC110509F39420B4571A473316",
        "GetAnalysisPrompt/LinguisticAnalysis/he/nullContext\t7814\t8F75811229E54501DB5B887D1FB1732E35F219BC110509F39420B4571A473316",
        "GetAnalysisPrompt/LinguisticAnalysis/he/full/Book\t8921\t7051EDC98FDBB2D24ADF4831ED638662FAF8E937EFEDA4A1F796CDD0610B45B0",
        "GetAnalysisPrompt/LinguisticAnalysis/he/full/Chapter\t8921\t7051EDC98FDBB2D24ADF4831ED638662FAF8E937EFEDA4A1F796CDD0610B45B0",
        "GetAnalysisPrompt/LinguisticAnalysis/he/full/Scene\t8837\t94FB895983142BEF8EDC7D20E7263A18E70E11BB86730792359CDE4003CF7AB9",
        "GetAnalysisPrompt/LinguisticAnalysis/en\t9326\t45000F07E3162DA05E317E9F36187EE2B139820FFB9C0F1B1A04536A02E470D0",
        "GetAnalysisPrompt/LinguisticAnalysis/en/nullContext\t9326\t45000F07E3162DA05E317E9F36187EE2B139820FFB9C0F1B1A04536A02E470D0",
        "GetAnalysisPrompt/LinguisticAnalysis/en/full/Book\t10433\tA56E274D62FE912014E7BCE904E493FF47BDABE36513B06D37244660D7F3BBF0",
        "GetAnalysisPrompt/LinguisticAnalysis/en/full/Chapter\t10433\tA56E274D62FE912014E7BCE904E493FF47BDABE36513B06D37244660D7F3BBF0",
        "GetAnalysisPrompt/LinguisticAnalysis/en/full/Scene\t10349\t7431400D4E9CDA9BBD32D0E878AF5611A320016823CD54796A74B0B0C6844ED5",
        "GetAnalysisPrompt/LiteraryAnalysis/he\t782\tBBEFADC4265947ECDB88C87C89F0546136207C6EE83C2B1862F2AD2520A0B7A8",
        "GetAnalysisPrompt/LiteraryAnalysis/he/nullContext\t782\tBBEFADC4265947ECDB88C87C89F0546136207C6EE83C2B1862F2AD2520A0B7A8",
        "GetAnalysisPrompt/LiteraryAnalysis/he/full/Book\t1766\tFF25DF75744E900D650A86CF5214B8DEE9B555AF8122917E5034855F2B0CB7E3",
        "GetAnalysisPrompt/LiteraryAnalysis/he/full/Chapter\t1766\tFF25DF75744E900D650A86CF5214B8DEE9B555AF8122917E5034855F2B0CB7E3",
        "GetAnalysisPrompt/LiteraryAnalysis/he/full/Scene\t1766\tFF25DF75744E900D650A86CF5214B8DEE9B555AF8122917E5034855F2B0CB7E3",
        "GetAnalysisPrompt/LiteraryAnalysis/en\t982\tA32EB923133D3CA2F9CBD1B803F3C67F10109E908FD5774827416CCE47AC5F25",
        "GetAnalysisPrompt/LiteraryAnalysis/en/nullContext\t982\tA32EB923133D3CA2F9CBD1B803F3C67F10109E908FD5774827416CCE47AC5F25",
        "GetAnalysisPrompt/LiteraryAnalysis/en/full/Book\t1966\t63CA2A96CB7CAEF72E30D8B97D03795950C9054B44AA2692AA02ECFEEDC5A203",
        "GetAnalysisPrompt/LiteraryAnalysis/en/full/Chapter\t1966\t63CA2A96CB7CAEF72E30D8B97D03795950C9054B44AA2692AA02ECFEEDC5A203",
        "GetAnalysisPrompt/LiteraryAnalysis/en/full/Scene\t1966\t63CA2A96CB7CAEF72E30D8B97D03795950C9054B44AA2692AA02ECFEEDC5A203",
        "GetAnalysisPrompt/Proofread/he\t1581\t614A43298FAE466C86AAE0B6C3BF7C432891AA8E79ACFAE66CBA608F81792DEE",
        "GetAnalysisPrompt/Proofread/he/nullContext\t1581\t614A43298FAE466C86AAE0B6C3BF7C432891AA8E79ACFAE66CBA608F81792DEE",
        "GetAnalysisPrompt/Proofread/he/full/Book\t2225\t3E8AEECEF99A2F9F83A001FD7A43DA27FEAC53EE317BEBBDB29C1F7EBD8B9991",
        "GetAnalysisPrompt/Proofread/he/full/Chapter\t2225\t3E8AEECEF99A2F9F83A001FD7A43DA27FEAC53EE317BEBBDB29C1F7EBD8B9991",
        "GetAnalysisPrompt/Proofread/he/full/Scene\t2225\t3E8AEECEF99A2F9F83A001FD7A43DA27FEAC53EE317BEBBDB29C1F7EBD8B9991",
        "GetAnalysisPrompt/Proofread/en\t1161\t9F479C491869657FC8E69E631477C36CC2A2E146D73D7DE01FF5552799BF1861",
        "GetAnalysisPrompt/Proofread/en/nullContext\t1161\t9F479C491869657FC8E69E631477C36CC2A2E146D73D7DE01FF5552799BF1861",
        "GetAnalysisPrompt/Proofread/en/full/Book\t1805\t64BD0589447D6B70930AFAB7F5951A355A23135009A27C83AE8CCDD3D6AFDECE",
        "GetAnalysisPrompt/Proofread/en/full/Chapter\t1805\t64BD0589447D6B70930AFAB7F5951A355A23135009A27C83AE8CCDD3D6AFDECE",
        "GetAnalysisPrompt/Proofread/en/full/Scene\t1805\t64BD0589447D6B70930AFAB7F5951A355A23135009A27C83AE8CCDD3D6AFDECE",
        "GetAnalysisPrompt/QA/he\t762\t7522C1D7847875A9E6E8B54E09718FCB8341103C74EE509C846DDC6A0F58EDD8",
        "GetAnalysisPrompt/QA/he/nullContext\t762\t7522C1D7847875A9E6E8B54E09718FCB8341103C74EE509C846DDC6A0F58EDD8",
        "GetAnalysisPrompt/QA/he/full/Book\t1177\t74494F8F7E16EA7107D4AA796C25CD50F0B35060DE60C856161163C2A118D6B0",
        "GetAnalysisPrompt/QA/he/full/Chapter\t1177\t74494F8F7E16EA7107D4AA796C25CD50F0B35060DE60C856161163C2A118D6B0",
        "GetAnalysisPrompt/QA/he/full/Scene\t1177\t74494F8F7E16EA7107D4AA796C25CD50F0B35060DE60C856161163C2A118D6B0",
        "GetAnalysisPrompt/QA/en\t593\t38F2A7C3D89BB66EF540005167990441239F4884A19900A9C60A12AA7E0D76F1",
        "GetAnalysisPrompt/QA/en/nullContext\t593\t38F2A7C3D89BB66EF540005167990441239F4884A19900A9C60A12AA7E0D76F1",
        "GetAnalysisPrompt/QA/en/full/Book\t1008\tCC7FE6C180D7633D07948EA6A6CD4364C10311E8604B7E0211FF2EA004AF9E29",
        "GetAnalysisPrompt/QA/en/full/Chapter\t1008\tCC7FE6C180D7633D07948EA6A6CD4364C10311E8604B7E0211FF2EA004AF9E29",
        "GetAnalysisPrompt/QA/en/full/Scene\t1008\tCC7FE6C180D7633D07948EA6A6CD4364C10311E8604B7E0211FF2EA004AF9E29",
        "GetAnalysisPrompt/StoryAnalysis/he\t736\tA16D26F820020C12EDE8C411705023CAEFF212CB76298E96FD5416AC76D566FB",
        "GetAnalysisPrompt/StoryAnalysis/he/nullContext\t736\tA16D26F820020C12EDE8C411705023CAEFF212CB76298E96FD5416AC76D566FB",
        "GetAnalysisPrompt/StoryAnalysis/he/full/Book\t917\t80C53BEDD8A0FC4200AE73E473158BEB2A7D32CC247EE67EDA339113878FE148",
        "GetAnalysisPrompt/StoryAnalysis/he/full/Chapter\t917\t80C53BEDD8A0FC4200AE73E473158BEB2A7D32CC247EE67EDA339113878FE148",
        "GetAnalysisPrompt/StoryAnalysis/he/full/Scene\t917\t80C53BEDD8A0FC4200AE73E473158BEB2A7D32CC247EE67EDA339113878FE148",
        "GetAnalysisPrompt/StoryAnalysis/en\t910\t0F6935E3E189E78A413D320E2DA15A2F81C3B36AEA4DBC637CA49F9559F691DE",
        "GetAnalysisPrompt/StoryAnalysis/en/nullContext\t910\t0F6935E3E189E78A413D320E2DA15A2F81C3B36AEA4DBC637CA49F9559F691DE",
        "GetAnalysisPrompt/StoryAnalysis/en/full/Book\t1091\t8A6A3C0E354A23C7E8ADBD4056654F86696938D8D81552D2EBD76B33BA911D4C",
        "GetAnalysisPrompt/StoryAnalysis/en/full/Chapter\t1091\t8A6A3C0E354A23C7E8ADBD4056654F86696938D8D81552D2EBD76B33BA911D4C",
        "GetAnalysisPrompt/StoryAnalysis/en/full/Scene\t1091\t8A6A3C0E354A23C7E8ADBD4056654F86696938D8D81552D2EBD76B33BA911D4C",
        "GetAnalysisPrompt/Summarization/he\t133\tECE6950FE729F6B69D8E2226D5B58E765660977BBE89FAD568C19894537FC1E7",
        "GetAnalysisPrompt/Summarization/he/nullContext\t133\tECE6950FE729F6B69D8E2226D5B58E765660977BBE89FAD568C19894537FC1E7",
        "GetAnalysisPrompt/Summarization/he/full/Book\t436\t4F7C63AF610C620564000782CA07220248399D98839BCD56B4083194DD80D242",
        "GetAnalysisPrompt/Summarization/he/full/Chapter\t436\t4F7C63AF610C620564000782CA07220248399D98839BCD56B4083194DD80D242",
        "GetAnalysisPrompt/Summarization/he/full/Scene\t436\t4F7C63AF610C620564000782CA07220248399D98839BCD56B4083194DD80D242",
        "GetAnalysisPrompt/Summarization/en\t161\tCFA331D3C2048CFB3F9156FA5BE906C81F9B41444847BFEFB703D91158A58207",
        "GetAnalysisPrompt/Summarization/en/nullContext\t161\tCFA331D3C2048CFB3F9156FA5BE906C81F9B41444847BFEFB703D91158A58207",
        "GetAnalysisPrompt/Summarization/en/full/Book\t464\tEB793BD1D7F1876BAB9CC621B02B721490CE3BAE09927EB9D883AD66D87DB2BA",
        "GetAnalysisPrompt/Summarization/en/full/Chapter\t464\tEB793BD1D7F1876BAB9CC621B02B721490CE3BAE09927EB9D883AD66D87DB2BA",
        "GetAnalysisPrompt/Summarization/en/full/Scene\t464\tEB793BD1D7F1876BAB9CC621B02B721490CE3BAE09927EB9D883AD66D87DB2BA",
        "GetAnalysisPrompt/Synopsis/he\t237\tC230EFF22A2A063AF7FE28D5C606E7B44CE176F09D9F29DDF49DF05BE2E12C3D",
        "GetAnalysisPrompt/Synopsis/he/nullContext\t237\tC230EFF22A2A063AF7FE28D5C606E7B44CE176F09D9F29DDF49DF05BE2E12C3D",
        "GetAnalysisPrompt/Synopsis/he/full/Book\t471\t0F7F2B2D87154EBB511CB0D613E3F7C695AB2FBD029AE237ABE851C8CD53F194",
        "GetAnalysisPrompt/Synopsis/he/full/Chapter\t471\t0F7F2B2D87154EBB511CB0D613E3F7C695AB2FBD029AE237ABE851C8CD53F194",
        "GetAnalysisPrompt/Synopsis/he/full/Scene\t471\t0F7F2B2D87154EBB511CB0D613E3F7C695AB2FBD029AE237ABE851C8CD53F194",
        "GetAnalysisPrompt/Synopsis/en\t319\tA93D3D93E21D404765EE9942A067CC89CDBB6E9D66DB1C569FCAEC4B4D680415",
        "GetAnalysisPrompt/Synopsis/en/nullContext\t319\tA93D3D93E21D404765EE9942A067CC89CDBB6E9D66DB1C569FCAEC4B4D680415",
        "GetAnalysisPrompt/Synopsis/en/full/Book\t553\tECFF2D114C4B6A0820EE5C70F433DF3EC5C27F1F58A6BBFC026F9B8968219341",
        "GetAnalysisPrompt/Synopsis/en/full/Chapter\t553\tECFF2D114C4B6A0820EE5C70F433DF3EC5C27F1F58A6BBFC026F9B8968219341",
        "GetAnalysisPrompt/Synopsis/en/full/Scene\t553\tECFF2D114C4B6A0820EE5C70F433DF3EC5C27F1F58A6BBFC026F9B8968219341",
        "BuildProofreadChunkPrompt/he/armOff/bare\t1581\t614A43298FAE466C86AAE0B6C3BF7C432891AA8E79ACFAE66CBA608F81792DEE",
        "BuildProofreadChunkPrompt/he/armOff/register\t1815\t71AF0DCF2547FB115CE94FFBFC390EB050BFE3BE7B0314FEC7FB792DA8DB9577",
        "BuildProofreadChunkPrompt/he/armOff/overlap\t1648\tB31F5141458CD90E89C7F4850DA26D589B0345C81A6DB3681B5337DD1A909FEA",
        "BuildProofreadChunkPrompt/he/armOff/both\t1882\t460689DFDA9A205FAF0CCB6B3BC8E52F38482E90076A51B3677F1467C079164E",
        "BuildProofreadChunkPrompt/he/armOn/bare\t1695\t352CFC671A0CB3E5320B15DF0428C4607C7443D4EBCB08FB9D4F500D4B8E6FEF",
        "BuildProofreadChunkPrompt/he/armOn/register\t1929\tD1C9CBD860CB9D36D644BFC1C80F3B56B8CE1AC70B1DA0BFCB922FB8AF82337F",
        "BuildProofreadChunkPrompt/he/armOn/overlap\t1762\tF2DC519E6884A3E0C0FB7108B560E67C62C857B551E4CE02EBF507A75444C39F",
        "BuildProofreadChunkPrompt/he/armOn/both\t1996\t0F36F8A875D38F373C2BFAC82D0BE92B2CFEEC27FD51E1E11201CCB1B298BD45",
        "BuildProofreadChunkPrompt/en/armOff/bare\t1161\t9F479C491869657FC8E69E631477C36CC2A2E146D73D7DE01FF5552799BF1861",
        "BuildProofreadChunkPrompt/en/armOff/register\t1395\tCD00784827488FDD34C84432468AFF1597A8B7A5D8129F811204CD830802FA13",
        "BuildProofreadChunkPrompt/en/armOff/overlap\t1228\t33400F058C3A0FFFA5FA7A7AF76638A61A739D7CC3C1C218E965CF20E23555EF",
        "BuildProofreadChunkPrompt/en/armOff/both\t1462\tE9F68C5851E5A4FE8C10171B41DD378B98C537612AE89E2AD0263245EC308B18",
        "BuildProofreadChunkPrompt/en/armOn/bare\t1316\t3E3B365927C454531E0108BDA3274D96FD54F4D7C0CFEF1F4AC4524464F79915",
        "BuildProofreadChunkPrompt/en/armOn/register\t1550\t11A7733119FB66258ADD9D5980F9C79916804840470D631628E374DC595D4BE1",
        "BuildProofreadChunkPrompt/en/armOn/overlap\t1383\t49EF2ED37FFB5E197788599D92EA19C924E3D25789A2376036C86706E6B8E6A3",
        "BuildProofreadChunkPrompt/en/armOn/both\t1617\t5D7DA06BC770927ED4FD476C0883C00620F1570EB3675A1F57FF6CA10E7386C2",
        "BuildLineEditChunkPrompt/he/first\t2127\t9082560945830EDDB9E423E5C39FF467EDC7F14B851CD64424AA4710A1B5FD3F",
        "BuildLineEditChunkPrompt/he/middle\t2110\tA403CA728419C397D6FEF8E5577F6AF5E015E4414D00CF25EA2311B9D8BED5AD",
        "BuildLineEditChunkPrompt/he/last\t2128\tAFA507E35F8E1E530250B0B436CFD4415D5AC2070F17235315C966747CB47219",
        "BuildLineEditChunkPrompt/he/only\t2145\t01EA9F1E40E7EF1668727A17453D6539513783FFAE27081C7DAA279B9EA4626D",
        "BuildLineEditChunkPrompt/en/first\t2488\t4B96572086093D7FBCEB6252838E521D8D505D62C3CE6F3C3500486328CCAE2E",
        "BuildLineEditChunkPrompt/en/middle\t2471\tA4FFE1BDAD775D4B7F05379C701689F8EEE6D99F471FE0EE1055839E1C129669",
        "BuildLineEditChunkPrompt/en/last\t2489\t567F7375DB6366B0928AB1E5AC447F01872C0CDCC4D961B95EB402745AEF9846",
        "BuildLineEditChunkPrompt/en/only\t2506\t5D5EE0BE8E91952498A0D191F640A94DC2B80929586D0CCA10AA7A48F9B5A2F7",
        "GetCharacterExtractionPrompt/he\t474\t23485019004CEC5ED8CBF3E771C21EF6F798509595D2F9D696F2B60722D001D9",
        "GetExplainSuggestionPrompt/he/withReason\t285\t5583C23AF78B5556EE2F04DC93DF62C68789B4CBE66425B64E6174D47DC8B30C",
        "GetExplainSuggestionPrompt/he/nullReason\t278\t04BA9F7302042642156085F33125FEAB815B49B2DEA5A4D49A2F48EB566F3987",
        "GetStructuredChapterBriefPrompt/he\t564\t4DBF3031BDC8CDBF1E2A50611E5633D88972D78ECA734A69EBDF71BACB7EAE83",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/he/seeded\t814\t242251CDA491E05505963CEBD6E7FD3917A187EEC1A3E1C4C89FEC70A4E9253B",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/he/blank\t564\t4DBF3031BDC8CDBF1E2A50611E5633D88972D78ECA734A69EBDF71BACB7EAE83",
        "GetCharacterExtractionPrompt/en\t622\tCCA3F921C318F0F93E02479CB54A44A04F408F4E1FC3F55AD0AF698F31CBB3C7",
        "GetExplainSuggestionPrompt/en/withReason\t325\tF216516FBC2111525FC3828847F19DF5C4324A8F1C4B3404D30D3B3F90D83C1F",
        "GetExplainSuggestionPrompt/en/nullReason\t322\t5F8F007348ACD04EB603CDFBFE2B65E8B418C87E15690F3DA591F8C494FC8A3A",
        "GetStructuredChapterBriefPrompt/en\t753\t2440C8F29ED17787E728A9FDD397CCE4C00967FB5FFB44858E7AD217C4BF979D",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/en/seeded\t1127\t547718B64E57661499F317426103686CB3B2323178732A0A3690FEDFAAEF5246",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/en/blank\t753\t2440C8F29ED17787E728A9FDD397CCE4C00967FB5FFB44858E7AD217C4BF979D",
        "BuildBookReviewPrompt/plot/he\t3131\tBC1A53ED6B39C7A609FF7EFC2D4FDA120A54C68CA52D628C048CB36D923AC6A9",
        "BuildBookReviewPrompt/character/he\t3133\t1AB31B7635511D950DD9607A7189C2BA6EE0138122EB771520FD112FAA514148",
        "BuildBookReviewPrompt/pacing/he\t3145\t400B51D71CEC2F23F8F95BAC3096219D15582ECD6C7A97E757FC1C9C86FE3FDB",
        "BuildBookReviewPrompt/tone/he\t3127\tABAED085470E26C88B953EE8E032C1982A2F317A129C301AA42EEED1C79F92DB",
        "BuildBookReviewPrompt/theme/he\t3142\t0F71D8A46EE7844930C15E3A9D0A93E5646C3D7E509084DD3D4297ED53F9CCD9",
        "BuildBookReviewPrompt/continuity/he\t3159\t38742D40CDD0EFBB9BC83521F8E35CD9F3CBC1B0CCC4FA4D4589C09E1CED2611",
        "BuildBookReviewPrompt/unknown-dimension/he\t3131\tBC1A53ED6B39C7A609FF7EFC2D4FDA120A54C68CA52D628C048CB36D923AC6A9",
        "BuildBookReviewCombinedPrompt/he\t3302\t7AB7DC77520BD8FD625051DC1BD8CAA988E6586AEE9E17AF0AD93E7C2298F4FF",
        "BuildChapterAnchorAllowlistRule/he/empty\t200\t7E5890BBC6BDBE4E63946181A3A8694CA0326FA48B344F03FEB5719DEC715DF5",
        "BuildChapterAnchorAllowlistRule/he/orders\t402\t2E6B69B9F755FFFF3F821FEE1C1FA71719BA015EC2EE79795FC73A266C4ED4FA",
        "BuildBookReviewWindowPrompt/he/first\t3873\tD2CB51BDB5BB024348CD56A2BFCAD9C0387DE10F1E9FEE0B3315372ECFA38134",
        "BuildBookReviewWindowPrompt/he/middle\t3875\t7AD72416B5B99986306FF12F79739E3988FD2BC19E9E0E965B0B5F9CB286FBD1",
        "BuildBookReviewSynthesisPrompt/he\t4566\t98BD7F5A88B5B4CD283ED868B288919D380A8FD855CA794881C03B34ABAB045C",
        "BuildBookReviewContinuityReducePrompt/he\t2702\t70E1AC58AF9D4FF705F45E1E9960C3D2083A68D356E745F821AD7ED63BDEAF4A",
        "BuildBookReviewPrompt/plot/en\t3847\t4F48FB8E8EC28B7208264456A67B3DB7FDDD2FB217A22F0D3488A820D870819A",
        "BuildBookReviewPrompt/character/en\t3866\t3AB1D45F548801D4428085B850BF1A32B3AED39DD94602ED51965F85E808499C",
        "BuildBookReviewPrompt/pacing/en\t3859\t788249461872606A1D83CAF2FAE803160997223EB301FFB345422120320EF326",
        "BuildBookReviewPrompt/tone/en\t3842\tAB3F562465F4B6A152AFF1F550CB7370E010EA3FE8833A336EF6A24081367C42",
        "BuildBookReviewPrompt/theme/en\t3880\tDD857FAD4FE22653867DBF74097E4A317429349A0B36DA9A61B28BC45201A938",
        "BuildBookReviewPrompt/continuity/en\t3887\t0AC52D32BC9058ABD7551A92DDADA6843A88C7B7EDEE3A532E077DEFA1FD299A",
        "BuildBookReviewPrompt/unknown-dimension/en\t3847\t4F48FB8E8EC28B7208264456A67B3DB7FDDD2FB217A22F0D3488A820D870819A",
        "BuildBookReviewCombinedPrompt/en\t3933\t0EB4BE0DF2B0CE521654BA7E7FD3CE88FBDCBBB76C7D65D7158A7D60A879CE2C",
        "BuildChapterAnchorAllowlistRule/en/empty\t277\t0B869FC19DD2A65B500F0D1BDD4977E9EB3FD558B7CC9A54EAD49217B7BE26C3",
        "BuildChapterAnchorAllowlistRule/en/orders\t540\tCF95EB79CA63FAC3A4805FBAE2EF67F2355CAE30A74DE3C6EE8A43B152092A16",
        "BuildBookReviewWindowPrompt/en/first\t4605\t2BEA02066380C5CEEFA2E9B2B326DD34D8602DB499BA811DD7FDD84B479514E4",
        "BuildBookReviewWindowPrompt/en/middle\t4607\tBF4B6FF87DDC8920D5360B8306F54BCE4A259877A217067F71BD843FA9C7DF7C",
        "BuildBookReviewSynthesisPrompt/en\t5488\t35334A909B7D65D9B6931ED626BD8B7744E370D02F80C6CB706AB72812CE902F",
        "BuildBookReviewContinuityReducePrompt/en\t3304\t3E815095299CEF45ABF6E8167FEFBF0CF4E18EA65403F9C3AFACCC1F71CD3A07",
        "FormatCharactersForBookReview\t146\tD13D6A5332DB2A41D5495648D8F73607E81361E9E1BA77854D5D8AC37B47C85D",
    });
}
