using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Shared test helpers used by both <see cref="BookReviewServiceTests"/> and
/// <see cref="BooksReviewControllerTests"/>. Extracted to avoid copy-paste drift.
/// </summary>
internal static class BookReviewTestHelpers
{
    internal const string ActiveModel = "qwen2.5:14b"; // AiOptions.DefaultModel under empty FeatureModels

    // A fresh L0 structured brief seeded directly so the BookContextAssembler takes the dense structured
    // path (UsedStructuredBriefs == true) WITHOUT any LLM call — the only LLM calls in these tests are the
    // per-dimension review calls, which the mock router serves keyed on the dimension token in the prompt.
    internal const string StructuredBriefJson = """
        {
          "plotEvents": ["The hero leaves home"],
          "characterStates": [ { "name": "Dana", "state": "fleeing", "emotionalArc": "fear to resolve" } ],
          "thematicMarkers": ["isolation", "rebirth"],
          "toneNotes": "tense",
          "openThreads": ["who sent the letter?"]
        }
        """;

    /// <summary>Spec for a single model finding (kept terse for table-style test setup).</summary>
    internal sealed record FindingSpec(string Verdict, int Severity, string Rationale, int Order);

    /// <summary>Builds the per-dimension JSON map: each of the six dimensions gets <paramref name="perDimensionCount"/>
    /// generic findings (anchored to chapter order 0). Override individual dimensions afterward.</summary>
    internal static Dictionary<string, string> FindingsPerDimension(int perDimensionCount)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dim in new[] { "plot", "character", "pacing", "tone", "theme", "continuity" })
        {
            if (perDimensionCount <= 0)
            {
                map[dim] = JsonFindings(); // empty findings array
                continue;
            }
            var specs = Enumerable.Range(0, perDimensionCount)
                .Select(i => new FindingSpec("improve", 2, $"{dim} finding {i}", i))
                .ToArray();
            map[dim] = JsonFindings(specs);
        }
        return map;
    }

    /// <summary>Serialises a BookReviewResult-shaped JSON (findings[] only) for the mock to return. The
    /// service stamps each finding's dimension to its own dimension, so the per-spec dimension is irrelevant
    /// here — order + rationale drive the dedup key.</summary>
    internal static string JsonFindings(params FindingSpec[] specs)
    {
        var findings = specs.Select(s => new
        {
            dimension = "ignored", // overwritten by the service to the called dimension
            verdict = s.Verdict,
            severity = s.Severity,
            rationale = s.Rationale,
            chapterAnchors = new[] { new { order = s.Order, title = $"Chapter {s.Order}" } },
            evidence = new[] { new { chapterOrder = s.Order, excerpt = "an excerpt" } },
            suggestedAction = (string?)null
        }).ToArray();
        return JsonSerializer.Serialize(new { findings });
    }

    /// <summary>Seeds a book with <paramref name="chapterCount"/> chapters, each with a FRESH structured L0
    /// brief and a cached BookSummaryBaseline so the BookContextAssembler takes the dense structured path
    /// (UsedStructuredBriefs == true). Returns the book id.</summary>
    internal static async Task<Guid> SeedReviewableBookAsync(AppDbContext db, int chapterCount)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Reviewable Book", Language = "he" });
        for (var i = 0; i < chapterCount; i++)
        {
            var chId = Guid.NewGuid();
            db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = i, Title = $"Chapter {i}", ContentText = $"תוכן {i}." });
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chId, Language = "he",
                StructuredJson = StructuredBriefJson, BuiltWithModel = ActiveModel,
                StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1) // fresh: after the chapter UpdatedAt
            });
        }
        // A cached BookSummaryBaseline so the assembler has an L2 BookBrief and status can compute staleness.
        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId, Language = "he",
            BookBriefJson = """{ "genre": "Fantasy", "themes": ["isolation"] }""",
            BuiltChapterCount = chapterCount, BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();
        return bookId;
    }
}
