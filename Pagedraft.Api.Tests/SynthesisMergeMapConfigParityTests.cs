using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Pagedraft.Api.Services.Ai;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Pins the shipped default of the b8 kill-switch <c>Ai:BookReview:SynthesisMergeMap</c> (gates the
/// synthesis merge-map pass's only mutation - a model-proposed merge that DELETES the absorbed findings;
/// full contract on <see cref="BookReviewOptions.SynthesisMergeMap"/>'s xmldoc in Services/Ai/AiOptions.cs)
/// to FALSE in both <c>appsettings.json</c> and <c>appsettings.Production.json</c>, and asserts the two
/// files agree - mirroring <see cref="AnalysisRepairConfigParityTests"/>'s contract for
/// <c>Ai:AnalysisRepair:Mode</c>.
///
/// Before this test the switch existed ONLY as a class default (<c>BookReviewOptions.SynthesisMergeMap =
/// false</c>) with no appsettings key at all and nothing that would fail if a future edit flipped it - an
/// appsettings key added with <c>true</c> would have silently enabled a MEASURED-harmful model-driven
/// delete with nothing going red (the b8 live gate: gemma4:12b falsely merged two unrelated findings that
/// were a dimension's only two findings, and the dimension vanished from the score panel). This test is
/// the guard: a future flip in either file must fail one of the assertions below.
/// </summary>
public class SynthesisMergeMapConfigParityTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void ShippedDefault_BindsIntoAiOptions_AndIsFalse(string fileName)
    {
        var path = FindUpward(Path.Combine("Pagedraft.Api", fileName));
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();

        var ai = config.GetSection("Ai").Get<AiOptions>();
        Assert.NotNull(ai);
        Assert.NotNull(ai!.BookReview);

        // (1) The bound value came from the FILE, not silently from the class default (both happen to be
        //     false right now, so an equality check against the class default alone would prove nothing) -
        //     compare the bound bool to the RAW STRING in the JSON instead. If the key were ever
        //     misspelled/removed, the binder would silently fall back to the class default and this
        //     assertion catches it.
        var raw = config.GetSection("Ai:BookReview:SynthesisMergeMap").Value;
        Assert.False(string.IsNullOrWhiteSpace(raw),
            $"{fileName} has no Ai:BookReview:SynthesisMergeMap value ({path}).");
        Assert.True(bool.TryParse(raw, out var rawBool),
            $"{fileName} ships Ai:BookReview:SynthesisMergeMap=\"{raw}\", which is not a valid bool ({path}).");
        Assert.Equal(rawBool, ai.BookReview.SynthesisMergeMap);

        // (2) THE POINT: the shipped default is FALSE. This switch gates the only mutation the synthesis
        //     merge-map pass can make - a model-driven DELETE that the b8 live gate measured to falsely
        //     merge unrelated findings and erase a whole dimension from the score panel (see
        //     BookReviewOptions.SynthesisMergeMap's xmldoc in Services/Ai/AiOptions.cs). A future edit that
        //     flips this key to true in either file must turn this assertion red.
        Assert.False(ai.BookReview.SynthesisMergeMap,
            $"{fileName} ships Ai:BookReview:SynthesisMergeMap=true. This flips ON a model-driven delete " +
            "that the b8 live gate measured to falsely merge unrelated findings and erase a whole " +
            "dimension's findings from the score panel. Flipping this default is a deliberate product " +
            "decision (revisit with a model whose measure-mode log has been read and is clean), not a " +
            "routine config edit - if you meant to do this, update this test's expectation alongside it.");
    }

    [Fact]
    public void BaseAndProduction_AgreeOnTheSwitch()
    {
        var basePath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var prodPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.Production.json"));

        var baseValue = LoadSwitch(basePath);
        var prodValue = LoadSwitch(prodPath);

        Assert.False(string.IsNullOrWhiteSpace(baseValue),
            $"appsettings.json Ai:BookReview:SynthesisMergeMap was null or empty ({basePath}).");
        Assert.False(string.IsNullOrWhiteSpace(prodValue),
            $"appsettings.Production.json Ai:BookReview:SynthesisMergeMap was null or empty ({prodPath}).");

        Assert.True(string.Equals(baseValue, prodValue, StringComparison.OrdinalIgnoreCase),
            "Ai:BookReview:SynthesisMergeMap differs between appsettings.json and appsettings.Production.json " +
            $"(base={baseValue}, prod={prodValue}) - keep the kill-switch's shipped state identical across " +
            "environments.");
    }

    private static string? LoadSwitch(string path)
    {
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        return config.GetSection("Ai:BookReview:SynthesisMergeMap").Value;
    }

    // Mirrors AnalysisRepairConfigParityTests.FindUpward: walks up from the test assembly's output
    // directory to locate the API project's appsettings files.
    private static string FindUpward(string relativeSubPath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeSubPath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate " + relativeSubPath + " above " + AppContext.BaseDirectory);
    }
}
