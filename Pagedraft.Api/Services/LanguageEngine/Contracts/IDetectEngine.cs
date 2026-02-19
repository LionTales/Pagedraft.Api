using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine;

/// <summary>Issue detection stage: identifies grammar, spelling, punctuation, style issues.</summary>
public interface IDetectEngine
{
    Task<DetectResult> DetectAsync(string normalizedText, string language, CancellationToken cancellationToken = default);
}
