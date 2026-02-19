using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine.Detect;

/// <summary>LanguageTool integration for Hebrew issue detection.</summary>
public class LanguageToolEngine : IDetectEngine
{
    private readonly HttpClient _httpClient;
    private readonly LanguageToolOptions _options;
    private readonly ILogger<LanguageToolEngine>? _logger;

    public LanguageToolEngine(
        IHttpClientFactory httpClientFactory,
        IOptions<LanguageToolOptions> options,
        ILogger<LanguageToolEngine>? logger = null)
    {
        _httpClient = httpClientFactory.CreateClient("LanguageTool");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DetectResult> DetectAsync(string normalizedText, string language, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger?.LogWarning("LanguageTool is disabled in configuration");
            return new DetectResult { Issues = [], ServiceUnavailable = true, ServiceUnavailableMessage = "The language checker is turned off in settings." };
        }

        try
        {
            var langCode = MapLanguageCode(language);
            var response = await PostCheckAsync(normalizedText, langCode, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (errorBody.Contains("not a language code known", StringComparison.OrdinalIgnoreCase)
                    || errorBody.Contains("is not a language code", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("LanguageTool does not support language {Lang}. Retrying with auto-detect.", langCode);
                    response = await PostCheckAsync(normalizedText, "auto", cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        var autoResponse = await response.Content.ReadFromJsonAsync<LanguageToolResponse>(cancellationToken: cancellationToken);
                        var autoIssues = autoResponse?.Matches?.Select(match => ConvertToLanguageIssue(match, normalizedText)).ToList() ?? new List<LanguageIssue>();
                        _logger?.LogInformation("LanguageTool (auto) detected {Count} issues", autoIssues.Count);
                        return new DetectResult
                        {
                            Issues = autoIssues,
                            ServiceUnavailable = langCode == "he",
                            ServiceUnavailableMessage = langCode == "he"
                                ? "Checked using auto-detected language (requested language isn't supported by this server)."
                                : null
                        };
                    }
                    return new DetectResult
                    {
                        Issues = [],
                        ServiceUnavailable = true,
                        ServiceUnavailableMessage = "The language checker doesn't support Hebrew. Use a LanguageTool server with Hebrew support (e.g. a community Docker image), or rely on other checks."
                    };
                }
            }

            response.EnsureSuccessStatusCode();

            var ltResponse = await response.Content.ReadFromJsonAsync<LanguageToolResponse>(cancellationToken: cancellationToken);
            if (ltResponse == null)
            {
                _logger?.LogWarning("LanguageTool returned null response");
                return new DetectResult { Issues = [] };
            }

            var issues = ltResponse.Matches?.Select(match => ConvertToLanguageIssue(match, normalizedText)).ToList() ?? new List<LanguageIssue>();
            _logger?.LogInformation("LanguageTool detected {Count} issues", issues.Count);
            return new DetectResult { Issues = issues };
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "LanguageTool server unreachable at {Url}. Returning no issues. Start the server or set LanguageEngine:LanguageTool:Enabled to false.", _options.ServerUrl);
            return new DetectResult
            {
                Issues = [],
                ServiceUnavailable = true,
                ServiceUnavailableMessage = "The language checker (LanguageTool) isn't available right now. Make sure the LanguageTool server is running, or try again later."
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException or null)
        {
            _logger?.LogWarning("LanguageTool request timed out at {Url}. Returning no issues.", _options.ServerUrl);
            return new DetectResult
            {
                Issues = [],
                ServiceUnavailable = true,
                ServiceUnavailableMessage = "The language checker took too long to respond. Try again in a moment."
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in LanguageTool detection");
            throw;
        }
    }

    /// <summary>POST to LanguageTool v2/check with form-encoded text and language.</summary>
    private async Task<HttpResponseMessage> PostCheckAsync(string normalizedText, string langCode, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["text"] = normalizedText,
            ["language"] = langCode
        };
        using var content = new FormUrlEncodedContent(form);
        return await _httpClient.PostAsync("v2/check", content, cancellationToken);
    }

    private static string MapLanguageCode(string language)
    {
        // Map language codes to LanguageTool format
        return language.ToLowerInvariant() switch
        {
            "he-il" or "he" => "he",
            "en-us" or "en" => "en-US",
            _ => language.Split('-')[0] // Fallback to first part
        };
    }

    private static LanguageIssue ConvertToLanguageIssue(LanguageToolMatch match, string text)
    {
        var issue = new LanguageIssue
        {
            StartOffset = match.Offset,
            EndOffset = match.Offset + match.Length,
            Message = match.Message ?? "Issue detected",
            Category = MapCategory(match.Rule?.Category),
            Severity = MapSeverity(match.Rule?.Category),
            Confidence = match.Rule?.Quality?.IssueType == "misspelling" ? 0.9 : 0.7,
            RuleId = match.Rule?.Id,
            Suggestions = match.Replacements?.Select(r => r.Value ?? "").Where(v => !string.IsNullOrEmpty(v)).Take(15).ToList() ?? new List<string>()
        };

        return issue;
    }

    private static string MapCategory(LanguageToolRuleCategory? category)
    {
        if (category == null) return "grammar";
        
        return category.Id switch
        {
            "TYPOS" => "spelling",
            "GRAMMAR" => "grammar",
            "STYLE" => "style",
            "PUNCTUATION" => "punctuation",
            _ => "grammar"
        };
    }

    private static string MapSeverity(LanguageToolRuleCategory? category)
    {
        if (category == null) return "warning";
        
        return category.Id switch
        {
            "TYPOS" => "error",
            "GRAMMAR" => "error",
            "STYLE" => "info",
            "PUNCTUATION" => "warning",
            _ => "warning"
        };
    }
}

/// <summary>LanguageTool API response structure.</summary>
internal class LanguageToolResponse
{
    public List<LanguageToolMatch>? Matches { get; set; }
}

internal class LanguageToolMatch
{
    public string? Message { get; set; }
    public string? ShortMessage { get; set; }
    public int Offset { get; set; }
    public int Length { get; set; }
    public LanguageToolRule? Rule { get; set; }
    public List<LanguageToolReplacement>? Replacements { get; set; }
}

internal class LanguageToolRule
{
    public string? Id { get; set; }
    public LanguageToolRuleCategory? Category { get; set; }
    public LanguageToolRuleQuality? Quality { get; set; }
}

internal class LanguageToolRuleCategory
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

internal class LanguageToolRuleQuality
{
    public string? IssueType { get; set; }
}

internal class LanguageToolReplacement
{
    public string? Value { get; set; }
}
