using System.Text.Json.Serialization;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>Structured line-editing feedback: per-sentence suggestions + overall summary.</summary>
public class LineEditResult
{
    [JsonPropertyName("suggestions")]
    public List<LineEditSuggestion> Suggestions { get; set; } = new();

    [JsonPropertyName("overallFeedback")]
    public string OverallFeedback { get; set; } = string.Empty;
}

public class LineEditSuggestion
{
    [JsonPropertyName("original")]
    public string Original { get; set; } = string.Empty;

    [JsonPropertyName("suggested")]
    public string Suggested { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Category: "clarity", "flow", "word-choice", "structure", "redundancy", "style", "consistency" (conflicts with established style/voice/register), "continuity" (breaks narrative flow with surrounding context, referencing something not yet introduced, contradicting adjacent scenes, etc.)</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "style";
}

/// <summary>Structured linguistic analysis with typed metrics.</summary>
public class LinguisticAnalysisResult
{
    [JsonPropertyName("syntaxMetrics")]
    public SyntaxMetrics SyntaxMetrics { get; set; } = new();

    [JsonPropertyName("morphologyMetrics")]
    public MorphologyMetrics MorphologyMetrics { get; set; } = new();

    [JsonPropertyName("styleMetrics")]
    public StyleMetrics StyleMetrics { get; set; } = new();

    [JsonPropertyName("grammaticalityScore")]
    public double GrammaticalityScore { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("deviations")]
    public List<StyleDeviation> Deviations { get; set; } = new();

    [JsonPropertyName("consistencyIssues")]
    public List<ConsistencyIssue> ConsistencyIssues { get; set; } = new();
}

public class StyleDeviation
{
    [JsonPropertyName("metric")]
    public string Metric { get; set; } = string.Empty;

    [JsonPropertyName("sceneValue")]
    public double SceneValue { get; set; }

    [JsonPropertyName("chapterBaseline")]
    public double ChapterBaseline { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}

public class ConsistencyIssue
{
    /// <summary>Type: "register" | "tense" | "pov"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("span")]
    public string Span { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

public class SyntaxMetrics
{
    [JsonPropertyName("sentenceCount")]
    public int SentenceCount { get; set; }

    [JsonPropertyName("averageSentenceLength")]
    public double AverageSentenceLength { get; set; }

    [JsonPropertyName("complexSentences")]
    public int ComplexSentences { get; set; }

    [JsonPropertyName("shortestSentence")]
    public int ShortestSentence { get; set; }

    [JsonPropertyName("longestSentence")]
    public int LongestSentence { get; set; }
}

public class MorphologyMetrics
{
    [JsonPropertyName("wordCount")]
    public int WordCount { get; set; }

    [JsonPropertyName("uniqueWords")]
    public int UniqueWords { get; set; }

    [JsonPropertyName("averageWordLength")]
    public double AverageWordLength { get; set; }

    [JsonPropertyName("lexicalDensity")]
    public double LexicalDensity { get; set; }
}

public class StyleMetrics
{
    /// <summary>"formal", "informal", "mixed", "literary", "conversational"</summary>
    [JsonPropertyName("formality")]
    public string Formality { get; set; } = "mixed";

    [JsonPropertyName("readability")]
    public double Readability { get; set; }

    /// <summary>"active", "passive", "mixed"</summary>
    [JsonPropertyName("voiceBalance")]
    public string VoiceBalance { get; set; } = "mixed";
}

/// <summary>Structured literary analysis: themes, tone, narrative voice, devices.</summary>
public class LiteraryAnalysisResult
{
    [JsonPropertyName("themes")]
    public List<ThemeEntry> Themes { get; set; } = new();

    [JsonPropertyName("tone")]
    public string Tone { get; set; } = string.Empty;

    [JsonPropertyName("toneDescription")]
    public string ToneDescription { get; set; } = string.Empty;

    [JsonPropertyName("narrativeVoice")]
    public string NarrativeVoice { get; set; } = string.Empty;

    [JsonPropertyName("narrativeVoiceDescription")]
    public string NarrativeVoiceDescription { get; set; } = string.Empty;

    [JsonPropertyName("rhetoricalDevices")]
    public List<RhetoricalDevice> RhetoricalDevices { get; set; } = new();

    [JsonPropertyName("moodProgression")]
    public string MoodProgression { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public class ThemeEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>"major" or "minor"</summary>
    [JsonPropertyName("significance")]
    public string Significance { get; set; } = "major";
}

public class RhetoricalDevice
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("example")]
    public string Example { get; set; } = string.Empty;

    [JsonPropertyName("effect")]
    public string Effect { get; set; } = string.Empty;
}

/// <summary>Book-level overview: genre, audience, register, reading time.</summary>
public class BookOverviewResult
{
    [JsonPropertyName("genre")]
    public string Genre { get; set; } = string.Empty;

    [JsonPropertyName("subGenre")]
    public string SubGenre { get; set; } = string.Empty;

    [JsonPropertyName("targetAudience")]
    public string TargetAudience { get; set; } = string.Empty;

    [JsonPropertyName("literatureLevel")]
    public int LiteratureLevel { get; set; }

    [JsonPropertyName("estimatedReadingTimeMinutes")]
    public int EstimatedReadingTimeMinutes { get; set; }

    [JsonPropertyName("languageRegister")]
    public string LanguageRegister { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

/// <summary>Character analysis: list of characters with roles, relationships, arcs.</summary>
public class CharacterAnalysisResult
{
    [JsonPropertyName("characters")]
    public List<CharacterEntry> Characters { get; set; } = new();

    [JsonPropertyName("relationships")]
    public List<CharacterRelationship> Relationships { get; set; } = new();

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public class CharacterEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>"protagonist", "antagonist", "supporting", "minor"</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("arc")]
    public string Arc { get; set; } = string.Empty;

    [JsonPropertyName("firstAppearanceChapter")]
    public int? FirstAppearanceChapter { get; set; }
}

public class CharacterRelationship
{
    [JsonPropertyName("character1")]
    public string Character1 { get; set; } = string.Empty;

    [JsonPropertyName("character2")]
    public string Character2 { get; set; } = string.Empty;

    [JsonPropertyName("relationship")]
    public string Relationship { get; set; } = string.Empty;
}

/// <summary>Story structure analysis: plot arc, pacing, conflicts.</summary>
public class StoryAnalysisResult
{
    [JsonPropertyName("plotStructure")]
    public PlotStructure PlotStructure { get; set; } = new();

    [JsonPropertyName("pacing")]
    public string Pacing { get; set; } = string.Empty;

    [JsonPropertyName("conflicts")]
    public List<ConflictEntry> Conflicts { get; set; } = new();

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public class PlotStructure
{
    [JsonPropertyName("setup")]
    public string Setup { get; set; } = string.Empty;

    [JsonPropertyName("risingAction")]
    public string RisingAction { get; set; } = string.Empty;

    [JsonPropertyName("climax")]
    public string Climax { get; set; } = string.Empty;

    [JsonPropertyName("fallingAction")]
    public string FallingAction { get; set; } = string.Empty;

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = string.Empty;
}

public class ConflictEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>"resolved", "unresolved", "ongoing"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ongoing";
}

// ---------------------------------------------------------------------------
// Whole-book review -- wb2-f01
// ---------------------------------------------------------------------------

/// <summary>
/// Top-level model output from the whole-book review pass.
/// DimensionScore.Score uses string labels ("weak" | "mixed" | "strong") rather than a
/// numeric scale so the value is self-documenting and does not imply false precision; v1
/// precedent: QAResult.Confidence and StyleMetrics.Formality both use string labels.
/// </summary>
public class BookReviewResult
{
    /// <summary>List of individual editorial findings, each covering one dimension/issue.</summary>
    [JsonPropertyName("findings")]
    public List<BookFindingItem> Findings { get; set; } = new();

    /// <summary>Per-dimension rollup scores summarising all findings in that dimension.</summary>
    [JsonPropertyName("scores")]
    public List<DimensionScore> Scores { get; set; } = new();

    /// <summary>
    /// b8 — THE MERGE MAP. Emitted ONLY by the SYNTHESIS reduce pass, and OPTIONAL there: a list of groups of
    /// accumulated findings (named by the build-local <c>W#</c> ids printed in the [WINDOW_FINDINGS] digest) that
    /// the model judges to be ONE finding, each naming which member to KEEP.
    ///
    /// WHY IT EXISTS. The synthesis prompt has always asked the model to reconcile duplicates, but its only output
    /// channel was <see cref="Findings"/>, which the build APPENDS to the accumulated set. So a "merge" could only
    /// ever ADD a third finding beside the two it meant to replace. This is the DELETE channel that was missing.
    /// It is ADDITIVE ON THE WIRE: a response that omits <c>merges</c> is handled exactly as it was pre-b8. It is
    /// NOT additive on the PROMPT side — the synthesis prompt ASKS for this key whether or not the kill-switch
    /// (<c>Ai:BookReview:SynthesisMergeMap</c>) is on, so an OFF build is not a pre-b8 build (see
    /// <see cref="SynthesisMergeMap"/>, KILL-SWITCH).
    ///
    /// UNTRUSTED, like every other model-supplied reference in this file. Ids are validated against the digest the
    /// model was actually shown and every malformed group is REJECTED whole (see
    /// <see cref="SynthesisMergeMap.Resolve"/>), never partially honoured.
    ///
    /// NULL vs EMPTY: System.Text.Json writes NULL over an initialiser when the JSON says <c>"merges": null</c>, so
    /// every consumer must treat this as nullable regardless of the <c>= new()</c> (the RepairableFields lesson).
    /// </summary>
    [JsonPropertyName("merges")]
    public List<SynthesisMergeItem>? Merges { get; set; }
}

/// <summary>
/// b8 — one proposed merge group from the SYNTHESIS reduce: "these accumulated findings are the SAME finding;
/// keep this one". Raw model output, fully untrusted; <see cref="SynthesisMergeMap.Resolve"/> is the only thing
/// allowed to turn it into an action.
/// </summary>
public class SynthesisMergeItem
{
    /// <summary>The build-local ids (W1..Wn, as printed in the digest) of the findings the model says are one
    /// finding. Fewer than 2 resolvable ids means the group says nothing and is ignored.</summary>
    [JsonPropertyName("ids")]
    public List<string>? Ids { get; set; }

    /// <summary>Which of <see cref="Ids"/> SURVIVES. The survivor is kept VERBATIM (one of the originals): the
    /// model chooses among findings it was shown, it never writes merged prose. A keep that is not one of the
    /// group's own ids rejects the group.</summary>
    [JsonPropertyName("keep")]
    public string? Keep { get; set; }
}

/// <summary>One editorial finding from the book review, as produced by the model.</summary>
public class BookFindingItem
{
    /// <summary>Editorial dimension: plot | character | pacing | tone | theme | continuity</summary>
    [JsonPropertyName("dimension")]
    public string Dimension { get; set; } = string.Empty;

    /// <summary>Overall verdict: keep | improve | cut</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = string.Empty;

    /// <summary>Severity 1 (minor) / 2 (moderate) / 3 (major).</summary>
    [JsonPropertyName("severity")]
    public int Severity { get; set; }

    [JsonPropertyName("rationale")]
    public string Rationale { get; set; } = string.Empty;

    /// <summary>Specific passages or moments that support this finding.</summary>
    [JsonPropertyName("evidence")]
    public List<FindingEvidence> Evidence { get; set; } = new();

    /// <summary>Chapters the finding touches (used for navigation and dedup key derivation).</summary>
    [JsonPropertyName("chapterAnchors")]
    public List<FindingChapterAnchor> ChapterAnchors { get; set; } = new();

    /// <summary>Optional concrete editorial action the model suggests.</summary>
    [JsonPropertyName("suggestedAction")]
    public string? SuggestedAction { get; set; }

    /// <summary>
    /// b7 — THE SHOWN-SET. The chapter Orders the pass that produced this finding actually SHOWED the model,
    /// stamped by the producing pass (never by the model, never serialized: it is provenance, not output).
    ///
    /// WHY. The whole-book review is a MAP-REDUCE: no single pass sees the whole book. A window sees only its
    /// own chapters, the synthesis reduce sees only a findings digest, a continuity group sees only its slice of
    /// the skeleton. A model asked to anchor a finding "by chapter order" will nonetheless emit an order for a
    /// chapter it was never shown — and in a multi-chapter book that invented order LANDS ON A REAL CHAPTER, so
    /// <see cref="ChapterAnchorResolver"/> resolves it happily and the finding is silently MIS-ANCHORED: the user
    /// clicks it and lands in the wrong chapter. Observed live on a 17-chapter book: a finding whose prose is
    /// explicitly about chapter 16 ("Ktiv") came back anchored to chapters 2 and 5, both real, both in a window
    /// the emitting pass never saw. Being a REAL order, nothing in b1's resolver could object.
    ///
    /// An anchor to a chapter the pass never saw is not evidence, it is a guess — so the resolver treats it as
    /// UNRESOLVABLE and drops it (the finding survives as a book-wide, no-anchor finding, which is exactly the
    /// shape b4b's cross-bucket fold then merges into its anchored twin).
    ///
    /// NULL vs EMPTY — two DIFFERENT states, deliberately (the b3 lesson: never let one value mean two things):
    ///   • NULL  = UNCONSTRAINED. The producer did not declare a shown-set, so no visibility gate applies. This
    ///             is the default, so a caller that constructs a BookFindingItem by hand (tests, future callers)
    ///             keeps the pre-b7 behaviour.
    ///   • EMPTY = the pass saw NO chapter orders at all (e.g. a synthesis digest in which every accumulated
    ///             finding was book-wide). Then EVERY anchor is a guess and every anchor is dropped. This is
    ///             correct, not a degenerate case: the model had no chapter number in front of it to copy.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<int>? VisibleChapterOrders { get; set; }
}

/// <summary>A single piece of textual evidence supporting a <see cref="BookFindingItem"/>.</summary>
public class FindingEvidence
{
    /// <summary>Chapter id if the evidence can be pinned to a chapter; null for book-wide evidence.</summary>
    [JsonPropertyName("chapterId")]
    public Guid? ChapterId { get; set; }

    [JsonPropertyName("chapterOrder")]
    public int ChapterOrder { get; set; }

    /// <summary>Short excerpt or paraphrase from the chapter text.</summary>
    [JsonPropertyName("excerpt")]
    public string Excerpt { get; set; } = string.Empty;
}

/// <summary>Chapter reference used to anchor a finding for navigation.</summary>
public class FindingChapterAnchor
{
    [JsonPropertyName("chapterId")]
    public Guid ChapterId { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// Per-dimension rollup of all findings in a <see cref="BookReviewResult"/>.
/// Score is a string label ("weak" | "mixed" | "strong") -- matches the label-over-int
/// convention used by QAResult.Confidence and StyleMetrics.Formality in this file.
/// </summary>
public class DimensionScore
{
    /// <summary>Dimension key: plot | character | pacing | tone | theme | continuity</summary>
    [JsonPropertyName("dimension")]
    public string Dimension { get; set; } = string.Empty;

    /// <summary>Holistic quality label for this dimension: "weak" | "mixed" | "strong"</summary>
    [JsonPropertyName("score")]
    public string Score { get; set; } = "mixed";

    [JsonPropertyName("keepCount")]
    public int KeepCount { get; set; }

    [JsonPropertyName("improveCount")]
    public int ImproveCount { get; set; }

    [JsonPropertyName("cutCount")]
    public int CutCount { get; set; }
}

/// <summary>Q&A answer with chapter citations.</summary>
public class QAResult
{
    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;

    [JsonPropertyName("citations")]
    public List<ChapterCitation> Citations { get; set; } = new();

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "medium";
}

public class ChapterCitation
{
    [JsonPropertyName("chapterNumber")]
    public int ChapterNumber { get; set; }

    [JsonPropertyName("chapterTitle")]
    public string ChapterTitle { get; set; } = string.Empty;

    [JsonPropertyName("relevantExcerpt")]
    public string RelevantExcerpt { get; set; } = string.Empty;
}
