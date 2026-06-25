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

    [Fact]
    public async Task BookReview_SendsStructuredInstructionVerbatim_WithoutLegacyPipelineText()
    {
        var (router, provider) = BuildRouter();
        // A representative BookReview instruction: [BOOK_CONTEXT] + a JSON-schema dimension prompt.
        const string instruction =
            "[BOOK_CONTEXT]\nGenre: drama\n[/BOOK_CONTEXT]\nReview the whole book and return ONLY a JSON object with a findings array.";

        // Hebrew language: without the verbatim allowlist add, the default GetPrompt fallthrough would
        // append "השב בעברית בלבד." to this structured JSON prompt. It must NOT be appended.
        await router.CompleteAsync(new AiRequest
        {
            TaskType = AiTaskType.BookReview,
            Instruction = instruction,
            Language = "he",
            InputText = string.Empty,
            JsonMode = true
        });

        Assert.NotNull(provider.Captured);
        // Verbatim: the structured instruction is the whole instruction, with no pipeline text appended.
        Assert.Equal(instruction, provider.Captured!.Instruction);
        // The Hebrew default pipeline instruction ("respond in Hebrew only") must not leak in.
        Assert.DoesNotContain("השב בעברית בלבד", provider.Captured.Instruction);
        Assert.True(provider.Captured.JsonMode);
    }

    [Fact]
    public void GetPrompt_BookReview_UsesLanguageAppropriateSystemMessage()
    {
        var factory = new PromptFactory();

        var (enSystem, enInstruction) = factory.GetPrompt(AiTaskType.BookReview, "en");
        var (heSystem, _) = factory.GetPrompt(AiTaskType.BookReview, "he");

        // English request: must NOT receive the Hebrew default system message, and the system message
        // must contain no Hebrew letters (i.e. it is the English analysis constant).
        Assert.NotEqual(heSystem, enSystem);
        Assert.DoesNotContain(enSystem, c => c >= '֐' && c <= '׿');
        Assert.Contains("literary", enSystem, System.StringComparison.OrdinalIgnoreCase);

        // The router supplies the real instruction verbatim, so GetPrompt returns an empty pipeline one.
        Assert.Equal(string.Empty, enInstruction);

        // Hebrew request: the system message must be Hebrew (the Hebrew analysis constant).
        Assert.Contains(heSystem, c => c >= '֐' && c <= '׿');
    }

    [Fact]
    public void GetPrompt_GenericChat_UsesAssistantSystem_NotProofreader()
    {
        var factory = new PromptFactory();

        var (proofreadSystem, _) = factory.GetPrompt(AiTaskType.Proofread, "he");
        var (heSystem, _) = factory.GetPrompt(AiTaskType.GenericChat, "he");
        var (enSystem, _) = factory.GetPrompt(AiTaskType.GenericChat, "en");

        // Regression: a free-form Custom prompt (and QA) returned a near-empty fragment because GenericChat
        // reused the PROOFREADER system message, so the model proofread the chapter instead of answering the
        // user's question. GenericChat must use a neutral literary-assistant system, distinct from Proofread.
        Assert.NotEqual(proofreadSystem, heSystem);
        Assert.DoesNotContain("מגיה", heSystem);            // not the "...עורך לשוני ומגיה טקסטים" proofreader framing
        Assert.Contains("עוזר", heSystem);                  // assistant framing
        Assert.Contains(heSystem, c => c >= '֐' && c <= '׿'); // Hebrew system for a he request

        // English request: English assistant system, no Hebrew letters.
        Assert.DoesNotContain(enSystem, c => c >= '֐' && c <= '׿');
        Assert.Contains("assistant", enSystem, System.StringComparison.OrdinalIgnoreCase);
    }
}
