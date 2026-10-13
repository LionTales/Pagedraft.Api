using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using static Pagedraft.Api.Tests.BookProofreadServiceTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-c11: the whole-book proofread ETA reads the two options it claims to, from configuration. Every other estimate
/// test computes its expectation from <c>new AiOptions()</c>, so a formula that hard-coded the shipped defaults, or a
/// service that read a default instance instead of the bound one, passed them all. Here both options are set to
/// values no default holds and the expectation is spelled from THOSE values: once through a configure action, once
/// through the real configuration binder (the key names as an operator writes them in appsettings), and the shipped
/// file is checked to carry both keys.
/// </summary>
public class BookProofreadEstimateOptionsTests
{
    private const int PerChunk = 13;
    private const int PerUnit = 41;

    /// <summary>
    /// Three chapters, four units: a 600-word chapter row, a 100-word chapter row, and a short chapter whose two
    /// written scenes are its units (at least one chunk each). Returns the expected (chunks, units).
    /// </summary>
    private static (int Chunks, int Units) SeedThreeChaptersFourUnits(Harness h)
    {
        h.AddChapter(0, "ראשון", wordCount: 600);
        h.AddChapter(1, "שני", wordCount: 100);
        var split = h.AddChapter(2, "שלישי", wordCount: 30);
        h.AddScene(split, 0, "אחת", written: true);
        h.AddScene(split, 1, "שתיים", written: true);

        // Chunk SIZING is not under test: its option keeps its default, read through THE accessor RunAsync uses.
        var target = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(new AiOptions(), "he", AiTier.Fast);
        Assert.True(target < 600 && target >= 30, "fixture must span several chunks and keep the split chapter small");
        var chunks = (int)Math.Ceiling(600 / (double)target) + (int)Math.Ceiling(100 / (double)target) + 2;
        return (chunks, 4);
    }

    private static async Task AssertTheEstimateUsesTheSetValues(Harness h)
    {
        var (chunks, units) = SeedThreeChaptersFourUnits(h);
        var expected = units * PerUnit + chunks * PerChunk;
        var defaults = new AiOptions();
        Assert.NotEqual(units * defaults.ProofreadSecondsPerUnitOverhead + chunks * defaults.ProofreadSecondsPerChunkEstimate, expected);

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        Assert.Equal(chunks, status.EstimatedChunks);
        Assert.Equal(expected, status.EstimatedSeconds);
        Assert.Equal(expected, status.ForceEstimatedSeconds); // nothing is fresh: the forced plan is the same plan

        // The consent's number is the start's number.
        var start = await StartAndFinish(h, h.Service());
        Assert.True(start.Started);
        Assert.Equal(chunks, start.EstimatedChunks);
        Assert.Equal(expected, start.EstimatedSeconds);
    }

    [Fact]
    public async Task OffDefaultOptions_SetInCode_DriveTheStatusAndTheStartEstimate()
    {
        using var h = new Harness(configure: s => s.Configure<AiOptions>(o =>
        {
            o.ProofreadSecondsPerChunkEstimate = PerChunk;
            o.ProofreadSecondsPerUnitOverhead = PerUnit;
        }));

        await AssertTheEstimateUsesTheSetValues(h);
    }

    [Fact]
    public async Task OffDefaultOptions_BoundFromConfigurationKeys_DriveTheStatusAndTheStartEstimate()
    {
        // The same registration Program.cs makes (Configure<AiOptions>(GetSection(AiOptions.SectionName))), over the
        // key names as they are written in appsettings.json: a renamed property or a misspelled key binds nothing,
        // leaves the defaults, and fails here.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:ProofreadSecondsPerChunkEstimate"] = PerChunk.ToString(),
                ["Ai:ProofreadSecondsPerUnitOverhead"] = PerUnit.ToString()
            })
            .Build();
        using var h = new Harness(configure: s => s.Configure<AiOptions>(config.GetSection(AiOptions.SectionName)));

        await AssertTheEstimateUsesTheSetValues(h);
    }

    [Fact]
    public void TheShippedConfiguration_CarriesBothEtaKeys_AtTheClassDefaults()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var defaults = new AiOptions();

        // The key itself, not a bind: a misspelled key in the file binds nothing and would read as the default.
        Assert.Equal(defaults.ProofreadSecondsPerChunkEstimate.ToString(), config["Ai:ProofreadSecondsPerChunkEstimate"]);
        Assert.Equal(defaults.ProofreadSecondsPerUnitOverhead.ToString(), config["Ai:ProofreadSecondsPerUnitOverhead"]);
        Assert.Equal(defaults.MaxParallelBookProofreadChapters.ToString(), config["Ai:MaxParallelBookProofreadChapters"]);
    }
}
