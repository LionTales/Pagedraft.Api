using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The reads c14 moved off <c>Include(a =&gt; a.Suggestions)</c> answer EXACTLY what the include answered (plan
/// whole-book-proofread-and-annotated-docx-2026-10-11, c14). The reference below is the shipped code verbatim
/// (include, then <see cref="AnalysisController.ToDto"/>), run on its own context and serialized the way ASP.NET
/// serializes the response; the new reads go through the real controller actions. The seed holds the cases a
/// two-query load could get wrong: two results whose suggestions must not cross, suggestions stored out of
/// <c>OrderIndex</c> order, a result with none, a <c>CreatedAt</c> tie (ordered by id, as SQL Server's include
/// did: <see cref="AnalysisSuggestionLoadQueryShapeTests.The_include_it_replaced_ordered_results_by_id"/>), an
/// archived row, a scene row and another chapter. The query SHAPE is pinned in
/// <see cref="AnalysisSuggestionLoadQueryShapeTests"/>; this pins the answer.
/// </summary>
public class AnalysisSuggestionLoadEqualityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Seed
    {
        public string DbName = Guid.NewGuid().ToString();
        public Guid BookId, ChapterId, OtherChapterId, SceneId;
        public Guid ProofreadId, LineEditId, LiteraryId, SceneResultId, ArchivedId, OtherChapterResultId;
        public Guid JobId = Guid.NewGuid();

        public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(DbName).Options);
    }

    private static Seed BuildSeed()
    {
        var seed = new Seed();
        using var db = seed.NewContext();

        var book = new Book { Title = "ספר", Language = "he" };
        var chapter = new Chapter { BookId = book.Id, Book = book, Title = "פרק 1", Order = 0, ContentText = "טקסט" };
        var other = new Chapter { BookId = book.Id, Book = book, Title = "פרק 2", Order = 1, ContentText = "טקסט" };
        var scene = new Scene { ChapterId = chapter.Id, Title = "סצנה", Order = 0 };
        db.Books.Add(book);
        db.Chapters.AddRange(chapter, other);
        db.Scenes.Add(scene);
        seed.BookId = book.Id;
        seed.ChapterId = chapter.Id;
        seed.OtherChapterId = other.Id;
        seed.SceneId = scene.Id;

        var t0 = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        AnalysisResult Result(AnalysisType type, Guid chapterId, DateTimeOffset createdAt, AnalysisStatus status = AnalysisStatus.Active, Guid? sceneId = null) => new()
        {
            ChapterId = chapterId,
            BookId = book.Id,
            SceneId = sceneId,
            Scope = sceneId == null ? AnalysisScope.Chapter : AnalysisScope.Scene,
            AnalysisType = type,
            Type = type.ToString(),
            Status = status,
            CreatedAt = createdAt,
            ResultText = "טקסט מתוקן של הפרק " + type,
            SourceTextSnapshot = "טקסט המקור של הפרק " + type,
            PromptUsed = "prompt",
            StructuredResult = type == AnalysisType.LineEdit ? "{\"edits\":[]}" : null,
            Language = "he",
        };

        // Ids fixed so the CreatedAt tie below has a known id order; the tie is broken by id, ascending.
        var proofread = Result(AnalysisType.Proofread, chapter.Id, t0);
        proofread.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        proofread.JobId = seed.JobId;
        proofread.ProofreadResultUnreliable = true;
        var lineEdit = Result(AnalysisType.LineEdit, chapter.Id, t0); // same CreatedAt as proofread
        lineEdit.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var literary = Result(AnalysisType.LiteraryAnalysis, chapter.Id, t0.AddMinutes(5));
        var sceneResult = Result(AnalysisType.Proofread, chapter.Id, t0.AddMinutes(1), sceneId: scene.Id);
        var archived = Result(AnalysisType.Proofread, chapter.Id, t0.AddMinutes(-5), AnalysisStatus.Archived);
        var otherChapter = Result(AnalysisType.Proofread, other.Id, t0);
        db.AnalysisResults.AddRange(proofread, lineEdit, literary, sceneResult, archived, otherChapter);

        // Stored out of OrderIndex order, with every optional field exercised.
        AddSuggestion(db, proofread, 2, "שלום", "שָׁלוֹם", outcome: SuggestionOutcome.Accepted, explanation: "ניקוד");
        AddSuggestion(db, proofread, 0, " ", "", start: 10, end: 11);
        AddSuggestion(db, proofread, 1, "ספר", "הספר", reason: "יידוע", category: "grammar", before: "את ", after: " הזה");
        AddSuggestion(db, lineEdit, 1, "הלך מהר", "מיהר");
        AddSuggestion(db, lineEdit, 0, "אמר בשקט", "לחש", outcome: SuggestionOutcome.Dismissed);
        AddSuggestion(db, sceneResult, 0, "א", "ב");
        AddSuggestion(db, archived, 0, "ישן", "חדש", outcome: SuggestionOutcome.Superseded);
        AddSuggestion(db, otherChapter, 0, "אחר", "שונה");
        db.SaveChanges();

        seed.ProofreadId = proofread.Id;
        seed.LineEditId = lineEdit.Id;
        seed.LiteraryId = literary.Id;
        seed.SceneResultId = sceneResult.Id;
        seed.ArchivedId = archived.Id;
        seed.OtherChapterResultId = otherChapter.Id;
        return seed;
    }

    private static void AddSuggestion(
        AppDbContext db, AnalysisResult result, int orderIndex, string original, string suggested,
        int? start = null, int? end = null, string? reason = null, string? category = null, string? explanation = null,
        SuggestionOutcome? outcome = null, string? before = null, string? after = null)
        => db.AnalysisSuggestions.Add(new AnalysisSuggestion
        {
            AnalysisResultId = result.Id,
            OrderIndex = orderIndex,
            OriginalText = original,
            SuggestedText = suggested,
            StartOffset = start,
            EndOffset = end,
            Reason = reason,
            Category = category,
            Explanation = explanation,
            Outcome = outcome,
            ContextBefore = before,
            ContextAfter = after,
        });

    private static AnalysisController Controller(AppDbContext db)
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);
        return new AnalysisController(
            db, unifiedAnalysis: null!, new AnalysisProgressTracker(), new Mock<IServiceScopeFactory>().Object,
            NullLogger<AnalysisController>.Instance, lifetime.Object);
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value, Web);

    private static object? OkValue<T>(ActionResult<T> result) => Assert.IsType<OkObjectResult>(result.Result).Value;

    // ── The shipped reads, verbatim (Include + ToDto), as the reference ──

    private static string ReferenceHistory(Seed seed, Guid? sceneId, string? analysisType)
    {
        using var db = seed.NewContext();
        var query = db.AnalysisResults
            .AsNoTracking()
            .Include(a => a.Suggestions)
            .Where(a => a.ChapterId == seed.ChapterId && a.Status == AnalysisStatus.Active);
        if (sceneId.HasValue)
            query = query.Where(a => a.SceneId == sceneId);
        if (!string.IsNullOrWhiteSpace(analysisType) && Enum.TryParse<AnalysisType>(analysisType, ignoreCase: true, out var type))
            query = query.Where(a => a.AnalysisType == type);

        // SQL Server returned the include ordered by result id (pinned by the query-shape test); InMemory does not
        // order at all, so the reference states the order the shipped endpoint actually had.
        var items = query.ToList().OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id).ToList();
        return Json(items.Select(a => AnalysisController.ToDto(a)).ToList());
    }

    private static string ReferenceSingle(Seed seed, Func<AnalysisResult, bool> predicate)
    {
        using var db = seed.NewContext();
        var a = db.AnalysisResults.Include(x => x.Suggestions).AsEnumerable().First(predicate);
        return Json(AnalysisController.ToDto(a));
    }

    public static IEnumerable<object?[]> HistoryCases() => new[]
    {
        new object?[] { false, null },        // what the analysis panel sends on open
        new object?[] { false, "Proofread" },
        new object?[] { false, "lineedit" },  // parsed case-insensitively
        new object?[] { false, "NotAType" },  // unparseable: no type filter, as before
        new object?[] { true, null },
    };

    [Theory]
    [MemberData(nameof(HistoryCases))]
    public async Task The_history_list_answers_what_the_include_answered(bool byScene, string? analysisType)
    {
        var seed = BuildSeed();
        Guid? sceneId = byScene ? seed.SceneId : null;

        using var db = seed.NewContext();
        var actual = Json(OkValue(await Controller(db).GetAnalyses(seed.BookId, seed.ChapterId, sceneId, analysisType)));

        var expected = ReferenceHistory(seed, sceneId, analysisType);
        Assert.Equal(expected, actual);

        // Non-vacuity: the answer carries suggestions, in OrderIndex order, and only its own.
        var dtos = JsonSerializer.Deserialize<List<AnalysisResultDto>>(actual, Web)!;
        Assert.NotEmpty(dtos);
        Assert.Contains(dtos, d => d.Suggestions is { Count: > 0 });
        Assert.All(dtos, d =>
        {
            Assert.All(d.Suggestions!, s => Assert.Equal(d.Id, s.AnalysisResultId));
            Assert.Equal(d.Suggestions!.Select(s => s.OrderIndex).OrderBy(i => i), d.Suggestions!.Select(s => s.OrderIndex));
        });
    }

    [Fact]
    public async Task The_history_list_breaks_a_created_at_tie_by_id_as_before()
    {
        var seed = BuildSeed();
        using var db = seed.NewContext();

        var dtos = (List<AnalysisResultDto>)OkValue(await Controller(db).GetAnalyses(seed.BookId, seed.ChapterId))!;

        // literary (newest), then the CreatedAt tie broken by ascending id: lineEdit (…01) before proofread (…02).
        Assert.Equal(new[] { seed.LiteraryId, seed.SceneResultId, seed.LineEditId, seed.ProofreadId }, dtos.Select(d => d.Id));
        Assert.Empty(dtos.Single(d => d.Id == seed.LiteraryId).Suggestions!);
    }

    [Fact]
    public async Task The_reads_by_id_and_by_job_answer_what_the_include_answered()
    {
        var seed = BuildSeed();

        foreach (var id in new[] { seed.ProofreadId, seed.LineEditId, seed.LiteraryId, seed.ArchivedId })
        {
            using var db = seed.NewContext();
            var actual = Json(OkValue(await Controller(db).GetAnalysisById(seed.BookId, seed.ChapterId, id, CancellationToken.None)));
            Assert.Equal(ReferenceSingle(seed, a => a.ChapterId == seed.ChapterId && a.Id == id), actual);
        }

        using (var db = seed.NewContext())
        {
            var actual = Json(OkValue(await Controller(db).GetAnalysisByJobId(seed.BookId, seed.ChapterId, seed.JobId, CancellationToken.None)));
            var expected = ReferenceSingle(seed, a => a.ChapterId == seed.ChapterId && a.BookId == seed.BookId && a.JobId == seed.JobId);
            Assert.Equal(expected, actual);
            Assert.Equal(3, JsonSerializer.Deserialize<AnalysisResultDto>(actual, Web)!.Suggestions!.Count);
        }

        using (var db = seed.NewContext())
        {
            // Another chapter's result by id is still not found.
            var result = await Controller(db).GetAnalysisById(seed.BookId, seed.ChapterId, seed.OtherChapterResultId, CancellationToken.None);
            Assert.IsType<NotFoundResult>(result.Result);
        }
    }

    [Fact]
    public async Task The_archive_step_still_supersedes_exactly_the_pending_suggestions_of_the_prior_run()
    {
        var seed = BuildSeed();

        using (var db = seed.NewContext())
        {
            await UnifiedAnalysisService.ArchivePreviousActiveAsync(
                db, seed.BookId, seed.ChapterId, null, AnalysisScope.Chapter, AnalysisType.Proofread, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        using var check = seed.NewContext();
        var results = check.AnalysisResults.AsNoTracking().ToDictionary(a => a.Id);
        var suggestions = check.AnalysisSuggestions.AsNoTracking().ToList().ToLookup(s => s.AnalysisResultId);

        // The chapter-scope Proofread run is archived; its pending suggestions are superseded, decided ones kept.
        Assert.Equal(AnalysisStatus.Archived, results[seed.ProofreadId].Status);
        Assert.Equal(
            new SuggestionOutcome?[] { SuggestionOutcome.Superseded, SuggestionOutcome.Superseded, SuggestionOutcome.Accepted },
            suggestions[seed.ProofreadId].OrderBy(s => s.OrderIndex).Select(s => s.Outcome));

        // Nothing outside the key moved: the other types, the scene run, the other chapter.
        foreach (var id in new[] { seed.LineEditId, seed.LiteraryId, seed.SceneResultId, seed.OtherChapterResultId })
            Assert.Equal(AnalysisStatus.Active, results[id].Status);
        Assert.Equal(new SuggestionOutcome?[] { SuggestionOutcome.Dismissed, null },
            suggestions[seed.LineEditId].OrderBy(s => s.OrderIndex).Select(s => s.Outcome));
        Assert.Null(suggestions[seed.SceneResultId].Single().Outcome);
        Assert.Null(suggestions[seed.OtherChapterResultId].Single().Outcome);
    }
}
