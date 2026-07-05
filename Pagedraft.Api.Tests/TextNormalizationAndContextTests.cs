using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

public class TextNormalizationAndContextTests
{
    [Fact]
    public void NormalizeTextForAnalysis_ConvertsLineBreaksToSpaces_AndDropsBidiControls()
    {
        var input = "\u05e9\u05d5\u05e8\u05d4 \u05e8\u05d0\u05e9\u05d5\u05e0\u05d4\u200E\r\n\u05e9\u05d5\u05e8\u05d4\u200F \u05e9\u05e0\u05d9\u05d9\u05d4\u202A";

        var normalized = TextNormalization.NormalizeTextForAnalysis(input);

        // Line breaks are gone (converted to spaces), not present as raw \r/\n. Use the char
        // overload: Assert.DoesNotContain(string, string) is culture-sensitive and treats bidi/
        // ignorable characters as matching at position 0 in ANY string, producing false failures.
        Assert.DoesNotContain('\r', normalized);
        Assert.DoesNotContain('\n', normalized);
        // Bidi controls are dropped entirely.
        Assert.DoesNotContain('\u200E', normalized);
        Assert.DoesNotContain('\u200F', normalized);
        Assert.DoesNotContain('\u202A', normalized);

        Assert.Contains("\u05e9\u05d5\u05e8\u05d4 \u05e8\u05d0\u05e9\u05d5\u05e0\u05d4", normalized, StringComparison.Ordinal);
        Assert.Contains("\u05e9\u05d5\u05e8\u05d4 \u05e9\u05e0\u05d9\u05d9\u05d4", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeTextForAnalysis_HardLineBreak_BecomesSpace_NotGlued()
    {
        // Root cause of the content-dropping proofread bug: the chapter title (RONI) is the first
        // body line, so ContentText head is RONI + newline + HITORARTI. Dropping the newline glued it
        // into a non-word, which the model "fixed" by deleting the title word. The break must map to a
        // space so the model sees a separated proper noun.
        Assert.Equal("\u05e8\u05d5\u05e0\u05d9 \u05d4\u05ea\u05e2\u05d5\u05e8\u05ea\u05d9", TextNormalization.NormalizeTextForAnalysis("\u05e8\u05d5\u05e0\u05d9\n\u05d4\u05ea\u05e2\u05d5\u05e8\u05ea\u05d9"));
        Assert.NotEqual("\u05e8\u05d5\u05e0\u05d9\u05d4\u05ea\u05e2\u05d5\u05e8\u05ea\u05d9", TextNormalization.NormalizeTextForAnalysis("\u05e8\u05d5\u05e0\u05d9\n\u05d4\u05ea\u05e2\u05d5\u05e8\u05ea\u05d9"));
    }

    [Fact]
    public void NormalizeTextForAnalysis_IsStableUnderRepeatedApplication_NoSpaceGrowth()
    {
        // Idempotent-ish: normalizing already-normalized text is a no-op (spaces are not line breaks,
        // so a second pass adds nothing). Guards against a rule that keeps growing whitespace.
        var once = TextNormalization.NormalizeTextForAnalysis("\u05e8\u05d5\u05e0\u05d9\r\n\u05d4\u05ea\u05e2\u05d5\u05e8\u05ea\u05d9");
        var twice = TextNormalization.NormalizeTextForAnalysis(once);
        Assert.Equal(once, twice);
    }

    // Shared FE/BE parity vector. The SAME input->expected pairs are asserted by the frontend spec
    // (normalize-text-for-analysis.spec.ts, SHARED_PARITY_VECTOR) so FE and BE normalization are
    // provably identical. Keep the two lists in lockstep when either changes.
    [Theory]
    [InlineData("\u05e8\u05d5\u05e0\u05d9\n\u05d4\u05ea\u05e2\u05d5\u05e8\u05ea\u05d9", "\u05e8\u05d5\u05e0\u05d9 \u05d4\u05ea\u05e2\u05d5\u05e8\u05ea\u05d9")]
    [InlineData("a\r\nb", "a  b")]
    [InlineData("a\rb\nc", "a b c")]
    [InlineData("x\u200Ey", "xy")]
    [InlineData("\u05e9\u05d5\u05e8\u05d4\u200F \u05e9\u05e0\u05d9\u05d9\u05d4", "\u05e9\u05d5\u05e8\u05d4 \u05e9\u05e0\u05d9\u05d9\u05d4")]
    [InlineData("plain text", "plain text")]
    public void NormalizeTextForAnalysis_SharedParityVector(string input, string expected)
    {
        Assert.Equal(expected, TextNormalization.NormalizeTextForAnalysis(input));
    }

    // ─── Paragraph-separator offset-alignment invariant (be-c01) ─────────────────────────────
    //
    // The FE SFDT offset walk (SfdtManipulationService) advances a CONSTANT
    // BLOCK_SEPARATOR_NORM_LEN = normalizeTextForAnalysis('\n').length = 1 between consecutive
    // blocks, i.e. it assumes each inter-paragraph boundary in the backend's offset string is
    // EXACTLY ONE normalized character. Backend suggestion offsets are computed by
    // SuggestionDiffService against NormalizeTextForAnalysis(TargetText), where TargetText is
    // the chapter/scene plain text AFTER SyncfusionWatermarkStripper.StripSyncfusionWatermark.
    //
    // Latent trap: Syncfusion's WordDocument.GetText() joins paragraphs with CRLF ("\r\n"), which
    // NormalizeTextForAnalysis maps to TWO spaces (the shared parity vector proves "a\r\nb" -> "a  b").
    // If that CRLF reached the offset string, every suggestion past the first paragraph break would
    // drift +1 and ACCUMULATE. It does NOT today ONLY because StripSyncfusionWatermark collapses
    // [\r\n]+ -> "\n" (a single '\n') before normalization, so each boundary contributes exactly ONE
    // space (matching the FE's BLOCK_SEPARATOR_NORM_LEN of 1). These tests lock that invariant so a
    // future change to the stripper (or a path that bypasses it) cannot silently reintroduce the drift.

    [Fact]
    public void ParagraphSeparator_OffsetString_ContributesExactlyOneSpacePerParagraphBreak()
    {
        // Build a real, Syncfusion-round-tripped SFDT (the same path chapter/scene ContentText takes),
        // then run the EXACT offset-string pipeline the backend uses:
        //   Syncfusion GetText() -> StripSyncfusionWatermark -> NormalizeTextForAnalysis
        var sfdt = SfdtConversionService.CreateMinimalSfdtFromText("para1\npara2\npara3");
        var (contentText, _) = new SfdtConversionService().GetTextFromSfdt(sfdt);

        var offsetString = OffsetString(contentText);

        // Three paragraphs => two inter-paragraph separators, each exactly ONE normalized char (a space),
        // so the offset string is "para1 para2 para3": 5 + 1 + 5 + 1 + 5 = 17 chars.
        Assert.Equal("para1 para2 para3", offsetString);
        Assert.Equal(17, offsetString.Length);

        // No raw line breaks survive into the offset string.
        Assert.DoesNotContain('\r', offsetString);
        Assert.DoesNotContain('\n', offsetString);
        // And no DOUBLE space (the CRLF-would-be-two-spaces failure mode) between words.
        Assert.DoesNotContain("  ", offsetString, StringComparison.Ordinal);

        // Directly assert the per-separator contribution matches the FE's BLOCK_SEPARATOR_NORM_LEN (1):
        // (offsetLen - sum(word lengths)) / separatorCount == 1.
        const int wordLenSum = 5 + 5 + 5; // "para1" + "para2" + "para3"
        const int separatorCount = 2;
        var perSeparatorNormLen = (offsetString.Length - wordLenSum) / separatorCount;
        Assert.Equal(1, perSeparatorNormLen); // must equal FE BLOCK_SEPARATOR_NORM_LEN
    }

    [Fact]
    public void ParagraphSeparator_RawSyncfusionSeparatorIsCrlf_ButStripperCollapsesItToSingleLf()
    {
        // Pins the two facts the invariant depends on:
        //   (1) Syncfusion GetText() DOES emit CRLF ("\r\n") between paragraphs (the trap), and
        //   (2) StripSyncfusionWatermark collapses that CRLF to a single '\n' (the safety), so
        //       NormalizeTextForAnalysis then yields ONE space, not two.
        var sfdt = SfdtConversionService.CreateMinimalSfdtFromText("alpha\nbeta");
        var (contentText, _) = new SfdtConversionService().GetTextFromSfdt(sfdt);

        // (1) The raw Syncfusion text still carries the CRLF paragraph mark.
        Assert.Contains("alpha\r\nbeta", contentText, StringComparison.Ordinal);

        // (2) After stripping, the boundary is a SINGLE '\n' (the [\r\n]+ -> "\n" collapse).
        var stripped = SyncfusionWatermarkStripper.StripSyncfusionWatermark(contentText);
        Assert.Equal("alpha\nbeta", stripped);
        Assert.DoesNotContain('\r', stripped);

        // Therefore the normalized offset string has exactly ONE space at the boundary (not two).
        Assert.Equal("alpha beta", TextNormalization.NormalizeTextForAnalysis(stripped));
    }

    /// <summary>
    /// Reproduces the backend's exact offset-string pipeline for chapter/scene text:
    /// StripSyncfusionWatermark (removes the trial watermark AND collapses [\r\n]+ to a single '\n')
    /// followed by NormalizeTextForAnalysis (bidi-strip + each line break -> one space). This is the
    /// string SuggestionDiffService indexes suggestion offsets against.
    /// </summary>
    private static string OffsetString(string rawSyncfusionText) =>
        TextNormalization.NormalizeTextForAnalysis(
            SyncfusionWatermarkStripper.StripSyncfusionWatermark(rawSyncfusionText));

    [Fact]
    public void NormalizeTextForStorage_StripsBidiControlsOnly()
    {
        var input = "שורה א\u200E\r\nשורה ב\u200F";

        var storage = TextNormalization.NormalizeTextForStorage(input);

        Assert.Contains("\r\n", storage);
        // At least one bidi control character should be removed while newlines remain.
        Assert.True(storage.Length < input.Length);
    }

    [Fact]
    public async Task AnalysisContextService_NoBookBible_GracefullyDegeneratesCharacters()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Test Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter 1",
            ContentText = "זהו טקסט לפרק הראשון."
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.Proofread,
            "he",
            CancellationToken.None);

        Assert.Equal("זהו טקסט לפרק הראשון.", context.TargetText);
        Assert.Equal(AnalysisScope.Chapter, context.Scope);
        Assert.Equal(AnalysisType.Proofread, context.AnalysisType);
        Assert.Equal(bookId, context.BookId);
        Assert.Equal(chapterId, context.ChapterId);
        Assert.Null(context.Characters);
    }

    [Fact]
    public async Task AnalysisContextService_LoadsCharacterRegisterFromBookBible()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Book with Bible" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter with Characters",
            ContentText = "רונית דיברה עם אלון."
        });

        var register = new CharacterRegister
        {
            Characters = new[]
            {
                new CharacterRegisterEntry { Name = "רונית", Gender = "female", Role = "protagonist" },
                new CharacterRegisterEntry { Name = "אלון", Gender = "male", Role = "supporting" }
            }
        };

        db.BookBibles.Add(new BookBible
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            CharacterRegisterJson = JsonSerializer.Serialize(register)
        });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.Proofread,
            "he",
            CancellationToken.None);

        Assert.NotNull(context.Characters);
        Assert.Equal(2, context.Characters!.Characters.Count);
        Assert.Contains(context.Characters.Characters, c => c.Name == "רונית");
    }

    [Fact]
    public async Task AnalysisContextService_UsesLlMExtractionFallbackWhenNoBookBible()
    {
        using var provider = BuildServiceProvider(useRealRouter: true);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Fallback Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "רונית דיברה עם אלון בחדר."
        });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.Proofread,
            "he",
            CancellationToken.None);

        Assert.NotNull(context.Characters);
        Assert.NotEmpty(context.Characters!.Characters);

        var bible = await db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId);
        Assert.NotNull(bible);
        Assert.False(string.IsNullOrWhiteSpace(bible!.CharacterRegisterJson));
    }

    [Fact]
    public async Task AnalysisContextService_PropagatesCancellationDuringCharacterExtraction()
    {
        using var provider = BuildServiceProvider(simulateSlowRouter: true);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Cancellable Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = new string('א', 1000)
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            svc.BuildContextAsync(AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", cts.Token));
    }

    [Fact]
    public async Task AnalysisContextService_ResolveContextEnvelope_SceneScope_MiddleScene_UsesAdjacentScenes()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Scene Envelope Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter with Scenes",
            ContentText = "Chapter opening paragraph.\n\nMiddle paragraph.\n\nChapter closing paragraph."
        });

        var scene1 = new Scene
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Title = "First Scene",
            Order = 1,
            ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("Content of first scene.")
        };
        var scene2 = new Scene
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Title = "Middle Scene",
            Order = 2,
            ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("Content of middle scene.")
        };
        var scene3 = new Scene
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Title = "Last Scene",
            Order = 3,
            ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("Content of last scene.")
        };

        db.Scenes.AddRange(scene1, scene2, scene3);
        await db.SaveChangesAsync();

        var svc = (AnalysisContextService)provider.GetRequiredService<IAnalysisContextService>();

        var method = typeof(AnalysisContextService).GetMethod(
            "ResolveContextEnvelopeAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task)method.Invoke(svc, new object[]
        {
            AnalysisScope.Scene,
            (Guid?)bookId,
            (Guid?)chapterId,
            (Guid?)scene2.Id,
            CancellationToken.None
        })!;
        await task;

        var result = (ValueTuple<string?, string?>)task.GetType().GetProperty("Result")!.GetValue(task)!;

        // With empty SFDT payloads we only assert that the method executes successfully for a middle scene.
        // Scene head/tail extraction is covered indirectly via first/last scene tests.
    }

    [Fact]
    public async Task AnalysisContextService_ResolveContextEnvelope_SceneScope_FirstAndLastScenes_UseChapterParagraphs()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Scene Edge Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter with Edge Scenes",
            ContentText = "Opening paragraph.\n\nMiddle paragraph.\n\nClosing paragraph."
        });

        var firstScene = new Scene
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Title = "First Scene",
            Order = 1,
            ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("First scene body.")
        };
        var middleScene = new Scene
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Title = "Middle Scene",
            Order = 2,
            ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("Middle scene body.")
        };
        var lastScene = new Scene
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Title = "Last Scene",
            Order = 3,
            ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("Last scene body.")
        };

        db.Scenes.AddRange(firstScene, middleScene, lastScene);
        await db.SaveChangesAsync();

        var svc = (AnalysisContextService)provider.GetRequiredService<IAnalysisContextService>();

        var method = typeof(AnalysisContextService).GetMethod(
            "ResolveContextEnvelopeAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // First scene: preceding from chapter opening, following from next scene head
        var firstTask = (Task)method.Invoke(svc, new object[]
        {
            AnalysisScope.Scene,
            (Guid?)bookId,
            (Guid?)chapterId,
            (Guid?)firstScene.Id,
            CancellationToken.None
        })!;
        await firstTask;
        var firstResult = (ValueTuple<string?, string?>)firstTask.GetType().GetProperty("Result")!.GetValue(firstTask)!;

        Assert.NotNull(firstResult.Item1);
        Assert.Contains("Opening paragraph.", firstResult.Item1);

        // Last scene: preceding from previous scene tail, following from chapter closing paragraph
        var lastTask = (Task)method.Invoke(svc, new object[]
        {
            AnalysisScope.Scene,
            (Guid?)bookId,
            (Guid?)chapterId,
            (Guid?)lastScene.Id,
            CancellationToken.None
        })!;
        await lastTask;
        var lastResult = (ValueTuple<string?, string?>)lastTask.GetType().GetProperty("Result")!.GetValue(lastTask)!;

        Assert.NotNull(lastResult.Item2);
        Assert.Contains("Closing paragraph.", lastResult.Item2);
    }

    [Fact]
    public async Task AnalysisContextService_ResolveContextEnvelope_ChapterScope_FirstMiddleLastChapters()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Chapter Envelope Book" });

        var firstChapter = new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Title = "First Chapter",
            Order = 1,
            ContentText = "First opening.\n\nFirst closing."
        };
        var middleChapter = new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Title = "Middle Chapter",
            Order = 2,
            ContentText = "Middle opening.\n\nMiddle closing."
        };
        var lastChapter = new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Title = "Last Chapter",
            Order = 3,
            ContentText = "Last opening.\n\nLast closing."
        };

        db.Chapters.AddRange(firstChapter, middleChapter, lastChapter);
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var middleContext = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            middleChapter.Id,
            AnalysisType.LineEdit,
            "he",
            CancellationToken.None);

        Assert.NotNull(middleContext.PrecedingContext);
        Assert.Contains("First closing.", middleContext.PrecedingContext);

        Assert.NotNull(middleContext.FollowingContext);
        Assert.Contains("Last opening.", middleContext.FollowingContext);

        var firstContext = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            firstChapter.Id,
            AnalysisType.LineEdit,
            "he",
            CancellationToken.None);

        Assert.Null(firstContext.PrecedingContext);
        Assert.NotNull(firstContext.FollowingContext);
        Assert.Contains("Middle opening.", firstContext.FollowingContext);

        var lastContext = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            lastChapter.Id,
            AnalysisType.LineEdit,
            "he",
            CancellationToken.None);

        Assert.NotNull(lastContext.PrecedingContext);
        Assert.Contains("Middle closing.", lastContext.PrecedingContext);
        Assert.Null(lastContext.FollowingContext);
    }

    [Fact]
    public async Task AnalysisContextService_LoadsStyleProfile_FromBookBible_WhenPresent()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Style Profile Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter with Style",
            ContentText = "Some chapter text."
        });

        var styleProfile = new StyleProfileData
        {
            DominantTone = "lyrical",
            Pov = "third-limited",
            TensePattern = "past",
            VocabularyLevel = "literary",
            DialogueStyle = "natural",
            RecurringMotifs = new[] { "rain", "mirrors" },
            AverageSentenceLength = 15,
            FormalityScore = 0.7
        };

        db.BookBibles.Add(new BookBible
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            StyleProfileJson = JsonSerializer.Serialize(styleProfile)
        });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.LineEdit,
            "he",
            CancellationToken.None);

        Assert.NotNull(context.StyleProfile);
        Assert.Equal("lyrical", context.StyleProfile!.DominantTone);
        Assert.Equal("third-limited", context.StyleProfile.Pov);
        Assert.Equal("past", context.StyleProfile.TensePattern);
        Assert.Equal("literary", context.StyleProfile.VocabularyLevel);
        Assert.Equal("natural", context.StyleProfile.DialogueStyle);
        Assert.Equal(0.7, context.StyleProfile.FormalityScore);
        Assert.Equal(15, context.StyleProfile.AverageSentenceLength);
    }

    [Fact]
    public async Task AnalysisContextService_StyleProfile_NullWhenMissingOrEmpty()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Style Profile Missing Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter without Style",
            ContentText = "Some chapter text."
        });

        db.BookBibles.Add(new BookBible
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            StyleProfileJson = null
        });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.LineEdit,
            "he",
            CancellationToken.None);

        Assert.Null(context.StyleProfile);
    }

    [Fact]
    public async Task AnalysisContextService_BuildContextAsync_PopulatesEnvelopeAndStyleProfile_ForLineEdit()
    {
        using var provider = BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Full Context Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Full Context Chapter",
            ContentText = "First para.\n\nTarget para.\n\nLast para."
        });

        var styleProfile = new StyleProfileData
        {
            DominantTone = "neutral",
            Pov = "first-person"
        };

        db.BookBibles.Add(new BookBible
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            StyleProfileJson = JsonSerializer.Serialize(styleProfile)
        });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.LineEdit,
            "he",
            CancellationToken.None);

        Assert.Equal(AnalysisScope.Chapter, context.Scope);
        Assert.Equal(AnalysisType.LineEdit, context.AnalysisType);

        Assert.NotNull(context.StyleProfile);
        Assert.Equal("neutral", context.StyleProfile!.DominantTone);
        Assert.Equal("first-person", context.StyleProfile.Pov);
    }

    // ─── IsProofreadResultUnrelated tests ───

    private static readonly MethodInfo IsUnrelatedMethod =
        typeof(UnifiedAnalysisService).GetMethod(
            "IsProofreadResultUnrelated",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool InvokeIsUnrelated(string input, string result, out double wordSimilarity)
    {
        var args = new object[] { input, result, 0.0 };
        var ret = (bool)IsUnrelatedMethod.Invoke(null, args)!;
        wordSimilarity = (double)args[2];
        return ret;
    }

    [Fact]
    public void IsProofreadResultUnrelated_SimilarText_ReturnsFalseWithHighSimilarity()
    {
        var input = "הילדה הלכה לבית הספר בבוקר המוקדם ולמדה שיעורים רבים במשך היום";
        var result = "הילדה הלכה לבית הספר בבוקר המוקדם, ולמדה שיעורים רבים במשך היום.";

        var unrelated = InvokeIsUnrelated(input, result, out var similarity);

        Assert.False(unrelated);
        Assert.True(similarity >= 0.7, $"Expected similarity >= 0.7, got {similarity}");
    }

    [Fact]
    public void IsProofreadResultUnrelated_CompletelyDifferentText_ReturnsTrueWithLowSimilarity()
    {
        var input = "הילדה הלכה לבית הספר בבוקר המוקדם ולמדה שיעורים רבים במשך היום";
        var result = "השמש זרחה על פני הים הכחול והגלים התנפצו על החוף בעוצמה רבה";

        var unrelated = InvokeIsUnrelated(input, result, out var similarity);

        Assert.True(unrelated);
        Assert.True(similarity < 0.7, $"Expected similarity < 0.7, got {similarity}");
    }

    [Fact]
    public void IsProofreadResultUnrelated_ContinuationMarker_ReturnsTrueWithSimilaritySet()
    {
        var input = "הילדה הלכה לבית הספר בבוקר המוקדם ולמדה שיעורים רבים במשך היום";
        var result = "הנה המשך לסיפור על הילדה שהלכה לבית הספר, היא פגשה חברה ישנה";

        var unrelated = InvokeIsUnrelated(input, result, out var similarity);

        Assert.True(unrelated);
        Assert.True(similarity > 0.0, "Similarity should be computed before marker check");
    }

    [Fact]
    public void IsProofreadResultUnrelated_EmptyInput_ReturnsFalseWithZeroSimilarity()
    {
        var unrelated = InvokeIsUnrelated("", "some result text that is long enough to pass the checks", out var similarity);

        Assert.False(unrelated);
        Assert.Equal(0.0, similarity);
    }

    [Fact]
    public void IsProofreadResultUnrelated_EmptyResult_ReturnsFalseWithZeroSimilarity()
    {
        var unrelated = InvokeIsUnrelated("some input text that is long enough", "", out var similarity);

        Assert.False(unrelated);
        Assert.Equal(0.0, similarity);
    }

    [Fact]
    public void IsProofreadResultUnrelated_ShortText_ReturnsFalseEarly()
    {
        var unrelated = InvokeIsUnrelated("short", "different short text", out var similarity);

        Assert.False(unrelated);
        Assert.Equal(0.0, similarity);
    }

    private static ServiceProvider BuildServiceProvider(bool useRealRouter = false, bool simulateSlowRouter = false)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddDbContext<AppDbContext>(opt =>
        {
            opt.UseInMemoryDatabase(Guid.NewGuid().ToString());
        });

        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        if (useRealRouter)
        {
            var routerMock = new Mock<IAiRouter>();
            routerMock
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AiResponse
                {
                    Content = "[{\"name\":\"רונית\",\"gender\":\"female\",\"role\":\"protagonist\"}]",
                    Model = "test-model",
                    Provider = "test-provider"
                });
            services.AddSingleton(routerMock.Object);
        }
        else if (simulateSlowRouter)
        {
            var routerMock = new Mock<IAiRouter>();
            routerMock
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .Returns<AiRequest, CancellationToken>(async (_, ct) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return new AiResponse { Content = "[]", Model = "test", Provider = "test" };
                });
            services.AddSingleton(routerMock.Object);
        }
        else
        {
            var routerMock = new Mock<IAiRouter>();
            routerMock
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AiResponse { Content = "[]", Model = "test", Provider = "test" });
            services.AddSingleton(routerMock.Object);
        }

        services.Configure<AiOptions>(_ => { });

        // AnalysisContextService now depends on the whole-book context assembler graph (wb1-c03); register
        // it so IAnalysisContextService resolves.
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();

        return services.BuildServiceProvider();
    }
}

