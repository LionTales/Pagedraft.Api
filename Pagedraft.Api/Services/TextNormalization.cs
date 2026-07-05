using System.Text.RegularExpressions;

namespace Pagedraft.Api.Services;

/// <summary>
/// Shared text normalization helpers used by analysis and SFDT conversion services
/// to keep server-side diffing and suggestion offsets consistent with the client.
/// </summary>
public static class TextNormalization
{
    private static bool IsBidiControl(char ch)
    {
        var code = (int)ch;
        // LRM, RLM, embeddings/overrides, isolates
        return code == 0x200E
               || code == 0x200F
               || (code >= 0x202A && code <= 0x202E)
               || (code >= 0x2066 && code <= 0x2069);
    }

    /// <summary>
    /// Strip Unicode bidi control characters and convert hard line breaks to spaces so text
    /// used for analysis and diffing matches the client normalization.
    ///
    /// Bidi controls (LRM, RLM, embeddings, isolates) are truly invisible and are DROPPED.
    /// Hard line breaks (\r, \n) are a WORD BOUNDARY, so each is replaced 1:1 with a single
    /// space rather than dropped: dropping them glued the chapter title into the first body
    /// word ("רוני\nהתעוררתי" -> "רוניהתעוררתי"), which the model then "fixed" by deleting a
    /// word. The 1:1 replacement (each \r -> space, each \n -> space; a CRLF becomes two
    /// spaces) preserves character-length parity so the offset mapping (normalizedOffsetToRawOffset)
    /// stays a simple 1:1 walk, and keeps the client's per-character normalization identical.
    /// </summary>
    public static string NormalizeTextForAnalysis(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Avoid regex quirks with invisible characters by walking the string explicitly.
        var buffer = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (IsBidiControl(ch)) continue;
            if (ch == '\r' || ch == '\n') { buffer.Append(' '); continue; }
            buffer.Append(ch);
        }
        return buffer.ToString();
    }

    /// <summary>
    /// Strip only Unicode bidi control characters, preserving line breaks and other
    /// whitespace. Use this for persisted plain-text content such as Chapter.ContentText
    /// where paragraph structure must be retained.
    /// </summary>
    public static string NormalizeTextForStorage(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var buffer = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (IsBidiControl(ch)) continue;
            buffer.Append(ch);
        }
        return buffer.ToString();
    }
}

