using System;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Xunit;

namespace Pagedraft.Api.Tests;

public class BuildLineEditChunkPromptTests
{
    private static AnalysisContext CreateBaseContext(
        string? preceding = null,
        string? following = null,
        StyleProfileData? style = null) =>
        new()
        {
            TargetText = "Target text",
            PrecedingContext = preceding,
            FollowingContext = following,
            StyleProfile = style
        };

    [Fact]
    public void FirstChunk_UsesGlobalPrecedingAndLocalFollowing_WhenAvailable()
    {
        var factory = new PromptFactory();
        var context = CreateBaseContext(
            preceding: "GLOBAL_PRECEDING",
            following: "GLOBAL_FOLLOWING");

        var prompt = factory.BuildLineEditChunkPrompt(
            language: "en",
            context: context,
            localOverlapBefore: "LOCAL_BEFORE",
            localOverlapAfter: "LOCAL_AFTER",
            isFirstChunk: true,
            isLastChunk: false);

        Assert.Contains("[PRECEDING_CONTEXT]", prompt);
        Assert.Contains("GLOBAL_PRECEDING", prompt);
        Assert.DoesNotContain("LOCAL_BEFORE", prompt, StringComparison.Ordinal);

        Assert.Contains("[FOLLOWING_CONTEXT]", prompt);
        Assert.Contains("LOCAL_AFTER", prompt);
        Assert.DoesNotContain("GLOBAL_FOLLOWING", prompt, StringComparison.Ordinal);

        Assert.DoesNotContain("[/STYLE_PROFILE]", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void MiddleChunk_UsesLocalPrecedingAndLocalFollowing()
    {
        var factory = new PromptFactory();
        var context = CreateBaseContext(
            preceding: "GLOBAL_PRECEDING",
            following: "GLOBAL_FOLLOWING");

        var prompt = factory.BuildLineEditChunkPrompt(
            language: "en",
            context: context,
            localOverlapBefore: "LOCAL_BEFORE",
            localOverlapAfter: "LOCAL_AFTER",
            isFirstChunk: false,
            isLastChunk: false);

        Assert.Contains("[PRECEDING_CONTEXT]", prompt);
        Assert.Contains("LOCAL_BEFORE", prompt);
        Assert.DoesNotContain("GLOBAL_PRECEDING", prompt, StringComparison.Ordinal);

        Assert.Contains("[FOLLOWING_CONTEXT]", prompt);
        Assert.Contains("LOCAL_AFTER", prompt);
        Assert.DoesNotContain("GLOBAL_FOLLOWING", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void LastChunk_UsesLocalPrecedingAndGlobalFollowing()
    {
        var factory = new PromptFactory();
        var context = CreateBaseContext(
            preceding: "GLOBAL_PRECEDING",
            following: "GLOBAL_FOLLOWING");

        var prompt = factory.BuildLineEditChunkPrompt(
            language: "en",
            context: context,
            localOverlapBefore: "LOCAL_BEFORE",
            localOverlapAfter: "LOCAL_AFTER",
            isFirstChunk: false,
            isLastChunk: true);

        Assert.Contains("[PRECEDING_CONTEXT]", prompt);
        Assert.Contains("LOCAL_BEFORE", prompt);
        Assert.DoesNotContain("GLOBAL_PRECEDING", prompt, StringComparison.Ordinal);

        Assert.Contains("[FOLLOWING_CONTEXT]", prompt);
        Assert.Contains("GLOBAL_FOLLOWING", prompt);
        Assert.DoesNotContain("LOCAL_AFTER", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsPrecedingAndFollowingSections_WhenBothGlobalAndLocalAreEmpty()
    {
        var factory = new PromptFactory();
        var context = CreateBaseContext(
            preceding: null,
            following: null);

        var prompt = factory.BuildLineEditChunkPrompt(
            language: "en",
            context: context,
            localOverlapBefore: null,
            localOverlapAfter: "   ",
            isFirstChunk: true,
            isLastChunk: true);

        Assert.DoesNotContain("[/PRECEDING_CONTEXT]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[/FOLLOWING_CONTEXT]", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludesStyleProfileSection_WhenStyleProfileIsPresent()
    {
        var factory = new PromptFactory();
        var style = new StyleProfileData
        {
            DominantTone = "lyrical",
            Pov = "third-limited",
            TensePattern = "past",
            VocabularyLevel = "literary",
            DialogueStyle = "natural",
            AverageSentenceLength = 20,
            FormalityScore = 0.7
        };
        var context = CreateBaseContext(style: style);

        var prompt = factory.BuildLineEditChunkPrompt(
            language: "en",
            context: context,
            localOverlapBefore: null,
            localOverlapAfter: null,
            isFirstChunk: true,
            isLastChunk: true);

        Assert.Contains("[STYLE_PROFILE]", prompt);
        Assert.Contains("[/STYLE_PROFILE]", prompt);
        Assert.Contains("The author's dominant tone is lyrical.", prompt);
        Assert.Contains("The narrative uses third-limited POV.", prompt);
    }

    [Fact]
    public void AppendsTextToEditGuidance_ForEnglishAndHebrew()
    {
        var factory = new PromptFactory();
        var context = CreateBaseContext();

        var enPrompt = factory.BuildLineEditChunkPrompt(
            language: "en",
            context: context,
            localOverlapBefore: null,
            localOverlapAfter: null,
            isFirstChunk: true,
            isLastChunk: true);

        Assert.Contains("[TEXT_TO_EDIT]...[/TEXT_TO_EDIT]", enPrompt);
        Assert.Contains("Only suggest edits for text inside those markers", enPrompt);

        var hePrompt = factory.BuildLineEditChunkPrompt(
            language: "he",
            context: context,
            localOverlapBefore: null,
            localOverlapAfter: null,
            isFirstChunk: true,
            isLastChunk: true);

        Assert.Contains("[TEXT_TO_EDIT]...[/TEXT_TO_EDIT]", hePrompt);
        Assert.Contains("הטקסט לעריכה מסומן", hePrompt);
    }
}

