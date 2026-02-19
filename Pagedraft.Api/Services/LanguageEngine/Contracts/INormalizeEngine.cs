namespace Pagedraft.Api.Services.LanguageEngine;

/// <summary>Normalization stage: RTL-safe whitespace, quotes, punctuation, diacritics handling.</summary>
public interface INormalizeEngine
{
    Task<string> NormalizeAsync(string inputText, string language, CancellationToken cancellationToken = default);
}
