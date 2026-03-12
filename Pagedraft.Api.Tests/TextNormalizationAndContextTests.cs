using System;
using System.Collections.Generic;
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

