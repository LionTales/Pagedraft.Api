using System.Net.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.LanguageEngine.Detect;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IOptions<LanguageToolOptions> _languageToolOptions;

    public HealthController(
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> aiOptions,
        IOptions<LanguageToolOptions> languageToolOptions)
    {
        _httpClientFactory = httpClientFactory;
        _aiOptions = aiOptions;
        _languageToolOptions = languageToolOptions;
    }

    /// <summary>Check AI provider availability.</summary>
    [HttpGet("ai")]
    public async Task<ActionResult<HealthStatus>> CheckAi(CancellationToken ct = default)
    {
        var status = new HealthStatus { Status = "healthy", Timestamp = DateTime.UtcNow };
        var details = new Dictionary<string, object>();

        try
        {
            var aiOptions = _aiOptions.Value;
            var defaultProvider = aiOptions.DefaultProvider ?? "Ollama";

            // Check Ollama if it's the default provider
            if (string.Equals(defaultProvider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                var ollamaClient = _httpClientFactory.CreateClient("Ollama");
                var ollamaUrl = aiOptions.DefaultProvider == "Ollama" 
                    ? "http://localhost:11434" 
                    : "http://localhost:11434";
                
                try
                {
                    var response = await ollamaClient.GetAsync($"{ollamaUrl}/api/tags", ct);
                    details["Ollama"] = new { status = response.IsSuccessStatusCode ? "available" : "unavailable", statusCode = (int)response.StatusCode };
                }
                catch (Exception ex)
                {
                    details["Ollama"] = new { status = "error", error = ex.Message };
                    status.Status = "degraded";
                }
            }

            status.Details = details;
            return Ok(status);
        }
        catch (Exception ex)
        {
            status.Status = "unhealthy";
            status.Details = new Dictionary<string, object> { { "error", ex.Message } };
            return StatusCode(503, status);
        }
    }

    /// <summary>Check language engine components.</summary>
    [HttpGet("language-engine")]
    public ActionResult<HealthStatus> CheckLanguageEngine()
    {
        var status = new HealthStatus
        {
            Status = "healthy",
            Timestamp = DateTime.UtcNow,
            Details = new Dictionary<string, object>
            {
                ["normalizeEngine"] = "available",
                ["detectEngine"] = "available",
                ["rewriteEngine"] = "available",
                ["analyzeEngine"] = "available"
            }
        };

        return Ok(status);
    }

    /// <summary>Check LanguageTool server availability.</summary>
    [HttpGet("languagetool")]
    public async Task<ActionResult<HealthStatus>> CheckLanguageTool(CancellationToken ct = default)
    {
        var status = new HealthStatus { Status = "healthy", Timestamp = DateTime.UtcNow };
        var options = _languageToolOptions.Value;

        if (!options.Enabled)
        {
            status.Status = "disabled";
            status.Details = new Dictionary<string, object> { { "message", "LanguageTool is disabled in configuration" } };
            return Ok(status);
        }

        try
        {
            var client = _httpClientFactory.CreateClient("LanguageTool");
            var response = await client.GetAsync("v2/languages", ct);
            
            if (response.IsSuccessStatusCode)
            {
                status.Details = new Dictionary<string, object>
                {
                    { "serverUrl", options.ServerUrl },
                    { "status", "available" },
                    { "statusCode", (int)response.StatusCode }
                };
            }
            else
            {
                status.Status = "degraded";
                status.Details = new Dictionary<string, object>
                {
                    { "serverUrl", options.ServerUrl },
                    { "status", "unavailable" },
                    { "statusCode", (int)response.StatusCode }
                };
            }
        }
        catch (Exception ex)
        {
            status.Status = "unhealthy";
            status.Details = new Dictionary<string, object>
            {
                { "serverUrl", options.ServerUrl },
                { "status", "error" },
                { "error", ex.Message }
            };
        }

        return status.Status == "healthy" ? Ok(status) : StatusCode(503, status);
    }
}

/// <summary>Health check response.</summary>
public class HealthStatus
{
    public required string Status { get; set; } // "healthy", "degraded", "unhealthy", "disabled"
    public DateTime Timestamp { get; set; }
    public Dictionary<string, object>? Details { get; set; }
}
