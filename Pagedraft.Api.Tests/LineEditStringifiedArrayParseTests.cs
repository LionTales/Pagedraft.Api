using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE SILENT ZERO (be-c08). In JSON mode a model can answer with the expected array INSIDE a string:
/// <c>{"suggestions":"[{\"original\":...}]"}</c>. Measured 2026-09-10: 2 of 6 gemma-4-31b LineEdit replies on
/// OpenRouter had that shape, and every LineEdit parse path (primary, aggressive, salvage, XML) returned null,
/// so the run persisted 0 suggestions and looked like a clean chapter. <see cref="StringifiedArrayFields"/>
/// unwraps it at the shared parse step; these tests drive the whole production LineEdit run
/// (<c>UnifiedAnalysisService.RunAsync</c> with a router that returns the reply verbatim) so the assertion is
/// what the author would be shown, not what a helper returns. The two failure cells pin the restraint: a
/// string that is NOT a JSON array must still fail, never turn into suggestions.
/// </summary>
public class LineEditStringifiedArrayParseTests
{
    private const string ChapterText = "הילד הלך לבית הספר בבוקר מוקדם. השמש זרחה מעל הגבעות הירוקות.";

    // Two edits whose originals exist verbatim in ChapterText, so both anchor.
    private static readonly object[] TwoEdits =
    {
        new { original = "הלך לבית הספר", suggested = "צעד לבית הספר", reason = "r1", category = "word-choice" },
        new { original = "זרחה מעל הגבעות", suggested = "עלתה מעל הגבעות", reason = "r2", category = "style" },
    };

    private static string ArrayText => JsonSerializer.Serialize(TwoEdits);

    private static async Task<AnalysisResult> RunLineEditAsync(string modelReply)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "T", Language = "he" });
        await db.SaveChangesAsync();

        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = modelReply, Provider = "test", Model = "test" });

        var context = new Mock<IAnalysisContextService>();
        context.Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.LineEdit, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = ChapterText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.LineEdit,
                BookId = bookId,
                ChapterId = chapterId,
            });

        var svc = new UnifiedAnalysisService(
            db, router.Object, new PromptFactory(), new SfdtConversionService(),
            Options.Create(new AiOptions()), NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(), context.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        return await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.LineEdit, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);
    }

    private static int StructuredSuggestionCount(AnalysisResult result)
    {
        Assert.False(string.IsNullOrWhiteSpace(result.StructuredResult),
            "The structured parse failed, so the run has no LineEditResult at all.");
        using var doc = JsonDocument.Parse(result.StructuredResult!);
        return doc.RootElement.GetProperty("suggestions").GetArrayLength();
    }

    [Fact]
    public async Task AStringifiedSuggestionsArray_ParsesToItsSuggestions_AndBothAnchor()
    {
        var reply = JsonSerializer.Serialize(new { suggestions = ArrayText, overallFeedback = "ok" });
        Assert.Contains("\"suggestions\":\"[", reply); // the fixture IS the measured shape

        var result = await RunLineEditAsync(reply);

        Assert.Equal(2, StructuredSuggestionCount(result));
        Assert.Equal(2, result.Suggestions.Count);
        Assert.Contains(result.Suggestions, s => s.SuggestedText == "צעד לבית הספר");
        Assert.Contains(result.Suggestions, s => s.SuggestedText == "עלתה מעל הגבעות");
    }

    [Fact]
    public async Task TheRecordedColonPrefixedShape_ParsesToItsSuggestions()
    {
        // The 2026-09-10 note records the string opening with ": [" (the key's colon repeated inside it).
        var reply = JsonSerializer.Serialize(new { suggestions = ": " + ArrayText, overallFeedback = "ok" });

        var result = await RunLineEditAsync(reply);

        Assert.Equal(2, result.Suggestions.Count);
    }

    /// <summary>A bidi mark BETWEEN JSON tokens defeats the primary parse (it is not JSON whitespace), so the
    /// reply reaches the aggressive fallback, which strips it: that path needs the same repair. This is the cell
    /// that owns the aggressive-parse call site; the plain fixtures above are recovered by either site.</summary>
    [Fact]
    public async Task AStringifiedArrayBehindABidiMark_IsUnwrappedByTheAggressiveFallback()
    {
        var reply = JsonSerializer.Serialize(new { suggestions = ArrayText, overallFeedback = "ok" })
            .Replace("{\"suggestions\":", "{‏\"suggestions\":", StringComparison.Ordinal);

        var result = await RunLineEditAsync(reply);

        Assert.Equal(2, result.Suggestions.Count);
    }

    [Fact]
    public async Task AWellFormedReply_IsUnchangedByTheRepair()
    {
        var reply = JsonSerializer.Serialize(new { suggestions = TwoEdits, overallFeedback = "ok" });

        var result = await RunLineEditAsync(reply);

        Assert.Equal(2, result.Suggestions.Count);
        Assert.Same(reply, StringifiedArrayFields.Unwrap<LineEditResult>(reply));
    }

    /// <summary>final-r01: a reply with a DUPLICATED top-level key parsed before the repair existed (the typed
    /// deserialize keeps the last value), but <c>JsonObject</c> throws an ArgumentException (not a JsonException)
    /// when it enumerates one, which escaped every parse path's <c>catch (JsonException)</c>. The repair must
    /// leave such a reply to the deserialize exactly as it came.</summary>
    [Fact]
    public async Task AReplyWithADuplicatedKey_StillParses_AsItDidBeforeTheRepair()
    {
        var wellFormed = JsonSerializer.Serialize(new { suggestions = TwoEdits, overallFeedback = "a" });
        var reply = wellFormed[..^1] + ",\"overallFeedback\":\"b\"}";
        Assert.EndsWith("\"overallFeedback\":\"a\",\"overallFeedback\":\"b\"}", reply); // the duplicate IS there

        var result = await RunLineEditAsync(reply);

        Assert.Equal(2, result.Suggestions.Count);
        Assert.Same(reply, StringifiedArrayFields.Unwrap<LineEditResult>(reply));
    }

    [Theory]
    [InlineData("אין הצעות לשיפור")]       // prose: genuinely not JSON
    [InlineData("[{\"original\": \"הלך")]   // a TRUNCATED array: not a complete JSON array
    public async Task AStringThatIsNotAJsonArray_StillFails_AndYieldsNoSuggestions(string inner)
    {
        var reply = JsonSerializer.Serialize(new { suggestions = inner, overallFeedback = "" });

        var result = await RunLineEditAsync(reply);

        Assert.Null(result.StructuredResult);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task AStringHoldingAJsonObject_IsNotMistakenForAnArray()
    {
        var oneObject = JsonSerializer.Serialize(TwoEdits[0]);
        var reply = JsonSerializer.Serialize(new { suggestions = oneObject, overallFeedback = "" });

        var result = await RunLineEditAsync(reply);

        Assert.Null(result.StructuredResult);
        Assert.Empty(result.Suggestions);
        Assert.Null(StringifiedArrayFields.TryParseArray(oneObject));
    }

    /// <summary>LinguisticAnalysis sends JsonMode too and parses through the generic
    /// <c>TryExtractAndReserialize</c>: its two array fields get the same repair, its scalars are untouched.</summary>
    [Fact]
    public void LinguisticAnalysis_StringifiedDeviations_ParseToTheirItems()
    {
        var deviations = JsonSerializer.Serialize(new[]
        {
            new { metric = "sentenceLength", sceneValue = 22.0, chapterBaseline = 14.0, note = "x" },
        });
        var reply = JsonSerializer.Serialize(new
        {
            summary = "s",
            grammaticalityScore = 0.9,
            deviations,
            consistencyIssues = "[]",
        });

        var json = UnifiedAnalysisService.TryExtractAndReserialize<LinguisticAnalysisResult>(reply);

        Assert.NotNull(json);
        var parsed = JsonSerializer.Deserialize<LinguisticAnalysisResult>(json!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Single(parsed.Deviations);
        Assert.Equal("sentenceLength", parsed.Deviations[0].Metric);
        Assert.Equal(22.0, parsed.Deviations[0].SceneValue);
        Assert.Empty(parsed.ConsistencyIssues);
        Assert.Equal("s", parsed.Summary); // a string-typed field is never touched
    }
}
