using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE SECOND MODEL-IDENTITY SURFACE (tier-ux-rework, widened scope).
///
/// c2 closed the IP leak on the TIER payload and pinned it with
/// <see cref="AiTierDtoDeidentificationTests"/>. The live-browser pass then found the same information
/// arriving through a completely different pipe: the analysis RESULT heading rendered
/// <c>AnalysisResultDto.ModelName</c> verbatim, so a finished run displayed e.g. "ספרותי (Ollama:gemma4:12b)"
/// on screen. The status DTOs carried <c>BuiltWithModel</c> / <c>ActiveModel</c> alongside it - not rendered,
/// but still shipped to the browser and readable by anything holding the response.
///
/// THE RULE THIS FILE PINS: model identity stops at the wire on the RESULT and STATUS surfaces too, not only
/// on the tier surface. The entities keep their stamps - <c>AnalysisResult.ModelName</c>,
/// <c>ChunkSummary.BuiltWithModel</c>, <c>BookFinding.BuiltWithModel</c> are all still persisted, because the
/// server genuinely needs them for diagnostics and for deciding staleness. What the client gets instead is
/// the VERDICT: <c>BuiltWithDifferentModel</c>, a boolean that names nothing. No user-facing copy changed,
/// because the cross-model warning never named a model in the first place.
///
/// TWO ASSERTIONS, DELIBERATELY DIFFERENT IN KIND:
///   1. a REFLECTION sweep over the DTO types, which fails the moment a property called anything like
///      "model" / "provider" / "version" reappears - including a null-valued one, which a text search over a
///      serialized instance would sail straight past;
///   2. a SERIALIZATION check through the real projection (<see cref="AnalysisController.ToDto"/>) with an
///      entity whose ModelName is deliberately set to a realistic "Ollama:gemma4:12b", proving the value is
///      dropped in transit rather than merely absent from a hand-built instance.
///
/// Named *AiTier* so the standing deterministic test filter picks the file up alongside the c2 contract test.
/// </summary>
public class AiTierResultSurfaceDeidentificationTests
{
    /// <summary>
    /// The wire-contract types that a client receives and that previously carried model identity. Kept as an
    /// explicit list rather than a namespace sweep: an unrelated internal DTO gaining a "Model" property is
    /// not this rule's business, and a sweep would turn every such addition into a mysterious failure here.
    /// </summary>
    public static IEnumerable<object[]> DeidentifiedWireTypes() => new[]
    {
        new object[] { typeof(AnalysisResultDto) },
        new object[] { typeof(BookStyleBaselineStatusDto) },
        new object[] { typeof(BookSummaryStatusDto) },
        new object[] { typeof(BookReviewStatusDto) },
        new object[] { typeof(BookFindingDto) },
        new object[] { typeof(ChapterSummaryViewDto) },
        new object[] { typeof(RederiveChapterSummaryResponse) }
    };

    /// <summary>
    /// Fragments that announce model identity in a property NAME. "BuiltWithDifferentModel" is the one
    /// deliberate survivor and is excluded by name below: it is the boolean verdict that REPLACED the two
    /// model strings, so a rule that rejected it would be demanding the leak back.
    /// </summary>
    private static readonly string[] ForbiddenNameFragments = { "model", "provider", "version" };

    private const string AllowedVerdictProperty = "BuiltWithDifferentModel";

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [MemberData(nameof(DeidentifiedWireTypes))]
    public void WireDto_DeclaresNoModelIdentityProperty(Type dtoType)
    {
        var offenders = dtoType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !string.Equals(name, AllowedVerdictProperty, StringComparison.Ordinal))
            .Where(name => ForbiddenNameFragments.Any(f =>
                name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{dtoType.Name} exposes model identity to the client through {string.Join(", ", offenders)}. " +
            "Which provider or model ran is internal IP that changes without notice. If the client needs to " +
            "know something is out of date, send a BOOLEAN verdict (see BuiltWithDifferentModel), not a name.");
    }

    /// <summary>
    /// The verdict field must still EXIST on the three status DTOs. Without this, "delete the model names"
    /// could be satisfied by deleting the staleness signal entirely, and the cross-model warning would
    /// silently stop firing - a regression the reflection test above would happily call a pass.
    /// </summary>
    [Theory]
    [InlineData(typeof(BookStyleBaselineStatusDto))]
    [InlineData(typeof(BookSummaryStatusDto))]
    [InlineData(typeof(BookReviewStatusDto))]
    public void StatusDto_StillCarriesTheStalenessVerdict(Type dtoType)
    {
        var verdict = dtoType.GetProperty(AllowedVerdictProperty, BindingFlags.Public | BindingFlags.Instance);

        Assert.True(verdict != null,
            $"{dtoType.Name} no longer carries {AllowedVerdictProperty}. Removing the model NAMES is correct; " +
            "removing the boolean that tells the client a rebuild is advisable breaks the cross-model warning.");
        Assert.Equal(typeof(bool), verdict!.PropertyType);
    }

    /// <summary>
    /// End-to-end through the real projection: the ENTITY carries a realistic provider:model stamp, the DTO
    /// that comes out of <see cref="AnalysisController.ToDto"/> must not contain it anywhere in its JSON.
    /// This is the case that actually shipped to a user's screen.
    /// </summary>
    [Theory]
    [InlineData("Ollama:gemma4:12b")]
    [InlineData("OpenRouter:google/gemma-4-31b-it")]
    [InlineData("chunked")]
    [InlineData("stream")]
    public void AnalysisResult_ModelStampSurvivesOnTheEntityButNeverReachesTheWire(string stamp)
    {
        var entity = new AnalysisResult
        {
            Id = Guid.NewGuid(),
            ChapterId = Guid.NewGuid(),
            Type = "Proofread",
            ResultText = "טקסט מתוקן.",
            ModelName = stamp,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // The stamp is still on the entity - the server needs it, this test is not asking for it to be lost.
        Assert.Equal(stamp, entity.ModelName);

        var json = JsonSerializer.Serialize(AnalysisController.ToDto(entity), WireOptions);

        Assert.DoesNotContain(stamp, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("modelName", json, StringComparison.OrdinalIgnoreCase);
        // The payload is still the real thing, not an empty object that would pass vacuously. Anchored on
        // ASCII: the default web encoder escapes Hebrew to \uXXXX, so the result text itself is not a usable
        // needle here.
        Assert.Contains("resultText", json, StringComparison.Ordinal);
        Assert.Contains(entity.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The vendor-substring guard from the c2 contract test, applied to the result surface. Reuses the
    /// SHIPPED configuration rather than a hardcoded list, so swapping a model cannot leave this test
    /// asserting against a name nobody runs any more.
    /// </summary>
    [Fact]
    public void AnalysisResultDto_ContainsNoConfiguredProviderOrModelString()
    {
        var identities = AiTierDtoDeidentificationTests.ConfiguredIdentityStrings();

        var entity = new AnalysisResult
        {
            Id = Guid.NewGuid(),
            ChapterId = Guid.NewGuid(),
            Type = "LinguisticAnalysis",
            ResultText = "ניתוח.",
            ModelName = string.Join(":", identities),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(AnalysisController.ToDto(entity), WireOptions);

        foreach (var identity in identities)
        {
            Assert.DoesNotContain(identity, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
