using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// WHAT Ai:HebrewStyle ACTUALLY SHIPS, bound through the real configuration binder over the real
/// files - not read off the class defaults.
///
/// A class default is a DIFFERENT CLAIM from a shipped default: the section can be present and set to
/// the opposite value, in which case the class default is never consulted at all. Both halves are
/// pinned here because the shape guard is the only switch in this project that DELETES model output,
/// so "is it on in production, and does anything have to say so" must be answerable from a test.
/// Same idiom as <c>ProofreadPromptArmTests.TheShippedConfiguration_BindsTheSection_AndLeavesEveryArmOff</c>
/// and <c>AnalysisRepairConfigParityTests</c>.
/// </summary>
public class HebrewStyleConfigParityTests
{
    [Fact]
    public void TheShippedConfiguration_BindsTheSection_AndShipsBothSwitchesOn()
    {
        Assert.Equal("Ai:HebrewStyle", HebrewStyleOptions.SectionName);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var section = config.GetSection(HebrewStyleOptions.SectionName);
        Assert.True(section.Exists(),
            "the Ai:HebrewStyle section is missing from the shipped appsettings.json, so neither switch " +
            "is documented where an operator would look for it");

        var bound = new HebrewStyleOptions();
        section.Bind(bound);

        Assert.True(bound.EnforceKtivMale);
        Assert.True(bound.DropOrthographicallyImpossibleSuggestions,
            "the orthographic-impossibility safety net ships OFF. It may be turned off deliberately as " +
            "a kill switch, but that is a decision a plan makes, not a value that drifts.");
    }

    /// <summary>
    /// THE CLASS DEFAULTS MATCH THE SHIPPED VALUES, so a test or a service constructing
    /// <see cref="HebrewStyleOptions"/> by hand exercises the posture production runs rather than a
    /// second, quieter one. Roughly half the suite constructs it that way.
    /// </summary>
    [Fact]
    public void TheClassDefaults_MatchTheShippedValues()
    {
        var defaults = new HebrewStyleOptions();

        Assert.True(defaults.EnforceKtivMale);
        Assert.True(defaults.DropOrthographicallyImpossibleSuggestions);
    }

    /// <summary>
    /// THE CONTAINER CAN STILL BUILD THE SERVICE, and it builds it with the CONFIGURED options rather
    /// than falling back to the parameterless constructor.
    ///
    /// This is not a formality: <c>SuggestionDiffService</c> is registered by type
    /// (<c>AddSingleton&lt;SuggestionDiffService&gt;()</c> in Program.cs) and now carries three
    /// constructors. The default container picks the longest one it can satisfy, and an ambiguity
    /// there is a STARTUP failure, not a compile error - it would surface as a 500 on the first
    /// analysis rather than as a red build. So the resolution is exercised, and the resolved instance
    /// is proved to have read the configuration by driving it with the switch set to false and
    /// checking the behaviour changed.
    /// </summary>
    [Fact]
    public void TheContainerResolvesTheDiffService_WithTheConfiguredHebrewStyleOptions()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:HebrewStyle:DropOrthographicallyImpossibleSuggestions"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<HebrewStyleOptions>(config.GetSection(HebrewStyleOptions.SectionName));
        services.AddSingleton<SuggestionDiffService>();

        using var provider = services.BuildServiceProvider();
        var diff = provider.GetRequiredService<SuggestionDiffService>();

        diff.ComputeProofreadSuggestions(
            "הוא צמצם את הפער.", "הוא צמץם את הפער.", out var outcome);

        Assert.False(outcome.Ran,
            "the container resolved a SuggestionDiffService that ignored Ai:HebrewStyle - most likely " +
            "it picked the parameterless constructor, which would make the kill switch dead in " +
            "production while every direct-construction test still passed");
    }

    /// <summary>
    /// THE CONTAINER STILL RESOLVES THE SERVICE EVEN IF <see cref="HebrewStyleOptions"/> IS ALSO
    /// REGISTERED AS A BARE SERVICE, which is the scenario the previous test does not cover.
    ///
    /// <c>SuggestionDiffService</c> used to carry TWO public one-parameter constructors -
    /// <c>SuggestionDiffService(IOptions&lt;HebrewStyleOptions&gt;)</c> and
    /// <c>SuggestionDiffService(HebrewStyleOptions)</c>. Today nothing in this codebase registers a
    /// bare <see cref="HebrewStyleOptions"/>, so the default container never had to choose between
    /// them. The moment something did, the container would see two equally-good one-parameter
    /// constructors and throw <see cref="InvalidOperationException"/> at startup, not at build. This
    /// test manufactures that exact scenario on purpose (registers the bare type alongside the
    /// configured <c>IOptions</c> wrapper) so the hazard cannot come back silently: the raw-options
    /// constructor is now <c>internal</c>, leaving only one public one-parameter constructor for the
    /// container to see.
    /// </summary>
    [Fact]
    public void TheContainerResolvesTheDiffService_EvenWhenHebrewStyleOptionsIsAlsoBareRegistered()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:HebrewStyle:DropOrthographicallyImpossibleSuggestions"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<HebrewStyleOptions>(config.GetSection(HebrewStyleOptions.SectionName));
        services.AddSingleton(new HebrewStyleOptions
        {
            DropOrthographicallyImpossibleSuggestions = true
        });
        services.AddSingleton<SuggestionDiffService>();

        using var provider = services.BuildServiceProvider();
        var diff = provider.GetRequiredService<SuggestionDiffService>();

        diff.ComputeProofreadSuggestions(
            "הוא צמצם את הפער.", "הוא צמץם את הפער.", out var outcome);

        Assert.False(outcome.Ran,
            "the container resolved a SuggestionDiffService that ignored the configured " +
            "Ai:HebrewStyle options in favor of the bare-registered HebrewStyleOptions instance - " +
            "it should be impossible for the container to even see two competing one-parameter " +
            "constructors now that the raw-options constructor is internal");
    }

    /// <summary>
    /// PRODUCTION INHERITS THE BASE VALUES because it declares no Ai:HebrewStyle block of its own.
    /// Asserted rather than assumed, because the moment Production DOES declare the block the
    /// inheritance stops and the two files have to be kept in sync by hand - which is a rule this
    /// project already carries for Ai:AnalysisRepair and Ai:Tier, and which nobody would think to
    /// apply to a block that did not exist yet.
    ///
    /// NON-VACUITY: the Production file is asserted to have loaded AND to carry a sibling Ai key, so a
    /// missing or empty file cannot pass this as "no HebrewStyle override".
    /// </summary>
    [Fact]
    public void ProductionDeclaresNoHebrewStyleOverride_SoTheBaseValuesGovernThere()
    {
        var productionPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Production.json");
        Assert.True(File.Exists(productionPath),
            "appsettings.Production.json is not in the output directory, so this test would be " +
            "asserting the absence of a file rather than the absence of an override");

        var production = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Production.json", optional: false)
            .Build();

        Assert.True(production.GetSection("Ai:AnalysisRepair").Exists(),
            "the Production file carries no Ai section at all, so 'it does not override Ai:HebrewStyle' " +
            "is vacuously true and proves nothing about the layering");

        Assert.False(production.GetSection(HebrewStyleOptions.SectionName).Exists(),
            "appsettings.Production.json now declares Ai:HebrewStyle. That is allowed, but it means " +
            "the base file no longer governs production: add the values there explicitly and add a " +
            "parity assertion for them, the way Ai:AnalysisRepair and Ai:Tier are handled.");
    }
}
