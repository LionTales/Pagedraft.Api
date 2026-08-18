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
    /// <summary>
    /// The one measurement every pinned row is taken with: "length\tsha256" of the value after its line
    /// endings are normalized to "\n". <see cref="BuildManifest"/> and
    /// <see cref="ThePin_MeasuresTheSamePrompt_WhicheverLineEndingsTheCheckoutHas"/> BOTH route through
    /// here on purpose, so that deleting the normalization cannot quietly re-introduce the checkout
    /// dependency: it turns that test red at the same moment it changes the manifest.
    /// </summary>
    private static string Measure(string value)
    {
        var normalized = value.ReplaceLineEndings("\n");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"{normalized.Length}\t{Convert.ToHexString(bytes)}";
    }

    /// <summary>
    /// The pin must describe the PROMPT, not the working copy it was composed in. This is the property
    /// that broke on this manifest's first push (green on a CRLF Windows checkout, red on the LF CI
    /// runner), so it is pinned rather than left to the comment in <see cref="BuildManifest"/>.
    /// </summary>
    [Fact]
    public void ThePin_MeasuresTheSamePrompt_WhicheverLineEndingsTheCheckoutHas()
    {
        var (_, instruction) = Unconfigured().GetPrompt(AiTaskType.LineEdit, "he");

        // NON-VACUITY FLOOR. A single-line prompt would satisfy the assertion below no matter how Measure
        // behaved, so the surface this test drives has to actually carry line breaks for it to prove
        // anything. If LineEdit/he ever becomes one line, move this to a surface that is not.
        Assert.Contains('\n', instruction);

        Assert.Equal(
            Measure(instruction.ReplaceLineEndings("\r\n")),
            Measure(instruction.ReplaceLineEndings("\n")));
    }

    private static string BuildManifest()
    {
        var rows = new List<string>();

        // LINE ENDINGS ARE NORMALIZED TO "\n" BEFORE MEASURING, and that is load-bearing rather than tidy.
        // Nearly every template here is a multi-line raw string literal, and a raw string literal carries the
        // line endings of its SOURCE FILE verbatim. With core.autocrlf the source is CRLF in a Windows working
        // copy and LF on the Linux CI runner, so the SAME prompt composes one byte per line longer on a dev box
        // than in CI. Pinning the raw bytes therefore pins the checkout, not the prompt: the manifest stamped
        // here went green locally and red in CI on its very first push, at GetPrompt/LineEdit/he/instruction,
        // 1160 chars against 1139, which is exactly that template's 21 line breaks.
        //
        // This follows the convention BookContextAssemblerTests already set for the same hazard (see the comment
        // on BriefJson there, which records the same green-locally/red-in-CI failure in a token budget). Pinning
        // the endings makes a row mean the same thing on every machine, whatever anyone's core.autocrlf is.
        //
        // THE COMPOSED PROMPTS ARE MIXED, which is why normalizing beats picking CRLF. Re-stamping this manifest
        // in the all-CRLF form was tried first and does NOT reproduce the original pin: the first 81 rows match
        // and then GetAnalysisPrompt/LineEdit/he/full/Book comes out 2010 against a pinned 1998. Those 12 chars
        // are line breaks the CONTEXT PREAMBLE contributes as bare "\n" while the template around it contributes
        // CRLF from the source file. So a composed prompt is not CRLF or LF, it is both at once, in a ratio that
        // depends on how much preamble the call happens to build. There is no raw form to pin that is stable
        // across machines, and "\n" is the only representation every row can agree on.
        //
        // WHAT THIS GIVES UP, stated so nobody reads the pin as stronger than it is: after normalizing, this
        // test can no longer see a change that is PURELY line endings. That is the right trade only because the
        // endings here are decided by git rather than by the prompt author, so a diff in them is not a change
        // anyone made. It also means the standing gold and gate numbers, all measured on Windows, were measured
        // against the CRLF form of these prompts, while a Linux build sends the LF form. Whether the composed
        // prompt should be normalized AT THE SOURCE so the deployed bytes match the measured ones is a real and
        // separate question, and a behavior change that needs its own re-measurement - not a cleanup edit.
        void Pin(string key, string value) => rows.Add($"{key}\t{Measure(value)}");

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
        "GetPrompt/LineEdit/he/instruction\t1139\tA931EB6A7BC3DE47D1450CDFC899976B7EE62234337182DD1C2D37B829F6C3A5",
        "GetPrompt/LineEdit/en/system\t202\tA0F3C74DEBA2EADEA6178BE5A57A536D65364DAF6A32729367EB99399BF25607",
        "GetPrompt/LineEdit/en/instruction\t1483\tB0B91D0B44F0BD7B424D64C579843FC932BCB8663FECB6B063D4AF873C3CCD89",
        "GetPrompt/LinguisticAnalysis/he/system\t422\t104809ED5998BE600AEF38EE6A00D37CB0E7AC23EC0ACA4C7C4607EC0F26528A",
        "GetPrompt/LinguisticAnalysis/he/instruction\t168\t14CEF296C3A3061F1E8CAC7E06A95189FB32B1A32B77890127BE3C015EAD5EE5",
        "GetPrompt/LinguisticAnalysis/en/system\t240\t362705C2BBAFBEE517BAA0B6CE2D59F853554278F7FBE8DBDBA0398E79F0A7A0",
        "GetPrompt/LinguisticAnalysis/en/instruction\t196\tF071C7B2350EBBA3EDAD63F144240D08B3A3BEDED7E10B422922A537B70891FE",
        // RE-STAMPED BY g3, DELIBERATELY, AND THESE TWO ROWS ONLY. ProductChat's system surface is
        // ProductChatPrompt's Union book-less message, whose book refusal told the author that answering
        // about a specific book "is not available yet and is coming" - false since phase B taught Show to
        // read the book, and measured reaching a real user on 5 of 102 turns in g3's live run. It now says
        // what ProductChatService's deterministic path says: "I can only see a book while it is open".
        // he 1082 -> 1061 chars, en 1429 -> 1368. The manifest was diffed row by row against the previous
        // stamp and NOTHING ELSE MOVED (2 of 225 rows), which is what says the edit stayed inside the one
        // block it was aimed at. The prose pins for the same change were re-typed by hand out of
        // ProductChatPromptBlocks; a SHA cannot be, so this row is a re-measurement and is recorded as one.
        //
        // RE-STAMPED AGAIN BY g3b, THE SAME TWO ROWS AND NO OTHERS. Two blocks on this surface moved.
        // (1) BookRefusalEn was given the Hebrew twin's construction - the finished first-person sentence
        //     in quotes, word for word ProductChatService.OpenTheBookEn - because the English half had been
        //     left as the imperative that BookRefusalHe's docstring records the model reading back verbatim;
        //     and its closing clause stopped naming the source ("from the guides" -> "from what is written
        //     below"). BookRefusalHe's closing clause moved the same way, its quoted sentence untouched.
        // (2) CitationLineEn/He dropped the source noun from the DESCRIPTION around the label ("naming the
        //     guide ids you used" -> "naming the ids you used"). The quoted label itself did not move, so
        //     the parser and the reader's chips are unaffected; see that block for why it is not being bet.
        // he 1061 -> 1055 chars, en 1368 -> 1432. Diffed row by row against the g3 stamp: 2 of 225 rows
        // moved, which is what says a change aimed at the product route did not leak into another surface.
        // Same discipline as above - the prose pins were re-typed by hand out of ProductChatPromptBlocks
        // and only these SHAs are a re-measurement.
        "GetPrompt/ProductChat/he/system\t1055\t2ED9495919DEE32DC2DCCD982D2F983BFF9E0867FD7601813261CD073E7E3084",
        "GetPrompt/ProductChat/he/instruction\t0\tE3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
        "GetPrompt/ProductChat/en/system\t1432\tAC6630B944DBA899EABE02E330062DA3CFC7E3F1D553F4520A3D71650512A31C",
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
        "GetAnalysisPrompt/BookOverview/he\t484\t59EF84A48752FC7F90CBE43D6D745D254E78E054EE5BF282B3556BDBC490967B",
        "GetAnalysisPrompt/BookOverview/he/nullContext\t484\t59EF84A48752FC7F90CBE43D6D745D254E78E054EE5BF282B3556BDBC490967B",
        "GetAnalysisPrompt/BookOverview/he/full/Book\t484\t59EF84A48752FC7F90CBE43D6D745D254E78E054EE5BF282B3556BDBC490967B",
        "GetAnalysisPrompt/BookOverview/he/full/Chapter\t484\t59EF84A48752FC7F90CBE43D6D745D254E78E054EE5BF282B3556BDBC490967B",
        "GetAnalysisPrompt/BookOverview/he/full/Scene\t484\t59EF84A48752FC7F90CBE43D6D745D254E78E054EE5BF282B3556BDBC490967B",
        "GetAnalysisPrompt/BookOverview/en\t594\t24CE34A499178DBE81D0B125CD36E0E706599BC21D294588506118607AE5CB37",
        "GetAnalysisPrompt/BookOverview/en/nullContext\t594\t24CE34A499178DBE81D0B125CD36E0E706599BC21D294588506118607AE5CB37",
        "GetAnalysisPrompt/BookOverview/en/full/Book\t594\t24CE34A499178DBE81D0B125CD36E0E706599BC21D294588506118607AE5CB37",
        "GetAnalysisPrompt/BookOverview/en/full/Chapter\t594\t24CE34A499178DBE81D0B125CD36E0E706599BC21D294588506118607AE5CB37",
        "GetAnalysisPrompt/BookOverview/en/full/Scene\t594\t24CE34A499178DBE81D0B125CD36E0E706599BC21D294588506118607AE5CB37",
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
        "GetAnalysisPrompt/CharacterAnalysis/he\t573\t20F6DC9C5FA8518F1604B2C6F20C2E107B0AD15B8810037775AF077665CADC21",
        "GetAnalysisPrompt/CharacterAnalysis/he/nullContext\t573\t20F6DC9C5FA8518F1604B2C6F20C2E107B0AD15B8810037775AF077665CADC21",
        "GetAnalysisPrompt/CharacterAnalysis/he/full/Book\t573\t20F6DC9C5FA8518F1604B2C6F20C2E107B0AD15B8810037775AF077665CADC21",
        "GetAnalysisPrompt/CharacterAnalysis/he/full/Chapter\t573\t20F6DC9C5FA8518F1604B2C6F20C2E107B0AD15B8810037775AF077665CADC21",
        "GetAnalysisPrompt/CharacterAnalysis/he/full/Scene\t573\t20F6DC9C5FA8518F1604B2C6F20C2E107B0AD15B8810037775AF077665CADC21",
        "GetAnalysisPrompt/CharacterAnalysis/en\t717\tC1343DF486E923FC61856E3FBA2DBA4C8E6FCA4A187C0F592597D4E2FD255A65",
        "GetAnalysisPrompt/CharacterAnalysis/en/nullContext\t717\tC1343DF486E923FC61856E3FBA2DBA4C8E6FCA4A187C0F592597D4E2FD255A65",
        "GetAnalysisPrompt/CharacterAnalysis/en/full/Book\t717\tC1343DF486E923FC61856E3FBA2DBA4C8E6FCA4A187C0F592597D4E2FD255A65",
        "GetAnalysisPrompt/CharacterAnalysis/en/full/Chapter\t717\tC1343DF486E923FC61856E3FBA2DBA4C8E6FCA4A187C0F592597D4E2FD255A65",
        "GetAnalysisPrompt/CharacterAnalysis/en/full/Scene\t717\tC1343DF486E923FC61856E3FBA2DBA4C8E6FCA4A187C0F592597D4E2FD255A65",
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
        "GetAnalysisPrompt/LineEdit/he\t1139\tA931EB6A7BC3DE47D1450CDFC899976B7EE62234337182DD1C2D37B829F6C3A5",
        "GetAnalysisPrompt/LineEdit/he/nullContext\t1139\tA931EB6A7BC3DE47D1450CDFC899976B7EE62234337182DD1C2D37B829F6C3A5",
        "GetAnalysisPrompt/LineEdit/he/full/Book\t1970\t080864668DC2E952A5CBFB633A34D58D62729F9CAE6AE93C17FB798EF6D98278",
        "GetAnalysisPrompt/LineEdit/he/full/Chapter\t1970\t080864668DC2E952A5CBFB633A34D58D62729F9CAE6AE93C17FB798EF6D98278",
        "GetAnalysisPrompt/LineEdit/he/full/Scene\t1970\t080864668DC2E952A5CBFB633A34D58D62729F9CAE6AE93C17FB798EF6D98278",
        "GetAnalysisPrompt/LineEdit/en\t1483\tB0B91D0B44F0BD7B424D64C579843FC932BCB8663FECB6B063D4AF873C3CCD89",
        "GetAnalysisPrompt/LineEdit/en/nullContext\t1483\tB0B91D0B44F0BD7B424D64C579843FC932BCB8663FECB6B063D4AF873C3CCD89",
        "GetAnalysisPrompt/LineEdit/en/full/Book\t2314\tF04012E5DDE04A521020AA89D0DAEF2E2A28955C3AAB04AE6826ACC03A8A7991",
        "GetAnalysisPrompt/LineEdit/en/full/Chapter\t2314\tF04012E5DDE04A521020AA89D0DAEF2E2A28955C3AAB04AE6826ACC03A8A7991",
        "GetAnalysisPrompt/LineEdit/en/full/Scene\t2314\tF04012E5DDE04A521020AA89D0DAEF2E2A28955C3AAB04AE6826ACC03A8A7991",
        "GetAnalysisPrompt/LinguisticAnalysis/he\t7748\t8EABF95A1CC61DDA061A8B75D9017E80C443EE678BF19F64CD5488E0C2A0EDFD",
        "GetAnalysisPrompt/LinguisticAnalysis/he/nullContext\t7748\t8EABF95A1CC61DDA061A8B75D9017E80C443EE678BF19F64CD5488E0C2A0EDFD",
        "GetAnalysisPrompt/LinguisticAnalysis/he/full/Book\t8835\t1732EEC166A487E7FB9888DB9273BCD019019EC64494FD781F21E4D1649E8B2E",
        "GetAnalysisPrompt/LinguisticAnalysis/he/full/Chapter\t8835\t1732EEC166A487E7FB9888DB9273BCD019019EC64494FD781F21E4D1649E8B2E",
        "GetAnalysisPrompt/LinguisticAnalysis/he/full/Scene\t8751\tB212CEA145240C6325B212DF70C2F7C88B97227FB5471189BAE297D323753823",
        "GetAnalysisPrompt/LinguisticAnalysis/en\t9260\t4D781E8C11558B2BE6EDE761E3BB80120F620D8030F21FA1516DB4DFF96FB82C",
        "GetAnalysisPrompt/LinguisticAnalysis/en/nullContext\t9260\t4D781E8C11558B2BE6EDE761E3BB80120F620D8030F21FA1516DB4DFF96FB82C",
        "GetAnalysisPrompt/LinguisticAnalysis/en/full/Book\t10347\t17E75DF83EE21114AC3FA5F13C9EBD6A2F7A8F2F691A5B4A48348511CA808E5F",
        "GetAnalysisPrompt/LinguisticAnalysis/en/full/Chapter\t10347\t17E75DF83EE21114AC3FA5F13C9EBD6A2F7A8F2F691A5B4A48348511CA808E5F",
        "GetAnalysisPrompt/LinguisticAnalysis/en/full/Scene\t10263\t5B1B41233FBBF557E656BF71F5B24A6DA9B42BECAF02F4910E3144EB557151F0",
        "GetAnalysisPrompt/LiteraryAnalysis/he\t764\t7910516B72A416982AD7ED4A29440DD047B72DEB81C7A6EFA4F8A6EDB30A8CBE",
        "GetAnalysisPrompt/LiteraryAnalysis/he/nullContext\t764\t7910516B72A416982AD7ED4A29440DD047B72DEB81C7A6EFA4F8A6EDB30A8CBE",
        "GetAnalysisPrompt/LiteraryAnalysis/he/full/Book\t1728\t611353474548824BF30195AA3B1951456C15B553B15C880FFC48CF105F6F3550",
        "GetAnalysisPrompt/LiteraryAnalysis/he/full/Chapter\t1728\t611353474548824BF30195AA3B1951456C15B553B15C880FFC48CF105F6F3550",
        "GetAnalysisPrompt/LiteraryAnalysis/he/full/Scene\t1728\t611353474548824BF30195AA3B1951456C15B553B15C880FFC48CF105F6F3550",
        "GetAnalysisPrompt/LiteraryAnalysis/en\t964\t8B94E7B962BB8E1E7D229CAFA5AA2ED6F9F5A0FE9148C8165C89332B885EF2A9",
        "GetAnalysisPrompt/LiteraryAnalysis/en/nullContext\t964\t8B94E7B962BB8E1E7D229CAFA5AA2ED6F9F5A0FE9148C8165C89332B885EF2A9",
        "GetAnalysisPrompt/LiteraryAnalysis/en/full/Book\t1928\t0B7352DF2C7D70BB0F350B232C3F31F2A798E6CE6CC9754A6E19E803603789E5",
        "GetAnalysisPrompt/LiteraryAnalysis/en/full/Chapter\t1928\t0B7352DF2C7D70BB0F350B232C3F31F2A798E6CE6CC9754A6E19E803603789E5",
        "GetAnalysisPrompt/LiteraryAnalysis/en/full/Scene\t1928\t0B7352DF2C7D70BB0F350B232C3F31F2A798E6CE6CC9754A6E19E803603789E5",
        "GetAnalysisPrompt/Proofread/he\t1566\t4B342DAD16CA2D7E7613C59CFF007FA22087DFE6D055B44555FA9A7738742211",
        "GetAnalysisPrompt/Proofread/he/nullContext\t1566\t4B342DAD16CA2D7E7613C59CFF007FA22087DFE6D055B44555FA9A7738742211",
        "GetAnalysisPrompt/Proofread/he/full/Book\t2200\tA1A6D5784CCE68F263E4D941CB0BAC644B4EC84B06C35AC16E019239908968C4",
        "GetAnalysisPrompt/Proofread/he/full/Chapter\t2200\tA1A6D5784CCE68F263E4D941CB0BAC644B4EC84B06C35AC16E019239908968C4",
        "GetAnalysisPrompt/Proofread/he/full/Scene\t2200\tA1A6D5784CCE68F263E4D941CB0BAC644B4EC84B06C35AC16E019239908968C4",
        "GetAnalysisPrompt/Proofread/en\t1149\t7A2E39B16309FA9DFA2F86CB56F1E6D125175748B9C6AD4F3F8B6A93BA211BD9",
        "GetAnalysisPrompt/Proofread/en/nullContext\t1149\t7A2E39B16309FA9DFA2F86CB56F1E6D125175748B9C6AD4F3F8B6A93BA211BD9",
        "GetAnalysisPrompt/Proofread/en/full/Book\t1783\t02F29A436B459FF727F42C81FFB89BBABA413B0B6695886968CF595FC9220B50",
        "GetAnalysisPrompt/Proofread/en/full/Chapter\t1783\t02F29A436B459FF727F42C81FFB89BBABA413B0B6695886968CF595FC9220B50",
        "GetAnalysisPrompt/Proofread/en/full/Scene\t1783\t02F29A436B459FF727F42C81FFB89BBABA413B0B6695886968CF595FC9220B50",
        "GetAnalysisPrompt/QA/he\t746\t9F38D28BC6FCCC83CC80649FD7E6AD2D692FDAE091CBADF4EC43DEF862DFD01C",
        "GetAnalysisPrompt/QA/he/nullContext\t746\t9F38D28BC6FCCC83CC80649FD7E6AD2D692FDAE091CBADF4EC43DEF862DFD01C",
        "GetAnalysisPrompt/QA/he/full/Book\t1154\tDA1F4FC7E9D8FBC3F45DD9EA2B1A0A632C75871E86791CB89A2D30CA883DAEB2",
        "GetAnalysisPrompt/QA/he/full/Chapter\t1154\tDA1F4FC7E9D8FBC3F45DD9EA2B1A0A632C75871E86791CB89A2D30CA883DAEB2",
        "GetAnalysisPrompt/QA/he/full/Scene\t1154\tDA1F4FC7E9D8FBC3F45DD9EA2B1A0A632C75871E86791CB89A2D30CA883DAEB2",
        "GetAnalysisPrompt/QA/en\t577\tD5D3504D6EF411FD6C9B60AC687CEA4B4FC0875EDAFA7206914CE0836C4369AB",
        "GetAnalysisPrompt/QA/en/nullContext\t577\tD5D3504D6EF411FD6C9B60AC687CEA4B4FC0875EDAFA7206914CE0836C4369AB",
        "GetAnalysisPrompt/QA/en/full/Book\t985\tC743914D210A655028DB8BB92ED728A31DE2FE5F128A82CF229F84C278E19CB6",
        "GetAnalysisPrompt/QA/en/full/Chapter\t985\tC743914D210A655028DB8BB92ED728A31DE2FE5F128A82CF229F84C278E19CB6",
        "GetAnalysisPrompt/QA/en/full/Scene\t985\tC743914D210A655028DB8BB92ED728A31DE2FE5F128A82CF229F84C278E19CB6",
        "GetAnalysisPrompt/StoryAnalysis/he\t714\tE5BF3324F08C6EB294D82E8C72914FEE65DC31BD0DE75CCF95E1BEE754FBF414",
        "GetAnalysisPrompt/StoryAnalysis/he/nullContext\t714\tE5BF3324F08C6EB294D82E8C72914FEE65DC31BD0DE75CCF95E1BEE754FBF414",
        "GetAnalysisPrompt/StoryAnalysis/he/full/Book\t891\tD0EA87F2D6CA19CA0CD56A5DBDCBBD09BA5482844CE386E0873FAFE56CDE6B2F",
        "GetAnalysisPrompt/StoryAnalysis/he/full/Chapter\t891\tD0EA87F2D6CA19CA0CD56A5DBDCBBD09BA5482844CE386E0873FAFE56CDE6B2F",
        "GetAnalysisPrompt/StoryAnalysis/he/full/Scene\t891\tD0EA87F2D6CA19CA0CD56A5DBDCBBD09BA5482844CE386E0873FAFE56CDE6B2F",
        "GetAnalysisPrompt/StoryAnalysis/en\t888\tFBFAA6D57FAABD5D8728D9FB58218BBA330AA9032F99BC90D50EC3CA24862FF4",
        "GetAnalysisPrompt/StoryAnalysis/en/nullContext\t888\tFBFAA6D57FAABD5D8728D9FB58218BBA330AA9032F99BC90D50EC3CA24862FF4",
        "GetAnalysisPrompt/StoryAnalysis/en/full/Book\t1065\t1B9CA016AB3BE65911FB3583262C7CC79EDA19B50A8BE5A426A63739EFE9BBA1",
        "GetAnalysisPrompt/StoryAnalysis/en/full/Chapter\t1065\t1B9CA016AB3BE65911FB3583262C7CC79EDA19B50A8BE5A426A63739EFE9BBA1",
        "GetAnalysisPrompt/StoryAnalysis/en/full/Scene\t1065\t1B9CA016AB3BE65911FB3583262C7CC79EDA19B50A8BE5A426A63739EFE9BBA1",
        "GetAnalysisPrompt/Summarization/he\t133\tECE6950FE729F6B69D8E2226D5B58E765660977BBE89FAD568C19894537FC1E7",
        "GetAnalysisPrompt/Summarization/he/nullContext\t133\tECE6950FE729F6B69D8E2226D5B58E765660977BBE89FAD568C19894537FC1E7",
        "GetAnalysisPrompt/Summarization/he/full/Book\t430\t3E35159F961A3F0CC4906B08DEE95D8B594314388F190810E45E375CC2D50DA6",
        "GetAnalysisPrompt/Summarization/he/full/Chapter\t430\t3E35159F961A3F0CC4906B08DEE95D8B594314388F190810E45E375CC2D50DA6",
        "GetAnalysisPrompt/Summarization/he/full/Scene\t430\t3E35159F961A3F0CC4906B08DEE95D8B594314388F190810E45E375CC2D50DA6",
        "GetAnalysisPrompt/Summarization/en\t161\tCFA331D3C2048CFB3F9156FA5BE906C81F9B41444847BFEFB703D91158A58207",
        "GetAnalysisPrompt/Summarization/en/nullContext\t161\tCFA331D3C2048CFB3F9156FA5BE906C81F9B41444847BFEFB703D91158A58207",
        "GetAnalysisPrompt/Summarization/en/full/Book\t458\t5A7C683E05F87F8C4ECFFEE24A2529C868C7BEF1AE1B6507D2A895BDB428B1ED",
        "GetAnalysisPrompt/Summarization/en/full/Chapter\t458\t5A7C683E05F87F8C4ECFFEE24A2529C868C7BEF1AE1B6507D2A895BDB428B1ED",
        "GetAnalysisPrompt/Summarization/en/full/Scene\t458\t5A7C683E05F87F8C4ECFFEE24A2529C868C7BEF1AE1B6507D2A895BDB428B1ED",
        "GetAnalysisPrompt/Synopsis/he\t237\tC230EFF22A2A063AF7FE28D5C606E7B44CE176F09D9F29DDF49DF05BE2E12C3D",
        "GetAnalysisPrompt/Synopsis/he/nullContext\t237\tC230EFF22A2A063AF7FE28D5C606E7B44CE176F09D9F29DDF49DF05BE2E12C3D",
        "GetAnalysisPrompt/Synopsis/he/full/Book\t468\t2DD25087480233AF1CCED074C5EDADF0128DCB2E4994DDF3D2CA1A8D67E5D124",
        "GetAnalysisPrompt/Synopsis/he/full/Chapter\t468\t2DD25087480233AF1CCED074C5EDADF0128DCB2E4994DDF3D2CA1A8D67E5D124",
        "GetAnalysisPrompt/Synopsis/he/full/Scene\t468\t2DD25087480233AF1CCED074C5EDADF0128DCB2E4994DDF3D2CA1A8D67E5D124",
        "GetAnalysisPrompt/Synopsis/en\t319\tA93D3D93E21D404765EE9942A067CC89CDBB6E9D66DB1C569FCAEC4B4D680415",
        "GetAnalysisPrompt/Synopsis/en/nullContext\t319\tA93D3D93E21D404765EE9942A067CC89CDBB6E9D66DB1C569FCAEC4B4D680415",
        "GetAnalysisPrompt/Synopsis/en/full/Book\t550\tC062A3858FD2E2931BFD5D63D2A8FCC4B312DC3C5C8E8CA551653365005C7536",
        "GetAnalysisPrompt/Synopsis/en/full/Chapter\t550\tC062A3858FD2E2931BFD5D63D2A8FCC4B312DC3C5C8E8CA551653365005C7536",
        "GetAnalysisPrompt/Synopsis/en/full/Scene\t550\tC062A3858FD2E2931BFD5D63D2A8FCC4B312DC3C5C8E8CA551653365005C7536",
        "BuildProofreadChunkPrompt/he/armOff/bare\t1566\t4B342DAD16CA2D7E7613C59CFF007FA22087DFE6D055B44555FA9A7738742211",
        "BuildProofreadChunkPrompt/he/armOff/register\t1797\t355219DC9F3783FA44720F99F20534166E26B5D7C638D3A41BDD664D8D178ED1",
        "BuildProofreadChunkPrompt/he/armOff/overlap\t1633\tA0578595AB21A0B3CAB7642692EC82159834C48534CE32E34FD2F93B80813464",
        "BuildProofreadChunkPrompt/he/armOff/both\t1864\t85B0BE6A4A6F7BF706F1F1074BA2F1EB92972BF453A78232CC550EDDDD548DD0",
        "BuildProofreadChunkPrompt/he/armOn/bare\t1680\t3660DDF9F16C1D47BFE8397AA48D18E88F67429522679970188639118865DDFE",
        "BuildProofreadChunkPrompt/he/armOn/register\t1911\t39578DC7286D050163FAEAD4C8BB3559AE81B52AC0A7A056441636AF419A0C44",
        "BuildProofreadChunkPrompt/he/armOn/overlap\t1747\t1E97ED224FD347CACC38173058798473BC4E1B4D4B144F6FA24109FD67FB8DDF",
        "BuildProofreadChunkPrompt/he/armOn/both\t1978\t00CA4814FADF769F578E59CAA6CC3870904838835027A958FFBB83767006945D",
        "BuildProofreadChunkPrompt/en/armOff/bare\t1149\t7A2E39B16309FA9DFA2F86CB56F1E6D125175748B9C6AD4F3F8B6A93BA211BD9",
        "BuildProofreadChunkPrompt/en/armOff/register\t1380\tA50E7D3497A311B1E8E26D939E07D18B30984071C3C9B32922ED0C575591BF73",
        "BuildProofreadChunkPrompt/en/armOff/overlap\t1216\t25EDD2FC71578CB1C689AD49C14925B3FEBBF2A281BAEC499E03EEE687EF09D4",
        "BuildProofreadChunkPrompt/en/armOff/both\t1447\t823FBB47DA07246922B64314078D096022266F9CE08476B9D7CABDF1AF6C9CA5",
        "BuildProofreadChunkPrompt/en/armOn/bare\t1304\t7B4BB9992CAE295FB5D48ED527F9338458E9748DC405AD77C562B2934875C7BB",
        "BuildProofreadChunkPrompt/en/armOn/register\t1535\t2E1EF372E1834882D485CE1B67D44D557400F71F725434975C84227C8E6A5A82",
        "BuildProofreadChunkPrompt/en/armOn/overlap\t1371\t96AD81818D45376F5444CDD338935F58AAA50D54606210901D33A2001961CE37",
        "BuildProofreadChunkPrompt/en/armOn/both\t1602\t799FD7263D8BC22D75D04A6DD03CF7E9C161448957A0C42B9E2B95095949F539",
        "BuildLineEditChunkPrompt/he/first\t2097\tAFC1F86A2A8402DAB93BAE287BE0707AE58D5C97692B6ABC263F05B5F1B0515D",
        "BuildLineEditChunkPrompt/he/middle\t2080\tB4DF89F9ED3EAAF7C07F31A5EBEF5558D32CCF4E92F34453CAB184C811B42A6C",
        "BuildLineEditChunkPrompt/he/last\t2098\tD3D624E7866B492378EB310DF8A0A34C08A465D5D25FE771443BD76C83D91648",
        "BuildLineEditChunkPrompt/he/only\t2115\tA8A0ACDF3A2F29F87A09F1F8B95B903FD1E55671B2D194058034AED8C6EE32BA",
        "BuildLineEditChunkPrompt/en/first\t2458\t7C1E71B85D28900A865D6D1723AC66C9950995FC30799C42333E7BA1E09EEF03",
        "BuildLineEditChunkPrompt/en/middle\t2441\tF61AC2335CD2C4E41870792B0BEE215BC1BCF89D2DFF834C24452B0CAB0F63B5",
        "BuildLineEditChunkPrompt/en/last\t2459\tDD858B06384E7F21F966FA1179FD81411E394FD081AF5D6BED3D656AF96A5E92",
        "BuildLineEditChunkPrompt/en/only\t2476\t76994F90C217696BE67DDE003D128AB0311D67D00E18658A7EDFA367B34DC325",
        "GetCharacterExtractionPrompt/he\t465\t5D34306F3ACB82F419BDD82BE6DC04329E2CC6612891CEDE67701693AA5CD41B",
        "GetExplainSuggestionPrompt/he/withReason\t279\t4D8BF0B5B15721849BBA10D79C744F86AC8F6FAA369E5C614F96F514262279ED",
        "GetExplainSuggestionPrompt/he/nullReason\t272\t68F823888DB026AED7F0C58E5E123D069F0453757E277926C567DCCF8E9ECEE0",
        "GetStructuredChapterBriefPrompt/he\t553\tCC263FDC77EC648EE60B4CABDBA5CE3CB82EBC44C5CABD04B45FFD904D601C14",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/he/seeded\t796\t875CA3472106514834204DB726016EA76EC8F82AB34327C7BD7C9FD7A0596D31",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/he/blank\t553\tCC263FDC77EC648EE60B4CABDBA5CE3CB82EBC44C5CABD04B45FFD904D601C14",
        "GetCharacterExtractionPrompt/en\t613\t55A7503BFE414FDA6A0E53074D244F9061EFFAA92AC981516E6AC29B366B2394",
        "GetExplainSuggestionPrompt/en/withReason\t319\t1F8201A421E2D1F2F9EE2E384ADFAD097DC84D9B0CC75C55EE966A7A18890D6C",
        "GetExplainSuggestionPrompt/en/nullReason\t316\t0C0AB13B352095DEA398F37343595C288525F2257636916E47EEBA7F2826F9E4",
        "GetStructuredChapterBriefPrompt/en\t742\t80FD153A8691F8FD573242D93F8057DE651A18538E0FB2A03941073AEDAF366F",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/en/seeded\t1107\tEAEF589FF1B2945E92F63ACCD8219A4C7B611A738992096C7935EA061760EEB5",
        "GetStructuredChapterBriefPromptSeededWithUserSummary/en/blank\t742\t80FD153A8691F8FD573242D93F8057DE651A18538E0FB2A03941073AEDAF366F",
        "BuildBookReviewPrompt/plot/he\t3093\tBE74FE367055CCFF38420202D880939EA3A105B39B3D1E315F3B5AFE22FECDCB",
        "BuildBookReviewPrompt/character/he\t3095\tCC17F7BF7738C5B6EE861C741AD12ED325D10191946869CFF578F58864DFCEBD",
        "BuildBookReviewPrompt/pacing/he\t3107\t85402BB5AC17AD13CFFB0C4F070CE55CEB215831CE411E59D93A7C920EA77090",
        "BuildBookReviewPrompt/tone/he\t3089\t4CE6F1F68319CA43A54589ECD5015E31391A34DEA794EBD44A4D0E4B58F1A91F",
        "BuildBookReviewPrompt/theme/he\t3104\t308640DDE353A64FA578CFE795B7C10F1C813C55AEB6642F80D89A4D3F2C3DD8",
        "BuildBookReviewPrompt/continuity/he\t3121\tC66E226C239B439432DE0B40594B259E1121CB15576E919C1306AC85792965CD",
        "BuildBookReviewPrompt/unknown-dimension/he\t3093\tBE74FE367055CCFF38420202D880939EA3A105B39B3D1E315F3B5AFE22FECDCB",
        "BuildBookReviewCombinedPrompt/he\t3264\t0A7D42BD51C457E2C8AC0F875C0C54F39D7E8E2C209E14CE305E83F5FB67C06A",
        "BuildChapterAnchorAllowlistRule/he/empty\t200\t7E5890BBC6BDBE4E63946181A3A8694CA0326FA48B344F03FEB5719DEC715DF5",
        "BuildChapterAnchorAllowlistRule/he/orders\t402\t2E6B69B9F755FFFF3F821FEE1C1FA71719BA015EC2EE79795FC73A266C4ED4FA",
        "BuildBookReviewWindowPrompt/he/first\t3832\t2AE451A559E444FA5529307C177E86DA1FF00D3207453C134150C50EB8E39A9D",
        "BuildBookReviewWindowPrompt/he/middle\t3834\t93080D8B79396164BA76D7DFC1D1D3C5269AA7B4D412E0D353365FDA0CEFF45A",
        "BuildBookReviewSynthesisPrompt/he\t4517\t64F1FECCDF47EC94C19F2678719967A0FA18ECD31068BA8AA1970A084559CB4F",
        "BuildBookReviewContinuityReducePrompt/he\t2662\t0E3260D272499DB7D374A57E41EB2D9D4A28D9746335BDC39A521FE655A920D5",
        "BuildBookReviewPrompt/plot/en\t3809\tBA5C801A520D1FEA3EDC284930DADDB17C232060ED4CFCE29937909DB42DCBB9",
        "BuildBookReviewPrompt/character/en\t3828\t75C56130EE2B4D55C43859F02620D3A84988C9FC31DE549B5C1070ECA50C7E4A",
        "BuildBookReviewPrompt/pacing/en\t3821\t1296749F02BBBA9687ABD6F71AFBFD54AF1902269EDF1B5EA4DB82833E5D357B",
        "BuildBookReviewPrompt/tone/en\t3804\tCE3982F1099C7814F6D0E171FAA2ED0C3C37C2D0FA6742E856930A227BFA523B",
        "BuildBookReviewPrompt/theme/en\t3842\t51CB706F9E0310B6DE27B81792A688C0B4CBC642F9F43E999678A71AE1D656A0",
        "BuildBookReviewPrompt/continuity/en\t3849\t987412418DF0DDF25768F7EB8D56BF5594579D22AECF72A36108B068E1F30E9B",
        "BuildBookReviewPrompt/unknown-dimension/en\t3809\tBA5C801A520D1FEA3EDC284930DADDB17C232060ED4CFCE29937909DB42DCBB9",
        "BuildBookReviewCombinedPrompt/en\t3895\t29869F163D53CE5AEB2D27C296D6E937B537749A6EE7A74DB423D530EDBA4B84",
        "BuildChapterAnchorAllowlistRule/en/empty\t277\t0B869FC19DD2A65B500F0D1BDD4977E9EB3FD558B7CC9A54EAD49217B7BE26C3",
        "BuildChapterAnchorAllowlistRule/en/orders\t540\tCF95EB79CA63FAC3A4805FBAE2EF67F2355CAE30A74DE3C6EE8A43B152092A16",
        "BuildBookReviewWindowPrompt/en/first\t4564\t757ED25C00023D53AEFB35DE5F8A6F6C359905DC2EF27037DA380262973EBD52",
        "BuildBookReviewWindowPrompt/en/middle\t4566\tCBE43CB8E60A1AFC475C32782EED1F4896263914928D743F16254C82C2119AD4",
        "BuildBookReviewSynthesisPrompt/en\t5439\tFADE365253ACD842FE1E334C17B1C1230EA9571E7C966D0E0751D4E3CBF0634A",
        "BuildBookReviewContinuityReducePrompt/en\t3264\t0A4EA40FDBD79F7A0ED8DE8AF27D8EA9466B03A8EE427660B9535BF54214EB88",
        "FormatCharactersForBookReview\t142\t8E4711663A2D1DD233ABEE65CD63758369819027B6DE703DDDC48B8783AF5048",
    });
}
