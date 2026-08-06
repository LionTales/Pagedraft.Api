using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE COMPLETENESS ORACLE for "which AnalysisTypes use the character register" (c04).
///
/// <para>What it replaces: three hand-maintained copies of that type set (the LOAD gate in
/// <c>AnalysisContextService.BuildContextAsync</c>, the <c>ContextField.Characters</c> rows of
/// <c>PromptFactory.GetRelevantFields</c>, and <c>AnalysisController.ReadsCharacterRegister</c>), kept in
/// step by a doc-comment saying "keep in lockstep" and by a <c>[Theory]</c> whose EXPECTED and ACTUAL
/// sides were both hand-authored. That theory named 7 of the enum's 12 members, so adding
/// <c>ContextField.Characters</c> to a new type moved nothing red: the new type was simply absent from
/// the table, and every result of that type then reported never-stale forever.</para>
///
/// <para>Why THIS one cannot go blind the same way: it enumerates
/// <c>Enum.GetValues&lt;AnalysisType&gt;()</c>, so a member added to the enum is covered the moment it
/// exists, and it probes the three OBSERVABLES rather than the predicates - a real assembled prompt
/// string, a real <c>BuildContextAsync</c> run against a real seeded register, and a real
/// <c>ToDto</c> flag. A copy re-inlined at any of the three sites therefore shows up as a disagreement
/// even though the shared predicate still exists. It READS the domain instead of restating it, which is
/// the whole point: a revert-verify cannot catch this defect class, because there is no bug to restore.</para>
///
/// <para>NON-VACUITY (this feature has already shipped two tests that passed against the bug): the
/// register-reading set is asserted to be non-empty AND a proper subset, every enum member is asserted
/// to have been visited, and each composed prompt is asserted non-empty - so a probe that silently
/// answered "false" for everything, or a loop that visited nothing, fails instead of greening.</para>
/// </summary>
public class CharacterRegisterReadingTypeSetTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EveryAnalysisType_LoadGate_RenderGate_AndStaleFlag_Agree()
    {
        var factory = new PromptFactory();

        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();
        var chapterId = await SeedBookWithRegisterAsync(db);
        var contextService = provider.GetRequiredService<IAnalysisContextService>();

        var allTypes = Enum.GetValues<AnalysisType>();
        var visited = new List<AnalysisType>();
        var readsRegister = new List<AnalysisType>();
        var disagreements = new List<string>();

        foreach (var type in allTypes)
        {
            visited.Add(type);

            // OBSERVABLE 1 - RENDER: does the prompt this type actually sends carry the section? Read off
            // the composed string, not off GetRelevantFields, so the probe survives a change in how the
            // decision is expressed.
            //
            // Probe the CLOSING delimiter, not the opening one. `[CHARACTER_REGISTER]` also appears
            // LITERALLY inside the Proofread instruction text in both languages ("If [CHARACTER_REGISTER]
            // is present - use it to verify ...", PromptFactory :541/:561), so an opening-tag probe reports
            // Proofread as rendering the section even when no section was emitted at all - which would pin
            // `readsRegister.Count > 0` true unconditionally and quietly disarm the non-vacuity floor
            // below. `[/CHARACTER_REGISTER]` is emitted ONLY by AppendSection, so it is true exactly when a
            // section was written.
            var prompt = factory.GetAnalysisPrompt(type, "he", new AnalysisContext
            {
                TargetText = "רונית דיברה עם אלון.",
                Characters = SeededRegister,
                Scope = AnalysisScope.Chapter,
                AnalysisType = type
            });
            Assert.False(
                string.IsNullOrWhiteSpace(prompt),
                $"{type}: the composed prompt is empty, so the render probe below would be vacuous.");
            var rendered = prompt.Contains("[/CHARACTER_REGISTER]", StringComparison.Ordinal);

            // OBSERVABLE 2 - LOAD: does the real context build hand this type a register? The book is
            // seeded with a NON-EMPTY register, so the load gate returns it at the top of
            // LoadCharacterRegisterAsync and no extraction pre-pass or write is involved.
            var built = await contextService.BuildContextAsync(
                AnalysisScope.Chapter, chapterId, type, "he", CancellationToken.None);
            var loaded = built.Characters is { Characters.Count: > 0 };

            // OBSERVABLE 3 - DTO: is a result of this type allowed to report stale against the stamp?
            var flagged = AnalysisController
                .ToDto(ResultOf(type, Stamp.AddDays(-1)), Stamp)
                .CharacterRegisterStale;

            if (rendered)
                readsRegister.Add(type);

            if (loaded != rendered || flagged != rendered)
                disagreements.Add($"{type}: rendered={rendered}, loaded={loaded}, staleFlag={flagged}");
        }

        // The domain was actually walked. If AnalysisType grows a member, it is here by construction.
        Assert.Equal(allTypes.Length, visited.Count);

        // NON-VACUITY: some type must read the register and some must not. Without these, a probe that
        // answered "false" everywhere (a renamed section marker, a context build that silently degraded)
        // would report perfect agreement.
        Assert.True(
            readsRegister.Count > 0,
            "No AnalysisType rendered [CHARACTER_REGISTER]. The render probe is broken, so the agreement " +
            "below is vacuous.");
        Assert.True(
            readsRegister.Count < allTypes.Length,
            "EVERY AnalysisType rendered [CHARACTER_REGISTER]. The render probe is broken, so the " +
            "agreement below is vacuous.");

        Assert.True(
            disagreements.Count == 0,
            "The three gates on 'does this analysis type use the character register' have diverged. They " +
            "are supposed to be one predicate (PromptFactory.RendersCharacterRegister); a disagreement " +
            "means one site re-inlined its own list. Offenders: " + string.Join(" | ", disagreements));
    }

    // The readable smoke check of the CURRENT membership lives with the other DTO-flag tests, at
    // CharacterRegisterEndpointTests.AnalysisDto_StaleFlag_IsGatedToTypesThatActuallyReadTheRegister.
    // It is deliberately NOT duplicated here: a second hand-authored table would be a third copy of the
    // very list this class exists to stop anyone from maintaining by hand.

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static readonly CharacterRegister SeededRegister = new()
    {
        Characters = new[] { new CharacterRegisterEntry { Name = "רונית", Gender = "female" } }
    };

    private static AnalysisResult ResultOf(AnalysisType type, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = type.ToString(),
        AnalysisType = type,
        ResultText = "x",
        CreatedAt = createdAt,
        Suggestions = new List<AnalysisSuggestion>()
    };

    private static async Task<Guid> SeedBookWithRegisterAsync(AppDbContext db)
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Register Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "פרק",
            ContentText = "רונית דיברה עם אלון."
        });
        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = CharacterRegisterService.Serialize(SeededRegister)
        });
        await db.SaveChangesAsync();
        return chapterId;
    }

    /// <summary>
    /// Same DI shape as <c>CharacterRegisterProvenanceTests.BuildProvider</c>: in-memory database, mocked
    /// router. The router returns an empty extraction so that IF a gate ever fell through to the pre-pass
    /// it would produce nothing - but with a non-empty register seeded, no type should reach it at all.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(databaseName));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = "[]", Model = "test", Provider = "test" });
        services.AddSingleton(router.Object);

        services.Configure<AiOptions>(_ => { });
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<CharacterRegisterService>();

        return services.BuildServiceProvider();
    }
}
