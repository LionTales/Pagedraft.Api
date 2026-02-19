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

    /// <summary>Category: "clarity", "flow", "word-choice", "structure", "redundancy", "style"</summary>
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
