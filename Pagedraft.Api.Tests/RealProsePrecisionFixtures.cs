using System;
using System.Collections.Generic;
using System.Linq;

// Bound through a using ALIAS, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes. Same rule (and same reason) as ChunkedAgreementFixtures.
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// RealProsePrecisionFixtures — THE PRECISION SURFACE FOR A PER-CHUNK PROMPT ARM.
//
// WHY IT HAD TO BE BUILT. The standing proofread gold corpus CANNOT reach a per-chunk prompt
// intervention at all: ProofreadQualityTests.BuildGoldRequest composes through the THREE-argument
// PromptFactory.BuildProofreadChunkPrompt(language, characters, overlapPrefix: null), so no
// [CONTEXT_BEFORE] section is ever rendered and no arm that extends it can be observed. That is
// recorded as a structural fact, not an oversight, at
// ProofreadStandingFloor.GoldSurfaceCannotReachAPerChunkIntervention, and it is why the previous
// plan's decision-rule condition 4 was VACUOUSLY satisfied rather than measured. Acting on the
// over-correction lead therefore required a surface where (a) the per-chunk builder actually runs and
// (b) precision is measurable. This is that surface.
//
// THE METRIC, AND WHY THE PROSE IS REAL. Every passage is a verbatim excerpt of a Hebrew manuscript
// that was PROOFREAD TWICE by a human before it was handed over (RealProsePassages). On text like
// that, essentially every edit a proofreading model proposes is by construction an over-correction,
// so precision is a COUNT of edits rather than a classification of them. That matters twice over:
//   1. it does not depend on the `phenomenon != agreement-repair` proxy the authored chunked corpus
//      uses, which is only a precision measure if the authored prose is otherwise clean; and
//   2. it does not depend on the shipped phenomenon CLASSIFIER, which was audited on 2026-08-05 and
//      found degenerate on this task - it emits only "agreement-repair" and "other".
// The honest caveat, stated here rather than discovered later: "already proofread twice" is not
// "provably error-free". A model edit on a clean passage is PRESUMED spurious, and the presumption is
// what makes the count a precision proxy. It is a far tighter presumption than the authored corpus's,
// and it is the reason the arm is compared to itself ACROSS ARMS on the same passages rather than
// scored against an absolute bar.
//
// WHY A RECALL GUARD IS NOT OPTIONAL. An arm that reduces the number of edits can do so by being
// right or by being lazy, and a precision-only measurement cannot tell those apart - it scores "the
// model changed nothing" as a perfect run. Four of the twelve passages therefore carry a SEEDED
// variant: real errors transplanted from the shipped proofread gold corpus (the inj-ms-* class,
// unambiguous word-level defects), so ONE session measures precision on the clean passages and recall
// on the seeded ones with the prose held constant. A seeded passage is a VARIANT of a passage that is
// also measured clean, so the recall reading and the precision reading share their prose exactly.
//
// THE FAILURE MODE THIS SURFACE IS MOST EXPOSED TO, and it is specific to a precision metric: a
// per-chunk model call that THROWS is swallowed by RunProofreadChunkedAsync, which merges the
// ORIGINAL chunk text and carries on. That is byte-identical to "the model proposed no edits", i.e. a
// PERFECT precision score. RealProseRun surfaces Failures for exactly this reason and the consumer
// must void the run on a non-zero count rather than fold it in.
//
// DESIGN OF THE PASSAGE SET (see also the RealProsePassages header for the selection procedure):
//   - 12 passages, EVERY ONE of them exactly 2 chunks at the Hebrew target of 250 words, so every run
//     exercises a genuine [CONTEXT_BEFORE] overlap (chunk 1 always carries one) at the smallest
//     multi-chunk size available. Small is deliberate: a per-chunk model call is the unit of GPU cost,
//     and the whole point of this plan is a bounded session.
//   - a DIALOGUE-DENSITY GRADIENT from 0 to 68 ASCII double quotes per passage, because the single
//     largest population of candidate edits on this manuscript is its ASCII quote (the book's house
//     style; the corpus's own gold case norm-3 treats ASCII -> gershayim as a correction). Two
//     passages carry no quote character at all and are the control for a quote-driven result.
//   - the construction that dominated the SYNTHETIC measurement (מן ה..., four instances of which
//     carried 62% of the over-corrections there) appears at most ONCE per passage here, so it cannot
//     carry this one. That is asserted, not assumed - see the composition test.
// ---------------------------------------------------------------------------------------------

/// <summary>Which text of a passage a run drives: the untouched excerpt, or the error-seeded variant.</summary>
public enum RealProseVariant
{
    /// <summary>The manuscript excerpt verbatim. Every model edit on it is presumed spurious.</summary>
    Clean,

    /// <summary>
    /// The same excerpt with this passage's <see cref="RealProsePassage.Seeds"/> transplanted in. Used
    /// for RECALL only: its over-correction count is confounded by the seeds and must not be reported
    /// on the precision axis.
    /// </summary>
    Seeded
}

/// <summary>
/// One known error transplanted from the shipped proofread gold corpus into a real passage.
///
/// TRANSPLANTED AS A SPAN, NOT AS A SENTENCE. The gold case's own carrier sentence is authored prose;
/// injecting it would put a synthetic sentence back into the surface whose whole point is that it is
/// real. What is transplanted is the DEFECT SHAPE applied to a word the manuscript already contains,
/// so the error is the gold corpus's and the prose around it is still the author's.
/// </summary>
/// <param name="GoldCaseId">The <c>proofread-gold.json</c> case this defect shape comes from.</param>
/// <param name="Shape">The defect, in words. Diagnostics and review, never matched on.</param>
/// <param name="Category">The gold case's own category ("spelling" / "grammar" / "punctuation").</param>
/// <param name="CleanSpan">
/// The span as the manuscript writes it. Occurs EXACTLY ONCE in the clean passage (asserted), so the
/// transplant is unambiguous.
/// </param>
/// <param name="SeededSpan">
/// What replaces it. Occurs ZERO times in the clean passage and exactly once in the seeded one
/// (asserted), so a scorer can never confuse a repair with prose that was always there.
/// </param>
/// <param name="ExpectedChunkIndex">
/// Which chunk of the SEEDED text carries <paramref name="SeededSpan"/>. Pinned because the arm under
/// test only renders a [CONTEXT_BEFORE] overlap from chunk 1 onward: a recall guard whose defects all
/// sat in chunk 0 would measure the first-chunk regime while claiming to measure the chunked one.
/// </param>
public sealed record RealProseSeed(
    string GoldCaseId,
    string Shape,
    string Category,
    string CleanSpan,
    string SeededSpan,
    int ExpectedChunkIndex)
{
    /// <summary>
    /// Whether a corrected text has repaired this defect: the seeded span is GONE and the manuscript's
    /// own span is present. Both halves are needed - "seeded span gone" alone would score a model that
    /// deleted the sentence as a repair, and "clean span present" alone is already true before the
    /// model runs for the doubling shape (whose seeded span CONTAINS its clean span).
    /// </summary>
    public bool RepairedIn(string? correctedText) =>
        correctedText is not null &&
        !correctedText.Contains(SeededSpan, StringComparison.Ordinal) &&
        correctedText.Contains(CleanSpan, StringComparison.Ordinal);
}

/// <summary>
/// One real-prose passage: the excerpt, its provenance, the chunk shape it realizes, and any seeded
/// defects. See the file header.
/// </summary>
/// <param name="Id">Stable id, <c>real-prose-NN</c>.</param>
/// <param name="SourceParagraphStart">First source paragraph index in the manuscript.</param>
/// <param name="SourceParagraphEnd">Last source paragraph index in the manuscript.</param>
/// <param name="Paragraphs">The excerpt, one entry per source paragraph, verbatim.</param>
/// <param name="Surface">The prompt surface this passage is measured on.</param>
/// <param name="ExpectedChunkCount">Realized chunk count, asserted model-free against the real chunker.</param>
/// <param name="Seeds">Transplanted gold defects, or empty for a precision-only passage.</param>
/// <param name="Note">What this passage contributes to the set that the others do not.</param>
public sealed record RealProsePassage(
    string Id,
    int SourceParagraphStart,
    int SourceParagraphEnd,
    IReadOnlyList<string> Paragraphs,
    GoldPromptSurface Surface,
    int ExpectedChunkCount,
    IReadOnlyList<RealProseSeed> Seeds,
    string Note)
{
    /// <summary>The excerpt verbatim, paragraphs joined by <see cref="RealProsePrecisionFixtures.ParagraphSeparator"/>.</summary>
    public string CleanText => string.Join(RealProsePrecisionFixtures.ParagraphSeparator, Paragraphs);

    /// <summary>
    /// The excerpt with every seed transplanted, applied to the FIRST occurrence of each
    /// <see cref="RealProseSeed.CleanSpan"/>. Each clean span occurs exactly once (asserted), so
    /// "first" and "only" coincide and the composition is order-independent.
    /// </summary>
    public string SeededText =>
        Seeds.Aggregate(CleanText, (text, seed) => ReplaceFirst(text, seed.CleanSpan, seed.SeededSpan));

    /// <summary>The text for a variant. <see cref="RealProseVariant.Seeded"/> on an unseeded passage throws.</summary>
    public string TextFor(RealProseVariant variant) => variant switch
    {
        RealProseVariant.Clean => CleanText,
        RealProseVariant.Seeded when Seeds.Count > 0 => SeededText,
        RealProseVariant.Seeded => throw new InvalidOperationException(
            $"{Id} carries no seeds, so it has no Seeded variant. Driving it as Seeded would silently " +
            "measure the CLEAN text under a recall label."),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "unknown variant")
    };

    /// <summary>True when this passage carries transplanted defects and therefore a recall reading.</summary>
    public bool IsSeeded => Seeds.Count > 0;

    /// <summary>Every variant this passage supports, in run order.</summary>
    public IReadOnlyList<RealProseVariant> Variants =>
        IsSeeded
            ? new[] { RealProseVariant.Clean, RealProseVariant.Seeded }
            : new[] { RealProseVariant.Clean };

    private static string ReplaceFirst(string text, string from, string to)
    {
        var at = text.IndexOf(from, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException(
                $"the seed span [{from}] is not present in the passage text, so the transplant would be " +
                "a silent no-op and the recall denominator would count a defect that was never injected");
        return text.Remove(at, from.Length).Insert(at, to);
    }
}

/// <summary>
/// COMPOSITION OF THE CANDIDATE-EDIT SURFACE of one passage, computed from the text rather than
/// declared on it.
///
/// WHY IT IS A FIRST-CLASS TYPE. The synthetic measurement this surface replaces was carried by ONE
/// construction: four instances of <c>מן ה...</c> produced 62% of the over-corrections and 93% of the
/// gross drop. A passage set that is structurally unable to show an effect, or one where a single
/// construction dominates, produces a number that reads exactly like a result. So the composition is
/// computed, reported, and ASSERTED for spread - it is part of the instrument, not a note about it.
/// </summary>
public sealed record RealProseComposition(
    string PassageId,
    int Words,
    int Paragraphs,
    int AsciiDoubleQuotes,
    int AsciiApostrophes,
    int EnDashes,
    int Maqafs,
    int EllipsisChars,
    int ThreeDotRuns,
    int MinHaDefinite,
    int Commas,
    int Periods,
    int QuestionMarks,
    int ExclamationMarks)
{
    /// <summary>Total quote characters of any kind - the largest single edit-candidate family here.</summary>
    public int Quotes => AsciiDoubleQuotes + AsciiApostrophes;

    /// <summary>Punctuation marks that are not quotes, per hundred words. A density, so passages compare.</summary>
    public double PunctuationPer100Words =>
        Words == 0 ? 0 : 100.0 * (Commas + Periods + QuestionMarks + ExclamationMarks) / Words;
}

/// <summary>The twelve passages, their seeds, and the helpers that describe them. See the file header.</summary>
public static class RealProsePrecisionFixtures
{
    /// <summary>Analysis language of every passage. Locale variant of "he"; the chunk sizer collapses it.</summary>
    public const string Language = "he-IL";

    /// <summary>Paragraph separator. A blank line is what <c>BuildChunkSegmentsCore</c> segments on.</summary>
    public const string ParagraphSeparator = "\n\n";

    /// <summary>
    /// The manuscript every passage is excerpted from, and its clearance. Recorded as data so a reader
    /// of a published result can tell what the numbers were measured on without the plan file.
    /// </summary>
    public const string SourceManuscript =
        "זיכרונות של מכשף - לאחר הגהה שנייה.docx (~107,600 words, 5,640 paragraphs), a Hebrew novel " +
        "cleared for use as proofread test data and PROOFREAD TWICE by a human before it was handed " +
        "over. The file lives in the workspace docs directory, which is outside every git repository " +
        "here, which is why the excerpts are EMBEDDED in RealProsePassages rather than read at runtime.";

    /// <summary>Passage id constants, so tests and g1's report select without string literals.</summary>
    public const string NarrationNoQuotesId = "real-prose-01-narration-no-quotes";
    public const string DialogueMidId = "real-prose-02-dialogue-mid";
    public const string DialogueLowId = "real-prose-03-dialogue-low";
    public const string DialogueHighId = "real-prose-04-dialogue-high";
    public const string ArgumentMidId = "real-prose-05-argument-mid";
    public const string NarrationNoQuotesTwoId = "real-prose-06-narration-no-quotes-2";
    public const string DialogueMidTwoId = "real-prose-07-dialogue-mid-2";
    public const string DialogueVeryHighId = "real-prose-08-dialogue-very-high";
    public const string InteriorLowId = "real-prose-09-interior-low";
    public const string BanterVeryHighId = "real-prose-10-banter-very-high";
    public const string SceneHighId = "real-prose-11-scene-high";
    public const string ActionMidId = "real-prose-12-action-mid";

    private static readonly IReadOnlyList<RealProseSeed> NoSeeds = Array.Empty<RealProseSeed>();

    /// <summary>
    /// THE CORPUS. Order is the passage order the report uses; it is also roughly the manuscript's own
    /// order, which is incidental. Ids are descriptive of the DIALOGUE BAND each passage occupies
    /// because that is the axis the set is spread along.
    /// </summary>
    public static readonly IReadOnlyList<RealProsePassage> All = new[]
    {
        new RealProsePassage(
            Id: NarrationNoQuotesId,
            SourceParagraphStart: 989, SourceParagraphEnd: 996,
            Paragraphs: RealProsePassages.P01,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2,
            Seeds: new[]
            {
                new RealProseSeed(
                    GoldCaseId: "inj-ms-06",
                    Shape: "a word-final nun written in its MEDIAL form (הקטן -> הקטנ in the gold case)",
                    Category: "spelling",
                    CleanSpan: "השעון", SeededSpan: "השעונ", ExpectedChunkIndex: 0),
                new RealProseSeed(
                    GoldCaseId: "inj-ms-03",
                    Shape: "a dropped yod in a ktiv-male verb form (חיכיתי -> חכיתי in the gold case)",
                    Category: "spelling",
                    CleanSpan: "וחיכיתי", SeededSpan: "וחכיתי", ExpectedChunkIndex: 1),
            },
            Note:
                "NO QUOTE CHARACTER AT ALL - one of the two narration-only controls. If the arm's effect " +
                "shows up only where the manuscript's ASCII quotes are dense, it is a quote-normalization " +
                "story and not a precision one, and this passage is what makes that visible. It is also " +
                "SEEDED, and deliberately so: the recall guard must not sit entirely on dialogue."),

        new RealProsePassage(
            Id: DialogueMidId,
            SourceParagraphStart: 1195, SourceParagraphEnd: 1210,
            Paragraphs: RealProsePassages.P02,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note:
                "Mid dialogue band (24 double quotes) and the only passage carrying more than one ASCII " +
                "apostrophe alongside them, so the two quote families are both represented."),

        new RealProsePassage(
            Id: DialogueLowId,
            SourceParagraphStart: 1302, SourceParagraphEnd: 1313,
            Paragraphs: RealProsePassages.P03,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note:
                "Low dialogue band (8 double quotes): mostly narration with a little speech, the shape " +
                "most of the manuscript is actually in."),

        new RealProsePassage(
            Id: DialogueHighId,
            SourceParagraphStart: 1411, SourceParagraphEnd: 1427,
            Paragraphs: RealProsePassages.P04,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note: "High dialogue band (29 double quotes) with four apostrophes."),

        new RealProsePassage(
            Id: ArgumentMidId,
            SourceParagraphStart: 1584, SourceParagraphEnd: 1597,
            Paragraphs: RealProsePassages.P05,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2,
            Seeds: new[]
            {
                new RealProseSeed(
                    GoldCaseId: "inj-ms-11",
                    Shape: "a word-final mem written in its MEDIAL form (לשלם -> לשלמ in the gold case)",
                    Category: "spelling",
                    CleanSpan: "שלהם", SeededSpan: "שלהמ", ExpectedChunkIndex: 0),
                new RealProseSeed(
                    GoldCaseId: "inj-ms-12",
                    Shape: "a word accidentally DOUBLED (מתוח מתוח -> מתוח in the gold case)",
                    Category: "grammar",
                    CleanSpan: "להירדם בלילה", SeededSpan: "להירדם להירדם בלילה", ExpectedChunkIndex: 1),
            },
            Note:
                "Mid dialogue band, argumentative register (second-person address), and one of the two " +
                "passages carrying an ellipsis CHARACTER as well as a three-dot run."),

        new RealProsePassage(
            Id: NarrationNoQuotesTwoId,
            SourceParagraphStart: 1652, SourceParagraphEnd: 1658,
            Paragraphs: RealProsePassages.P06,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note:
                "The SECOND narration-only control (no quote character). Two of them, not one, because a " +
                "single passage carrying the whole no-quote arm of the comparison would make that arm a " +
                "sample of one."),

        new RealProsePassage(
            Id: DialogueMidTwoId,
            SourceParagraphStart: 1678, SourceParagraphEnd: 1693,
            Paragraphs: RealProsePassages.P07,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note:
                "Mid-high dialogue band with the densest PERIOD count per word in the set - short " +
                "sentences, which is where a punctuation-shaped over-correction has the most surface."),

        new RealProsePassage(
            Id: DialogueVeryHighId,
            SourceParagraphStart: 2634, SourceParagraphEnd: 2657,
            Paragraphs: RealProsePassages.P08,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2,
            Seeds: new[]
            {
                new RealProseSeed(
                    GoldCaseId: "inj-ms-04",
                    Shape: "a word-final kaf written in its MEDIAL form (המשיך -> המשיכ in the gold case)",
                    Category: "spelling",
                    CleanSpan: "תצטרך", SeededSpan: "תצטרכ", ExpectedChunkIndex: 0),
                new RealProseSeed(
                    GoldCaseId: "inj-ms-02",
                    Shape: "two words run together with no space (זה הקקאו -> זההקקאו in the gold case)",
                    Category: "spelling",
                    CleanSpan: "אימוני כושר", SeededSpan: "אימוניכושר", ExpectedChunkIndex: 1),
            },
            Note:
                "Very high dialogue band (48 double quotes) and the top of the exclamation band. The " +
                "seeded counterpart of the no-quote passage: recall is measured at BOTH ends of the " +
                "dialogue gradient, so a recall drop cannot be attributed to register alone."),

        new RealProsePassage(
            Id: InteriorLowId,
            SourceParagraphStart: 3118, SourceParagraphEnd: 3134,
            Paragraphs: RealProsePassages.P09,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2,
            Seeds: new[]
            {
                new RealProseSeed(
                    GoldCaseId: "inj-ms-09",
                    Shape: "a stray space BEFORE a full stop (מסמיקה . -> מסמיקה. in the gold case)",
                    Category: "punctuation",
                    CleanSpan: "להיעלם.", SeededSpan: "להיעלם .", ExpectedChunkIndex: 0),
                new RealProseSeed(
                    GoldCaseId: "inj-ms-10",
                    Shape: "a one-letter prefix split off its word (ל הראות -> להראות in the gold case)",
                    Category: "spelling",
                    CleanSpan: "לחשוב", SeededSpan: "ל חשוב", ExpectedChunkIndex: 1),
            },
            Note:
                "Low dialogue band, interior monologue. Carries the only PUNCTUATION-category seed in the " +
                "set, which is the class most at risk from an arm that narrows the model's scope to named " +
                "grammatical categories - so it is the seed the recall guard most needs."),

        new RealProsePassage(
            Id: BanterVeryHighId,
            SourceParagraphStart: 3627, SourceParagraphEnd: 3648,
            Paragraphs: RealProsePassages.P10,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note:
                "The densest dialogue in the set (68 double quotes, 22 paragraphs of rapid exchange) and " +
                "three three-dot runs. The upper bound of the gradient."),

        new RealProsePassage(
            Id: SceneHighId,
            SourceParagraphStart: 3952, SourceParagraphEnd: 3977,
            Paragraphs: RealProsePassages.P11,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note:
                "High dialogue band with the most QUESTION marks in the set and three en-dashes - the " +
                "manuscript's own dash convention, a standing candidate for punctuation normalization."),

        new RealProsePassage(
            Id: ActionMidId,
            SourceParagraphStart: 4227, SourceParagraphEnd: 4235,
            Paragraphs: RealProsePassages.P12,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2, Seeds: NoSeeds,
            Note:
                "Mid dialogue band, action register, and the most MAQAFS in the set (the Hebrew " +
                "hyphenation mark) - the hyphenation edit-candidate family."),
    };

    /// <summary>The passages carrying transplanted defects: the RECALL half of the surface.</summary>
    public static IReadOnlyList<RealProsePassage> Seeded =>
        All.Where(p => p.IsSeeded).ToArray();

    /// <summary>
    /// The PRECISION axis: every passage's clean variant. All twelve, including the four that also have
    /// a seeded variant - the clean text of a seeded passage is untouched manuscript prose and there is
    /// no reason to drop it, and dropping it would cut the replication axis from twelve to eight.
    /// </summary>
    public static IReadOnlyList<RealProsePassage> PrecisionAxis => All;

    /// <summary>Every (passage, variant) pair one full session drives. This is the run matrix's row set.</summary>
    public static IReadOnlyList<(RealProsePassage Passage, RealProseVariant Variant)> RunUnits =>
        All.SelectMany(p => p.Variants.Select(v => (p, v))).ToArray();

    /// <summary>Look a passage up by id (throws on an unknown id rather than returning null).</summary>
    public static RealProsePassage ById(string id) =>
        All.SingleOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No real-prose passage with this id.");

    // ── composition ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The construction that dominated the SYNTHETIC over-correction measurement. See the header.</summary>
    public const string SyntheticDominantConstruction = "מן ה";

    /// <summary>Composition of one passage's CLEAN text. Computed, never declared. See <see cref="RealProseComposition"/>.</summary>
    public static RealProseComposition Describe(RealProsePassage passage)
    {
        var t = passage.CleanText;
        return new RealProseComposition(
            PassageId: passage.Id,
            Words: WordCount(t),
            Paragraphs: passage.Paragraphs.Count,
            AsciiDoubleQuotes: Count(t, '"'),
            AsciiApostrophes: Count(t, '\''),
            EnDashes: Count(t, '–'),
            Maqafs: Count(t, '־'),
            EllipsisChars: Count(t, '…'),
            ThreeDotRuns: Occurrences(t, "..."),
            MinHaDefinite: Occurrences(t, SyntheticDominantConstruction),
            Commas: Count(t, ','),
            Periods: Count(t, '.'),
            QuestionMarks: Count(t, '?'),
            ExclamationMarks: Count(t, '!'));
    }

    /// <summary>Every passage's composition, in corpus order - the table a report prints.</summary>
    public static IReadOnlyList<RealProseComposition> Compositions =>
        All.Select(Describe).ToArray();

    /// <summary>Word count on the same rule the chunker uses (whitespace runs).</summary>
    public static int WordCount(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : System.Text.RegularExpressions.Regex.Split(text.Trim(), @"\s+").Count(s => s.Length > 0);

    private static int Count(string text, char ch) => text.Count(c => c == ch);

    /// <summary>Overlapping-safe occurrence count of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    public static int Occurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var from = 0;
        while (true)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) return count;
            count++;
            from = at + 1;
        }
    }
}
