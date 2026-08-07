namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE parser for a guide file's YAML-ish frontmatter block (chatbot phase A, c1).
///
/// <para>The shipped shape is exactly: <c>---</c> on the first line, then <c>key: value</c> lines
/// (<c>id</c>, <c>stage</c>, <c>audience</c>, <c>updated</c>, <c>lang</c>), then <c>---</c>, then the
/// body. No quoting, no nesting, no lists. This deliberately does NOT pull in a YAML library: the
/// format is fixed by the guides plan and enforced by the guides themselves, and a full YAML parser
/// would accept shapes the corpus is not allowed to contain.</para>
///
/// <para>Static and side-effect free so the parse can be pinned without a filesystem.</para>
/// </summary>
public static class GuideFrontmatter
{
    private const string Fence = "---";

    /// <summary>Language assumed when a file carries no <c>lang</c> and no <c>.he.md</c> suffix.</summary>
    public const string DefaultLang = "en";

    /// <summary>
    /// Parses one guide file. Returns <c>(null, reason)</c> when the file cannot be used, so the caller
    /// can log WHICH file failed and WHY rather than silently ending up with a shorter corpus.
    ///
    /// <para>A file is usable when it has a frontmatter block and a non-empty <c>id</c>. Everything
    /// else degrades: <c>stage</c>/<c>audience</c>/<c>updated</c> default to empty, and <c>lang</c>
    /// falls back to the filename suffix. <c>id</c> is the one hard requirement because it is the
    /// CITATION the contract promises (d1 item 2) - a document that cannot be cited must not be able
    /// to reach an answer.</para>
    /// </summary>
    public static (GuideDocument? Document, string? Reason) Parse(string fileName, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, "the file is empty");

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // Tolerate a UTF-8 BOM ahead of the opening fence (File.ReadAllText usually strips it, but a
        // caller passing raw text should not fall off a cliff over an invisible character).
        var first = lines.Length > 0 ? lines[0].TrimStart('﻿').Trim() : string.Empty;
        if (!string.Equals(first, Fence, StringComparison.Ordinal))
            return (null, "no opening --- frontmatter fence");

        var close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), Fence, StringComparison.Ordinal)) { close = i; break; }
        }

        if (close < 0)
            return (null, "no closing --- frontmatter fence");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < close; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue; // not a key: value line; ignored rather than fatal

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0) fields[key] = value;
        }

        var id = Field(fields, "id");
        if (id.Length == 0)
            return (null, "frontmatter has no id");

        var lang = Field(fields, "lang");
        if (lang.Length == 0)
        {
            // The filename suffix is the documented CROSS-CHECK, so it is also the only sane fallback
            // when the authoritative field is absent.
            lang = LanguageFromFileName(fileName);
        }

        var body = string.Join("\n", lines.Skip(close + 1)).Trim();
        var headings = ExtractHeadings(lines.Skip(close + 1));

        return (new GuideDocument(
            Id: id,
            Stage: Field(fields, "stage"),
            Audience: Field(fields, "audience"),
            Updated: Field(fields, "updated"),
            Lang: lang.ToLowerInvariant(),
            FileName: fileName,
            NumericPrefix: NumericPrefixOf(fileName),
            Headings: headings,
            Body: body), null);
    }

    /// <summary>
    /// The language the FILENAME implies: <c>.he.md</c> means Hebrew, anything else means English.
    /// This is the cross-check, never the authority - see <see cref="GuideDocument"/>.
    /// </summary>
    public static string LanguageFromFileName(string fileName)
        => fileName.EndsWith(".he.md", StringComparison.OrdinalIgnoreCase) ? "he" : DefaultLang;

    /// <summary>
    /// The leading number in a guide filename (<c>00-workflow-overview.md</c> -&gt; 0), or
    /// <see cref="int.MaxValue"/> when there is none (<c>README.md</c>), so unnumbered files sort last
    /// in the selector's tie-break rather than first.
    /// </summary>
    public static int NumericPrefixOf(string fileName)
    {
        var digits = 0;
        while (digits < fileName.Length && fileName[digits] >= '0' && fileName[digits] <= '9') digits++;
        if (digits == 0) return int.MaxValue;
        return int.TryParse(fileName[..digits], out var value) ? value : int.MaxValue;
    }

    /// <summary>H1 and H2 heading TEXT, in document order. d1 scores against these (weighted above
    /// the frontmatter), so what counts as a heading is part of the retrieval contract.</summary>
    private static List<string> ExtractHeadings(IEnumerable<string> bodyLines)
    {
        var headings = new List<string>();
        foreach (var line in bodyLines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                headings.Add(trimmed[3..].Trim());
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                headings.Add(trimmed[2..].Trim());
        }

        return headings;
    }

    private static string Field(IReadOnlyDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : string.Empty;
}
