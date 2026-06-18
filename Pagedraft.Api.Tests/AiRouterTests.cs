using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Guards how AiRouter composes the final instruction. Structured-JSON task types (LineEdit,
/// LinguisticAnalysis) must be sent VERBATIM, without the legacy GetPrompt() pipeline text appended —
/// that text asks for headings / numbered lists and contradicts format=json output.
/// </summary>
public class AiRouterTests
{
    private sealed class CapturingProvider : IAiAnalysisProvider
    {
        public ResolvedAiRequest? Captured { get; private set; }

        public Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(new AiResponse { Content = "{}", Provider = "Fake", Model = request.Selection.Model });
        }
    }

    private static (AiRouter Router, CapturingProvider Provider) BuildRouter()
    {
        var provider = new CapturingProvider();
        var options = Options.Create(new AiOptions { DefaultProvider = "Fake", DefaultModel = "m" });
        var providers = new Dictionary<string, IAiAnalysisProvider> { ["Fake"] = provider };
        return (new AiRouter(options, new PromptFactory(), providers), provider);
    }

    [Fact]
    public async Task LinguisticAnalysis_SendsStructuredInstructionVerbatim_WithoutLegacyPipelineText()
    {
        var (router, provider) = BuildRouter();
        const string structured = "Return ONLY a JSON object with deviations and consistencyIssues.";

        await router.CompleteAsync(new AiRequest
        {
            TaskType = AiTaskType.LinguisticAnalysis,
            Instruction = structured,
            Language = "en",
            InputText = "Some text.",
            JsonMode = true
        });

        Assert.NotNull(provider.Captured);
        // Verbatim: the structured instruction is the whole instruction, with no legacy text appended.
        Assert.Equal(structured, provider.Captured!.Instruction);
        // The legacy LinguisticAnalysis pipeline prompt asks for "numbered lists" — it must not leak in.
        Assert.DoesNotContain("numbered lists", provider.Captured.Instruction);
        Assert.True(provider.Captured.JsonMode);
    }

    [Fact]
    public async Task LineEdit_StillSendsInstructionVerbatim()
    {
        var (router, provider) = BuildRouter();
        const string instruction = "Perform a sentence-level line edit and return JSON.";

        await router.CompleteAsync(new AiRequest
        {
            TaskType = AiTaskType.LineEdit,
            Instruction = instruction,
            Language = "en",
            InputText = "Some text.",
            JsonMode = true
        });

        Assert.NotNull(provider.Captured);
        Assert.Equal(instruction, provider.Captured!.Instruction);
    }

    [Fact]
    public async Task NonStructuredTask_AppendsLegacyPipelineInstruction()
    {
        var (router, provider) = BuildRouter();
        const string instruction = "Summarize this.";

        await router.CompleteAsync(new AiRequest
        {
            TaskType = AiTaskType.Summarization,
            Instruction = instruction,
            Language = "en",
            InputText = "Some text."
        });

        Assert.NotNull(provider.Captured);
        // Non-verbatim tasks keep the legacy behavior: caller instruction first, pipeline text appended.
        Assert.StartsWith(instruction, provider.Captured!.Instruction);
        Assert.NotEqual(instruction, provider.Captured.Instruction);
    }
}
