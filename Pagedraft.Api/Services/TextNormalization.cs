using System.Text.RegularExpressions;

namespace Pagedraft.Api.Services;

/// <summary>
/// Shared text normalization helpers used by analysis and SFDT conversion services
/// to keep server-side diffing and suggestion offsets consistent with the client.
/// </summary>
public static class TextNormalization
{
    /// <summary>
    /// Strip Unicode bidi control characters and hard line breaks so text used for
    /// analysis and diffing matches the client normalization (LRM, RLM, embeddings,
    /// isolates, \r, \n).
    /// </summary>
    public static string NormalizeTextForAnalysis(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Regex.Replace(text, @"[\u200E\u200F\u202A-\u202E\u2066-\u2069\r\n]", "");
    }

    /// <summary>
    /// Strip only Unicode bidi control characters, preserving line breaks and other
    /// whitespace. Use this for persisted plain-text content such as Chapter.ContentText
    /// where paragraph structure must be retained.
    /// </summary>
    public static string NormalizeTextForStorage(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Regex.Replace(text, @"[\u200E\u200F\u202A-\u202E\u2066-\u2069]", "");
    }
}

