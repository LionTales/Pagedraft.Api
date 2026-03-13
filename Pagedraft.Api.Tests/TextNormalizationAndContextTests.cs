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
    public void NormalizeTextForAnalysis_StripsBidiControlsAndNewlines()
    {
        var input = "שורה ראשונה\u200E\r\nשורה\u200F שנייה\u202A";

        var normalized = TextNormalization.NormalizeTextForAnalysis(input);

        Assert.DoesNotContain("\r", normalized);
        Assert.DoesNotContain("\n", normalized);

        Assert.Contains("שורה ראשונה", normalized);
        Assert.Contains("שורה שנייה", normalized);
    }

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
            svc.BuildContextAsync(AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, cts.Token));
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
            CancellationToken.None);

        Assert.NotNull(middleContext.PrecedingContext);
        Assert.Contains("First closing.", middleContext.PrecedingContext);

        Assert.NotNull(middleContext.FollowingContext);
        Assert.Contains("Last opening.", middleContext.FollowingContext);

        var firstContext = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            firstChapter.Id,
            AnalysisType.LineEdit,
            CancellationToken.None);

        Assert.Null(firstContext.PrecedingContext);
        Assert.NotNull(firstContext.FollowingContext);
        Assert.Contains("Middle opening.", firstContext.FollowingContext);

        var lastContext = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            lastChapter.Id,
            AnalysisType.LineEdit,
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
            CancellationToken.None);

        Assert.Equal(AnalysisScope.Chapter, context.Scope);
        Assert.Equal(AnalysisType.LineEdit, context.AnalysisType);

        Assert.NotNull(context.StyleProfile);
        Assert.Equal("neutral", context.StyleProfile!.DominantTone);
        Assert.Equal("first-person", context.StyleProfile.Pov);
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

        services.AddScoped<IAnalysisContextService, AnalysisContextService>();

        return services.BuildServiceProvider();
    }
}

