using System.Text;
using System.Text.RegularExpressions;

namespace Pagedraft.Api.Services.LanguageEngine.Normalize;

/// <summary>Hebrew-specific normalization: RTL-safe whitespace, quotes, punctuation. For non-Hebrew (e.g. en-US), only whitespace and zero-width removal so word content is never altered.</summary>
public class HebrewNormalizeEngine : INormalizeEngine
{
    public Task<string> NormalizeAsync(string inputText, string language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputText))
            return Task.FromResult(inputText);

        var normalized = inputText;
        var isHebrew = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);

        // Always: whitespace and zero-width cleanup (safe for all languages)
        normalized = NormalizeWhitespace(normalized);
        normalized = RemoveZeroWidthCharacters(normalized);

        // Hebrew-only: quote and punctuation rules (can alter text; must not run on English)
        if (isHebrew)
        {
            normalized = NormalizeQuotes(normalized);
            normalized = NormalizePunctuationSpacing(normalized);
        }

        return Task.FromResult(normalized);
    }

    private static string NormalizeWhitespace(string text)
    {
        // Normalize line breaks
        text = Regex.Replace(text, @"\r\n|\r", "\n");
        
        // Normalize multiple spaces to single space (but preserve intentional spacing)
        text = Regex.Replace(text, @"[ \t]+", " ");
        
        // Remove trailing whitespace from lines
        text = Regex.Replace(text, @"[ \t]+(\n|$)", "$1");
        
        // Normalize multiple newlines to max 2 consecutive
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        
        return text.Trim();
    }

    private static string NormalizeQuotes(string text)
    {
        // Hebrew gershayim (״) - sometimes appears as double quote
        // Normalize various quote marks to standard Hebrew quotes
        text = text.Replace("\u201C", "\u05F4"); // Left double quotation mark (") to Hebrew gershayim (״)
        text = text.Replace("\u201D", "\u05F4"); // Right double quotation mark (") to Hebrew gershayim (״)
        text = text.Replace("\"", "\u05F4"); // Regular double quote to Hebrew gershayim (״)
        
        // Single quotes - normalize to Hebrew geresh (׳)
        text = text.Replace("\u2018", "\u05F3"); // Left single quotation mark (') to Hebrew geresh (׳)
        text = text.Replace("\u2019", "\u05F3"); // Right single quotation mark (') to Hebrew geresh (׳)
        text = text.Replace("'", "\u05F3"); // Regular single quote to Hebrew geresh (׳)
        
        return text;
    }

    private static string NormalizePunctuationSpacing(string text)
    {
        // Hebrew punctuation spacing rules:
        // - No space before Hebrew punctuation marks
        // - Space after punctuation (except gershayim which attaches to word)
        
        // Remove spaces before Hebrew punctuation
        text = Regex.Replace(text, @"\s+([.,;:!?])", "$1");
        
        // Ensure space after punctuation (unless followed by quote or another punctuation)
        text = Regex.Replace(text, @"([.,;:!?])([^\s""'״])", "$1 $2");
        
        // Gershayim attaches to the word (no space before or after)
        // This is already handled by quote normalization
        
        return text;
    }

    private static string RemoveZeroWidthCharacters(string text)
    {
        // Remove zero-width space, zero-width non-joiner, etc.
        text = text.Replace("\u200B", ""); // Zero-width space
        text = text.Replace("\u200C", ""); // Zero-width non-joiner
        text = text.Replace("\u200D", ""); // Zero-width joiner
        text = text.Replace("\uFEFF", ""); // Zero-width no-break space
        
        return text;
    }
}
