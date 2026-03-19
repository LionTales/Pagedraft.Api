using System.Text.RegularExpressions;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Rules for detecting scene breaks within a chapter's text.
/// Used by the auto-split endpoint: POST .../chapters/{id}/split-scenes.
///
/// Design decisions:
/// - Patterns are ordered by specificity (explicit markers first, whitespace last).
/// - Hebrew markers are first-class: many Hebrew authors use specific scene separators.
/// - A minimum content threshold prevents creating empty scenes from consecutive breaks.
/// - The algorithm splits on the FIRST matching pattern type per location.
/// </summary>
public static class SceneAutoSplitRules
{
    /// <summary>Minimum characters a scene must contain to be kept (avoids empty/trivial scenes).</summary>
    public const int MinSceneContentLength = 50;

    /// <summary>Maximum scenes per chapter to prevent runaway splits on noisy content.</summary>
    public const int MaxScenesPerChapter = 50;

    /// <summary>
    /// All recognized scene break patterns, ordered by priority.
    /// Each pattern matches a full line (or group of lines) that acts as a scene separator.
    /// </summary>
    private static readonly SceneBreakPattern[] Patterns =
    [
        // ── Explicit scene markers ──────────────────────────────────
        new("Asterisks", @"^\s*\*\s*\*\s*\*\s*$", true),
        new("HorizontalRule", @"^\s*[-_]{3,}\s*$", true),
        new("HashMarks", @"^\s*#\s*#\s*#\s*$", true),
        new("Tildes", @"^\s*~{3,}\s*$", true),

        // ── Hebrew-specific markers ─────────────────────────────────
        new("HebrewMultiplicationSign", @"^\s*×\s*×\s*×\s*$", true),
        new("HebrewSceneLabel", @"^\s*(סצנה|משכן|תמונה|מחזה|מערכה)\s+[\u0590-\u05FFa-zA-Z0-9\-'""]+\s*$", true),
        new("SymbolDivider", @"^\s*[◆◇●○★☆■□♦♣♠♥♢♤♡]{1,5}\s*$", true),

        // ── Whitespace breaks ───────────────────────────────────────
        new("DoubleBlankLines", @"(?:\r?\n){3,}", false),
    ];

    /// <summary>
    /// Split chapter text into scenes.
    /// Returns a list of (title, content) tuples. Titles are auto-generated.
    /// </summary>
    public static List<(string Title, string Content)> Split(string chapterText, string chapterTitle)
    {
        if (string.IsNullOrWhiteSpace(chapterText))
            return new List<(string, string)>();

        var breakPositions = new SortedSet<int>();

        foreach (var pattern in Patterns)
        {
            var options = pattern.IsMultiline
                ? RegexOptions.Multiline
                : RegexOptions.None;

            foreach (Match match in Regex.Matches(chapterText, pattern.Regex, options))
            {
                breakPositions.Add(match.Index);
            }
        }

        if (breakPositions.Count == 0)
            return new List<(string, string)> { (chapterTitle, chapterText.Trim()) };

        var scenes = new List<(string Title, string Content)>();
        int prevEnd = 0;
        int sceneIndex = 1;

        foreach (var breakPos in breakPositions)
        {
            if (scenes.Count >= MaxScenesPerChapter) break;

            var content = chapterText[prevEnd..breakPos].Trim();
            if (content.Length >= MinSceneContentLength)
            {
                scenes.Add(($"סצנה {sceneIndex}", content));
                sceneIndex++;
            }

            var breakEnd = FindBreakEnd(chapterText, breakPos);
            prevEnd = breakEnd;
        }

        var lastContent = chapterText[prevEnd..].Trim();
        if (lastContent.Length >= MinSceneContentLength && scenes.Count < MaxScenesPerChapter)
        {
            scenes.Add(($"סצנה {sceneIndex}", lastContent));
        }

        if (scenes.Count <= 1)
            return new List<(string, string)> { (chapterTitle, chapterText.Trim()) };

        return scenes;
    }

    /// <summary>Find the end position of a break (skip the separator and trailing whitespace).</summary>
    private static int FindBreakEnd(string text, int breakStart)
    {
        int i = breakStart;
        while (i < text.Length && (text[i] == '\r' || text[i] == '\n' || text[i] == ' ' || text[i] == '\t'
            || text[i] == '*' || text[i] == '-' || text[i] == '_' || text[i] == '#' || text[i] == '~' || text[i] == '×'
            || "◆◇●○★☆■□♦♣♠♥♢♤♡".Contains(text[i])))
        {
            i++;
        }

        while (i < text.Length && (text[i] == '\r' || text[i] == '\n'))
            i++;

        return i;
    }

    private record SceneBreakPattern(string Name, string Regex, bool IsMultiline);

    // ── Reusable break-marker patterns (used by proofread validation) ──

    /// <summary>
    /// Matches a single line that is a scene or chapter break marker
    /// (asterisks, dashes, tildes, hashes, Hebrew multiplication signs, symbol dividers).
    /// </summary>
    public static readonly Regex SceneBreakLineRegex = new(
        @"^\s*(?:" +
            @"\*{3,5}|" +                       // *** to *****
            @"\*\s+\*\s+\*|" +                  // * * *
            @"[-_]{3,}|" +                       // --- or ___
            @"#\s*#\s*#|" +                      // ###
            @"~{3,}|" +                          // ~~~
            @"×\s*×\s*×|" +                      // ×××
            @"[◆◇●○★☆■□♦♣♠♥♢♤♡]{1,5}" +        // symbol dividers
        @")\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches a single line that looks like a chapter heading
    /// (numbered chapters in English/Hebrew, standalone numbers).
    /// </summary>
    public static readonly Regex ChapterHeadingLineRegex = new(
        @"^\s*(?:" +
            @"Chapter\s+\d+|" +                  // Chapter 1
            @"פרק\s+[\u0590-\u05FF0-9]+|" +     // פרק א / פרק 1
            @"\d+\.\s*[A-Za-z\u0590-\u05FF]+" +  // 1. Title
        @")\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strips leading lines that are scene/chapter break markers or blank,
    /// returning the text starting from the first real content line.
    /// </summary>
    public static string StripLeadingBreakMarkers(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var span = text.AsSpan();
        while (span.Length > 0)
        {
            var newlineIdx = span.IndexOf('\n');
            var line = newlineIdx >= 0 ? span[..newlineIdx] : span;
            var trimmed = line.Trim();

            var lineString = trimmed.ToString();

            if (trimmed.IsEmpty ||
                SceneBreakLineRegex.IsMatch(lineString) ||
                ChapterHeadingLineRegex.IsMatch(lineString))
            {
                span = newlineIdx >= 0 ? span[(newlineIdx + 1)..] : ReadOnlySpan<char>.Empty;
                continue;
            }
            break;
        }
        return span.ToString();
    }
}
