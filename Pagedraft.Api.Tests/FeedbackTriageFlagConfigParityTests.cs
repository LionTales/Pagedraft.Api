using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Pagedraft.Api.Services.Feedback;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Pins the shipped values of <c>Feedback:TriageEnabled</c> in BOTH config files (Show C2, d1 section (4)),
/// mirroring <c>AiTierConsentFlagConfigParityTests</c> / <c>AnalysisRepairConfigParityTests</c>.
///
/// <para>WHY A TEST AND NOT A COMMENT. Nothing at runtime relates the two files:
/// <c>appsettings.Production.json</c> REPLACES a block at the key level rather than merging into it, so an
/// omitted key there does not inherit the base file's <c>true</c> - it falls through to the class default.
/// And the direction that matters is asymmetric: base <c>true</c> going false would silently remove the
/// owner's reading tool from their own machine, while production <c>false</c> going true would serve
/// MANUSCRIPT-BEARING triage evidence from a deployment with no <c>[Authorize]</c> anywhere. The second is
/// the one that must never happen by accident, so both values are pinned by name and a flip in either file
/// turns this red on purpose - a future flip is expected to edit this test deliberately, together with the
/// auth work.</para>
/// </summary>
public class FeedbackTriageFlagConfigParityTests
{
    [Fact]
    public void TheTriageFlag_IsTrueInTheBaseFile_AndFalseInProduction()
    {
        var basePath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var prodPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.Production.json"));

        var baseValue = ReadFlag(basePath);
        var prodValue = ReadFlag(prodPath);

        // NON-VACUITY: the key must be PRESENT in both files. A null here would mean the assertions below
        // were comparing "absent" against an expectation, which is the shape that lets a whole config
        // block go missing while the test still reads as though it checked something.
        Assert.True(baseValue.HasValue,
            $"Feedback:TriageEnabled is missing from {basePath}. The base file must state it explicitly.");
        Assert.True(prodValue.HasValue,
            $"Feedback:TriageEnabled is missing from {prodPath}. Production must state it EXPLICITLY rather " +
            "than relying on inheritance - Production replaces a block rather than merging into it, so an " +
            "omitted key falls through to the class default instead of the base file's value.");

        Assert.True(baseValue!.Value,
            "Feedback:TriageEnabled must ship TRUE in the base appsettings.json: it is the owner's own " +
            "machine, and Development inherits this file.");
        Assert.False(prodValue!.Value,
            "Feedback:TriageEnabled must ship FALSE in appsettings.Production.json. The triage detail " +
            "composes manuscript-bearing evidence and PageDraft has no [Authorize] anywhere yet. Flip this " +
            "only together with the login and [Authorize] on the three gated routes.");
    }

    /// <summary>
    /// The CLASS default is false too, which is the safe posture for programmatic and test construction:
    /// an <c>Options.Create(new FeedbackOptions())</c> in a caller that never thought about the flag gets
    /// the closed surface, not the open one.
    /// </summary>
    [Fact]
    public void TheClassDefault_IsClosed()
    {
        Assert.False(new FeedbackOptions().TriageEnabled);
    }

    private static bool? ReadFlag(string path)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();
        return config.GetSection($"{FeedbackOptions.SectionName}:TriageEnabled").Get<bool?>();
    }

    private static string FindUpward(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find '{relativePath}' walking up from {AppContext.BaseDirectory}.");
    }
}
