using System.Collections.Generic;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// LIVES AT THE TEST PROJECT ROOT ON PURPOSE. This class is deterministic, but it used to sit in
/// <c>LanguageEngine/</c>, the folder whose namespace the standing deterministic filter EXCLUDES. It was
/// only ever reached because the standing NARROW filter happens to carry a <c>~ProviderPayload</c> term,
/// i.e. it ran by coincidence of its own name rather than by any rule. Moved 2026-07-29 so the
/// deterministic suite covers it too; the class name is unchanged, so the narrow filter still reaches it.
///
/// Pins the per-model-family payload shape for the cloud providers so a refactor can't silently
/// reintroduce a 400 (e.g. sending `temperature` to a Claude Opus model, or `max_tokens` to gpt-5).
/// These assert against the in-memory payload dictionary that <c>BuildPayload</c> returns — no network,
/// no API key. When a NEW model family that drops/changes sampling params ships, add it here AND to the
/// provider predicate (AnthropicProvider.AnthropicSupportsTemperature / OpenAiProvider gpt-5 prefix).
/// </summary>
public class ProviderPayloadTests
{
    private static readonly ProviderTuningOptions Tuning = new() { Temperature = 0.2, MaxTokens = 2048 };

    private static ResolvedAiRequest MakeRequest(string provider, string model) => new()
    {
        SystemMessage = "system message",
        Instruction = "instruction",
        InputText = "input text",
        Selection = new AiModelSelection { Provider = provider, Model = model }
    };

    // ---- Anthropic ---------------------------------------------------------

    [Theory]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-fable-5")]
    public void Anthropic_OmitsTemperature_ForModelsThatReject(string model)
    {
        var payload = AnthropicProvider.BuildPayload(model, MakeRequest("Anthropic", model), Tuning);

        Assert.False(payload.ContainsKey("temperature"),
            $"Anthropic model '{model}' must NOT include `temperature` (it rejects it with HTTP 400).");
        // Sanity: the rest of the body is still well-formed.
        Assert.Equal(model, payload["model"]);
        Assert.Equal(Tuning.MaxTokens, payload["max_tokens"]);
        Assert.True(payload.ContainsKey("system"));
        Assert.True(payload.ContainsKey("messages"));
    }

    [Theory]
    [InlineData("claude-sonnet-4-6")]
    public void Anthropic_IncludesTemperature_ForModelsThatAccept(string model)
    {
        var payload = AnthropicProvider.BuildPayload(model, MakeRequest("Anthropic", model), Tuning);

        Assert.True(payload.ContainsKey("temperature"),
            $"Anthropic model '{model}' must include `temperature`.");
        Assert.Equal(Tuning.Temperature, payload["temperature"]);
    }

    // ---- OpenAI ------------------------------------------------------------

    [Theory]
    [InlineData("gpt-5")]
    public void OpenAi_UsesMaxCompletionTokens_AndOmitsTemperature_ForGpt5(string model)
    {
        var payload = OpenAiProvider.BuildPayload(model, MakeRequest("OpenAI", model), Tuning);

        Assert.True(payload.ContainsKey("max_completion_tokens"),
            $"OpenAI model '{model}' must use `max_completion_tokens`.");
        Assert.Equal(Tuning.MaxTokens, payload["max_completion_tokens"]);
        Assert.False(payload.ContainsKey("max_tokens"),
            $"OpenAI model '{model}' must NOT use `max_tokens` (gpt-5 rejects it with HTTP 400).");
        Assert.False(payload.ContainsKey("temperature"),
            $"OpenAI model '{model}' must NOT include `temperature` (gpt-5 only allows the default).");
    }

    [Theory]
    [InlineData("gpt-4o")]
    public void OpenAi_UsesMaxTokens_AndTemperature_ForOlderModels(string model)
    {
        var payload = OpenAiProvider.BuildPayload(model, MakeRequest("OpenAI", model), Tuning);

        Assert.True(payload.ContainsKey("max_tokens"),
            $"OpenAI model '{model}' must use `max_tokens`.");
        Assert.Equal(Tuning.MaxTokens, payload["max_tokens"]);
        Assert.True(payload.ContainsKey("temperature"),
            $"OpenAI model '{model}' must include `temperature`.");
        Assert.Equal(Tuning.Temperature, payload["temperature"]);
        Assert.False(payload.ContainsKey("max_completion_tokens"),
            $"OpenAI model '{model}' must NOT use `max_completion_tokens`.");
    }
}
