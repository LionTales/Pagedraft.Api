using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// <see cref="BookChatContextReader"/> against a REAL database (in-memory EF), which is the only place
/// two of phase B's defects are observable at all.
///
/// <para>WHY A DATABASE-BACKED FIXTURE, WHEN EVERY OTHER PHASE-B TEST STUBS THE READER. Both defects
/// pinned here are about WHICH ROWS ARE READ, and a stubbed reader has no rows. g1 F-2 (book artifacts
/// keyed on the DETECTED ANSWER LANGUAGE instead of the BOOK's) and g1 F-7 (the author's own edited
/// summary unreachable by the question that asks for it) both survived a 2,155-test green suite, and the
/// reason is the same in both cases: NO FIXTURE HELD THE STATE. No test asked an English question about
/// a Hebrew book, and no test seeded an author-edited summary on a chapter a question would escalate.
/// That is a seed-space gap, not a weak assertion, so these are the fixtures rather than assertions
/// added to existing ones.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK. The router is a mock that is never reached: every path exercised
/// here is a read.</para>
/// </summary>
public class ProductChatBookRetrievalTests
{
    private const string HebrewQuestionAboutChapter = "מה קורה בפרק 8?";
    private const string EnglishQuestionAboutPacing = "What does the review say about pacing in my book?";

    // ─── Harness ────────────────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        // The router is registered because the service graph requires it; nothing in a READ path calls it,
        // and a call would be a defect this fixture should surface rather than tolerate.
        var router = new Mock<IAiRouter>(MockBehavior.Strict);
        services.AddSingleton(router.Object);

        services.AddSingleton<PromptFactory>();
        services.AddSingleton<SfdtConversionService>();
        services.Configure<AiOptions>(_ => { });

        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();
        services.AddSingleton<StyleBaselineBuildRegistry>();
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<StyleBaselineService>();
        services.AddScoped<DynamicTermRepairService>();
        services.AddScoped<BookReviewService>();
        services.AddScoped<IBookChatContextReader, BookChatContextReader>();

        return services.BuildServiceProvider();
    }

    /// <summary>The model stamp a brief must carry to count as FRESH, read from the SAME resolver the
    /// status count and the composer read it from - so the fixture cannot be fresh for one and stale for
    /// the other.</summary>
    private static string? ActiveSummarizationModel(ServiceProvider provider)
        => provider.GetRequiredService<BookSummaryService>().ActiveSummarizationModel;

    private static string L0Json(int order) => JsonSerializer.Serialize(new StructuredChunkSummaryData
    {
        PlotEvents = new[] { $"מרים מגיעה אל הבית הישן בפרק {order}.", $"דורון מוצא את היומן בפרק {order}." },
        CharacterStates = new[] { new ChapterCharacterState { Name = "מרים", State = "נחושה", EmotionalArc = "חשש להחלטה" } },
        ThematicMarkers = new[] { "סודות משפחתיים", "קצב" },
        ToneNotes = "טון מהורהר עם מתח גובר.",
        OpenThreads = new[] { "מי שלח את המכתב האנונימי." },
    });

    /// <summary>
    /// Seeds one book with <paramref name="chapterCount"/> chapters and a FRESH structured brief per
    /// chapter, a book-level rollup, and two findings - all under <paramref name="artifactLanguage"/>,
    /// which is deliberately a SEPARATE argument from <paramref name="bookLanguage"/> so a test can seed
    /// the states where the two disagree.
    /// </summary>
    private static Guid Seed(
        ServiceProvider provider,
        string bookLanguage,
        string artifactLanguage,
        int chapterCount = 8,
        bool seedArtifacts = true,
        string? chapterTextForLastChapter = null,
        string? authorEditedSummaryForLastChapter = null)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        var model = ActiveSummarizationModel(provider);

        var builtAt = DateTimeOffset.UtcNow;
        var chapterUpdatedAt = builtAt.AddHours(-1);   // brief built AFTER the chapter's last edit = fresh

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book
        {
            Id = bookId, Title = "צל הירח", Language = bookLanguage,
            CreatedAt = chapterUpdatedAt, UpdatedAt = chapterUpdatedAt
        });

        for (var order = 0; order < chapterCount; order++)
        {
            var chapterId = Guid.NewGuid();
            var isLast = order == chapterCount - 1;

            db.Chapters.Add(new Chapter
            {
                Id = chapterId, BookId = bookId, Order = order, Title = $"פרק {order}",
                ContentText = isLast && chapterTextForLastChapter != null ? chapterTextForLastChapter : string.Empty,
                CreatedAt = chapterUpdatedAt, UpdatedAt = chapterUpdatedAt
            });

            if (!seedArtifacts) continue;

            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chapterId, Language = artifactLanguage,
                StructuredJson = L0Json(order), StructuredBuiltAt = builtAt, BuiltWithModel = model,
                CreatedAt = builtAt,
                SummaryText = isLast && authorEditedSummaryForLastChapter != null
                    ? authorEditedSummaryForLastChapter
                    : string.Empty,
                SummaryUserEdited = isLast && authorEditedSummaryForLastChapter != null,
                SummaryUserEditedAt = isLast && authorEditedSummaryForLastChapter != null ? builtAt : null,
            });
        }

        if (seedArtifacts)
        {
            db.BookSummaryBaselines.Add(new BookSummaryBaseline
            {
                BookId = bookId, Language = artifactLanguage,
                BookBriefJson = JsonSerializer.Serialize(new BookBrief
                {
                    Genre = "דרמה משפחתית", Synopsis = "רומן על משפחה באי קטן.",
                    Themes = new List<string> { "סודות משפחתיים" }
                }),
                BuiltChapterCount = chapterCount,
                CreatedAt = builtAt, UpdatedAt = builtAt
            });

            foreach (var dimension in new[] { "pacing", "character" })
            {
                db.BookFindings.Add(new BookFinding
                {
                    BookId = bookId, Language = artifactLanguage, Dimension = dimension,
                    Verdict = "improve", Severity = 2,
                    Rationale = $"הקצב באמצע הספר מאט ({dimension}).",
                    EvidenceJson = "[]", ChapterAnchorsJson = "[{\"order\":3}]",
                    CreatedAt = builtAt, UpdatedAt = builtAt
                });
            }
        }

        db.SaveChanges();
        return bookId;
    }

    private static async Task<BookChatContext> ReadAsync(
        ServiceProvider provider, Guid bookId, string question, string answerLanguage,
        AmbientChapterContext? ambient = null)
        => await provider.GetRequiredService<IBookChatContextReader>()
            .ReadAsync(bookId, question, answerLanguage, ambient ?? AmbientChapterContext.None,
                       CancellationToken.None);

    private static string StatusText(BookChatContext context)
        => context.Blocks.Single(b => b.Kind == BookArtifactKind.Status).Text;

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── F-2: retrieval keys on the BOOK's language, never on the answer's ──────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FIXTURE THAT WOULD HAVE CAUGHT F-2: an ENGLISH question about a HEBREW book. g1 measured this
    /// retrieving 4 blocks instead of 16 and, far worse, a status computed for a language the book has no
    /// rows in - "0 of 8 briefs built", "BLOCKED: the book briefs are not built" - which the model then
    /// told the author about their own manuscript, 3 of 3 runs. A user whose briefs ARE built was being
    /// sent to go build them.
    /// </summary>
    [Fact]
    public async Task AnEnglishQuestionAboutAHebrewBook_RetrievesTheBooksOwnArtifacts()
    {
        using var provider = BuildProvider();
        var bookId = Seed(provider, bookLanguage: "he", artifactLanguage: "he");

        var context = await ReadAsync(provider, bookId, EnglishQuestionAboutPacing, answerLanguage: "en");
        var refs = context.References;

        Assert.Contains(refs, r => r.StartsWith(BookArtifactRefs.ChapterBriefPrefix, StringComparison.Ordinal));
        Assert.Contains(refs, r => r.StartsWith(BookArtifactRefs.FindingPrefix, StringComparison.Ordinal));
        Assert.Contains(BookArtifactRefs.BookBrief, refs);

        // AND THE STATUS IS THE BOOK'S REAL STATE, not the empty one a wrong key produces.
        var status = StatusText(context);
        Assert.Contains("with a current brief: 8", status, StringComparison.Ordinal);
        Assert.DoesNotContain("BLOCKED", status, StringComparison.Ordinal);
        Assert.Empty(context.Faults);
    }

    /// <summary>
    /// THE ANSWER LANGUAGE IS NOT AN INPUT TO RETRIEVAL AT ALL. Asserted as an EQUALITY between the two
    /// answer languages rather than as "English works too": a fix that merely widened the query to accept
    /// both languages would pass the test above while still letting the answer language steer what is
    /// read.
    /// </summary>
    [Fact]
    public async Task TheAnswerLanguage_ChangesNothingAboutWhatIsRetrieved()
    {
        using var provider = BuildProvider();
        var bookId = Seed(provider, bookLanguage: "he", artifactLanguage: "he");

        var inEnglish = await ReadAsync(provider, bookId, EnglishQuestionAboutPacing, answerLanguage: "en");
        var inHebrew = await ReadAsync(provider, bookId, EnglishQuestionAboutPacing, answerLanguage: "he");

        Assert.Equal(inHebrew.References.OrderBy(r => r, StringComparer.Ordinal),
                     inEnglish.References.OrderBy(r => r, StringComparer.Ordinal));
        Assert.Equal(StatusText(inHebrew), StatusText(inEnglish));

        // VACUITY GUARD: there is something real to compare - this book carries briefs, findings and a
        // book brief, not an empty set that would match itself under any keying at all.
        Assert.True(inEnglish.References.Count >= 5,
            $"expected a populated retrieval to compare, got [{string.Join(", ", inEnglish.References)}]");
    }

    /// <summary>
    /// A BOOK ROW WITH NO LANGUAGE KEYS WHERE THE PRODUCT WROTE THE ROWS, not where the reader is asking.
    /// g1's diagnostic patch fell back to the passed ANSWER language here; this is the fixture that says
    /// why that was rejected. <c>BooksController.ResolveBaselineLanguageAsync</c> - the resolver every
    /// status endpoint AND every build POST uses - normalizes a blank book language to "he", so that is
    /// where a blank-language book's briefs, findings and statuses actually live. Keying chat on the
    /// answer language instead would make an English question about such a book report "not built" about
    /// a book that is built: F-2 again, through a second door.
    /// </summary>
    [Fact]
    public async Task ABookWithNoLanguage_KeysWhereTheProductWroteTheRows_NotWhereTheReaderIsAsking()
    {
        using var provider = BuildProvider();

        // Exactly what a build POST would have written for a book row with no language.
        Assert.Equal("he", BaselineLanguageResolver.Normalize(string.Empty));
        var bookId = Seed(provider, bookLanguage: string.Empty, artifactLanguage: "he");

        var context = await ReadAsync(provider, bookId, EnglishQuestionAboutPacing, answerLanguage: "en");

        Assert.Contains(context.References,
            r => r.StartsWith(BookArtifactRefs.ChapterBriefPrefix, StringComparison.Ordinal));
        Assert.Contains("with a current brief: 8", StatusText(context), StringComparison.Ordinal);
        Assert.DoesNotContain("BLOCKED", StatusText(context), StringComparison.Ordinal);
    }

    /// <summary>
    /// AND WHEN THE BOOK GENUINELY HAS NOTHING BUILT, THE STATUS SAYS SO. This is the vacuity guard for
    /// all three tests above: "with a current brief: 8" has to be a fact this reader can fail to produce,
    /// or those assertions are measuring a constant. It is also the honesty half of the F-2 verdict - the
    /// only "found nothing" state left is the one where nothing exists, and reporting it is correct.
    /// </summary>
    [Fact]
    public async Task ABookWithNothingBuilt_ReportsNotBuilt_RatherThanAnythingElse()
    {
        using var provider = BuildProvider();
        var bookId = Seed(provider, bookLanguage: "he", artifactLanguage: "he", seedArtifacts: false);

        var context = await ReadAsync(provider, bookId, EnglishQuestionAboutPacing, answerLanguage: "en");

        Assert.DoesNotContain(context.References,
            r => r.StartsWith(BookArtifactRefs.ChapterBriefPrefix, StringComparison.Ordinal));
        Assert.Contains("with a current brief: 0", StatusText(context), StringComparison.Ordinal);
        Assert.Contains("BLOCKED", StatusText(context), StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── final-r05: which of the two languages the author-facing chapter name is written in ─────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ONE STRING IN THE BOOK SECTION WRITTEN FOR THE READER FOLLOWS THE ANSWER'S LANGUAGE, NOT THE
    /// RETRIEVAL LANGUAGE - AND THIS IS THE ONLY FIXTURE THAT CAN TELL THE TWO APART, which is why the
    /// decision is pinned here and not against the renderer alone.
    ///
    /// <para>THE DEFECT. final-r02 rendered "the author calls this chapter: ..." on every chapter-scoped
    /// block so the model could COPY a finished name instead of computing an offset, and <c>g5</c>
    /// confirmed it copies (19 of 25 answers reproduce the title and the number together). The frame was
    /// English, so copying carried English into Hebrew answers: Latin "chapter N" survived inside Hebrew
    /// prose in 7 of 45 Hebrew book-scoped runs, six of them with the CORRECT number. final-r02 predicted
    /// this exact risk and left it untested.</para>
    ///
    /// <para>WHY THE ANSWER'S LANGUAGE IS THE RIGHT ONE, AND WHY IT IS NOT OBVIOUS. The two values are
    /// ALLOWED to differ and the divergence is deliberate - it is the whole of the F-2 fix pinned above.
    /// The BOOK's language is a retrieval KEY: it decides which rows exist and it normalizes a blank to
    /// Hebrew, so keying this line on it would put a Hebrew frame into an English answer about a
    /// blank-language book. The ANSWER's language is the language of the sentence this line gets copied
    /// into, and it is also the value that selects the grounding clause instructing the model to copy it,
    /// so keying on it is what keeps an instruction and its referent in one language.</para>
    ///
    /// <para>THE QUESTION IS HELD CONSTANT ACROSS BOTH ROWS, so the ONLY variable is the answer language -
    /// the same isolation <see cref="TheAnswerLanguage_ChangesNothingAboutWhatIsRetrieved"/> uses for the
    /// opposite property. Together they say the whole thing: the answer language changes this line and
    /// nothing else, and the book's language changes everything else and not this line.</para>
    /// </summary>
    [Theory]
    [InlineData("en", "the author calls this chapter: ", "המחבר קורא לפרק הזה: ")]
    [InlineData("he", "המחבר קורא לפרק הזה: ", "the author calls this chapter: ")]
    public async Task TheAuthorFacingChapterName_FollowsTheAnswersLanguage_NotTheBooksRetrievalLanguage(
        string answerLanguage, string expectedFrame, string forbiddenFrame)
    {
        using var provider = BuildProvider();
        var bookId = Seed(provider, bookLanguage: "he", artifactLanguage: "he");

        var context = await ReadAsync(
            provider, bookId, EnglishQuestionAboutPacing, answerLanguage: answerLanguage);

        var chapterBlocks = context.Blocks
            .Where(b => b.Kind == BookArtifactKind.ChapterBrief
                     || b.Kind == BookArtifactKind.ChapterText
                     || b.Kind == BookArtifactKind.AuthorSummary)
            .ToList();

        // VACUITY GUARD: the retrieval really carried chapter-scoped blocks, so the assertions below are
        // about rendered lines rather than about an empty set. The book is Hebrew and its artifacts are
        // Hebrew under BOTH rows, which is what makes this a test of the ANSWER language.
        Assert.NotEmpty(chapterBlocks);

        foreach (var block in chapterBlocks)
        {
            Assert.True(
                block.Text.Contains(expectedFrame, StringComparison.Ordinal),
                $"a {block.Kind} block retrieved for a {answerLanguage} answer about a HEBREW book does " +
                $"not carry the author-facing name line in the answer's language ('{expectedFrame}'). " +
                $"g5 measured the model copying this line verbatim, so its language is the language the " +
                $"author reads. Block text was:\n{block.Text}");

            Assert.False(
                block.Text.Contains(forbiddenFrame, StringComparison.Ordinal),
                $"a {block.Kind} block retrieved for a {answerLanguage} answer carries the frame in the " +
                $"OTHER language ('{forbiddenFrame}'), which means it was keyed on the book's retrieval " +
                $"language instead of on the answer's. Block text was:\n{block.Text}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── F-7: the author's own summary, on a chapter the question escalated ─────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FIXTURE THAT WOULD HAVE CAUGHT F-7. Naming a chapter escalates it; escalation withholds that
    /// chapter's structured brief; and d1 section (1) had the author's own edited summary riding INSIDE
    /// that brief. So the question that literally asks "what did I write in my summary for chapter N" was
    /// the one question whose answer could never reach the prompt - g1 observed <c>chapter-summary:6</c>
    /// carried in ZERO of 6 runs of exactly that question, while the same summary DID ride along on
    /// questions that did not name the chapter. Exactly backwards.
    ///
    /// <para>All three properties are asserted together, because the fix is only correct if it does not
    /// buy the summary back by giving up what the exclusion protects: the raw text rides, the STRUCTURED
    /// brief still does not, and the author's own words do.</para>
    /// </summary>
    [Fact]
    public async Task AnEscalatedChapters_AuthorEditedSummary_StillReachesThePrompt()
    {
        const string authorSummary = "בסיכום שלי כתבתי שמרים מבינה סוף סוף שהיומן אינו של אביה.";

        using var provider = BuildProvider();
        var bookId = Seed(
            provider, bookLanguage: "he", artifactLanguage: "he",
            chapterTextForLastChapter: "מרים פתחה את היומן והבינה שכתב היד אינו של אביה כלל.",
            authorEditedSummaryForLastChapter: authorSummary);

        // "פרק 8" resolves ONLY to order 7 on an 8-chapter book (order 8 does not exist), so the usual
        // 0-based/1-based dual match collapses to one chapter and these assertions name a single target.
        var context = await ReadAsync(provider, bookId, HebrewQuestionAboutChapter, answerLanguage: "he");
        var refs = context.References;

        Assert.Equal(new[] { 7 }, context.EscalatedWholeChapters);
        Assert.Contains(BookArtifactRefs.ChapterText(7), refs);

        // The exclusion still protects what it was protecting: no structured brief for a chapter whose
        // full text is in the prompt.
        Assert.DoesNotContain(BookArtifactRefs.ChapterBrief(7), refs);

        // And the author's own words are reachable, with their own citation ref and their own block.
        Assert.Contains(BookArtifactRefs.ChapterSummary(7), refs);
        var block = context.Blocks.Single(b => b.Kind == BookArtifactKind.AuthorSummary);
        Assert.Contains(authorSummary, block.Text, StringComparison.Ordinal);
        Assert.Contains("the author's own summary", block.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// VACUITY GUARD for the test above: with NO author-edited summary the same escalating question
    /// produces no <c>chapter-summary</c> ref at all. Without this, a bug that emitted the block
    /// unconditionally (or emitted the machine-written flat summary, which no author stands behind) would
    /// pass the test above while quoting text to the author as their own writing.
    /// </summary>
    [Fact]
    public async Task AnEscalatedChapterTheAuthorNeverEdited_CarriesNoAuthorSummary()
    {
        using var provider = BuildProvider();
        var bookId = Seed(
            provider, bookLanguage: "he", artifactLanguage: "he",
            chapterTextForLastChapter: "מרים פתחה את היומן והבינה שכתב היד אינו של אביה כלל.");

        var context = await ReadAsync(provider, bookId, HebrewQuestionAboutChapter, answerLanguage: "he");

        Assert.Contains(BookArtifactRefs.ChapterText(7), context.References);
        Assert.DoesNotContain(BookArtifactRefs.ChapterSummary(7), context.References);
        Assert.DoesNotContain(context.Blocks, b => b.Kind == BookArtifactKind.AuthorSummary);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── THE AMBIENT OPEN CHAPTER (a1, d2 section (1b)): id-to-order reconciliation ─────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //
    // These need a database for the same reason the two above do: the property under test is WHICH ROW
    // the ambient id resolves against, and a stubbed reader has no rows. The selector's own rules are
    // pinned purely in ProductChatAmbientChapterTests; what lives only here is the reconciliation between
    // the id the client sent and the order the book actually has now.

    private const string HebrewDeicticQuestion = "מה קורה בפרק הזה?";

    private static Guid ChapterIdOf(ServiceProvider provider, Guid bookId, int order)
        => provider.GetRequiredService<AppDbContext>().Chapters
            .Single(c => c.BookId == bookId && c.Order == order).Id;

    /// <summary>
    /// A DEICTIC QUESTION REACHES THE OPEN CHAPTER'S RAW TEXT, end to end through the reader. This is the
    /// owner's shape: a Hebrew question that names no chapter, asked about the chapter on screen, needing
    /// the prose rather than the summary. The pre-change half is the second assertion - the identical
    /// question with nothing open retrieves no chapter text at all, which is what the live log recorded
    /// as "selected chapters [], escalated whole [] and excerpted []".
    /// </summary>
    [Fact]
    public async Task ADeicticQuestion_EscalatesTheOpenChaptersText()
    {
        using var provider = BuildProvider();
        var bookId = Seed(
            provider, bookLanguage: "he", artifactLanguage: "he",
            chapterTextForLastChapter: "מרים פתחה את היומן והבינה שכתב היד אינו של אביה כלל.");

        var open = new AmbientChapterContext(ChapterIdOf(provider, bookId, 7), 7);

        var withChapterOpen = await ReadAsync(provider, bookId, HebrewDeicticQuestion, "he", open);

        Assert.Equal(new[] { 7 }, withChapterOpen.EscalatedWholeChapters);
        Assert.Contains(BookArtifactRefs.ChapterText(7), withChapterOpen.References);
        Assert.Empty(withChapterOpen.Faults);

        // PRE-CHANGE: the same question with nothing open reaches no chapter text.
        var withNothingOpen = await ReadAsync(provider, bookId, HebrewDeicticQuestion, "he");

        Assert.Empty(withNothingOpen.EscalatedWholeChapters);
        Assert.Empty(withNothingOpen.EscalatedExcerptChapters);
        Assert.True(withNothingOpen.Keys.NeedsChapterClarification);
    }

    /// <summary>
    /// THE ID DECIDES WHICH CHAPTER AND THE FRESHLY-READ ROW DECIDES ITS ORDER. The client's order is a
    /// snapshot from when the editor loaded the book, so a reorder since then makes it name a different
    /// chapter; trusting it would produce a confident answer about the wrong chapter of the author's own
    /// manuscript. Here the client sends the right id with a stale order and the retrieval still lands on
    /// the chapter the author is looking at.
    /// </summary>
    [Fact]
    public async Task AStaleAmbientOrder_LosesToTheIdsCurrentRow()
    {
        using var provider = BuildProvider();
        var bookId = Seed(
            provider, bookLanguage: "he", artifactLanguage: "he",
            chapterTextForLastChapter: "מרים פתחה את היומן והבינה שכתב היד אינו של אביה כלל.");

        var stale = new AmbientChapterContext(ChapterIdOf(provider, bookId, 7), ChapterOrder: 2);

        var context = await ReadAsync(provider, bookId, HebrewDeicticQuestion, "he", stale);

        Assert.Equal(new[] { 7 }, context.Keys.ChapterOrders);
        Assert.Contains(BookArtifactRefs.ChapterText(7), context.References);

        // VACUITY GUARD: the stale number names a REAL chapter of this book, so it could have been used -
        // the id winning is a decision, not the only option that would have resolved.
        Assert.Contains(BookArtifactRefs.ChapterBrief(2), context.References);
    }

    /// <summary>
    /// AN ID THAT NO LONGER NAMES A CHAPTER OF THIS BOOK falls back to the sent order, range-checked
    /// against the same rows - and when that number is not a chapter either, nothing is ambient and the
    /// turn asks which chapter rather than grounding in whatever happened to be nearby.
    /// </summary>
    [Fact]
    public async Task AnAmbientIdThatIsNotAChapterOfThisBook_FallsBackToTheOrder_ThenToNothing()
    {
        using var provider = BuildProvider();
        var bookId = Seed(provider, bookLanguage: "he", artifactLanguage: "he");

        var deleted = new AmbientChapterContext(Guid.NewGuid(), ChapterOrder: 5);
        var fellBack = await ReadAsync(provider, bookId, HebrewDeicticQuestion, "he", deleted);
        Assert.Equal(new[] { 5 }, fellBack.Keys.ChapterOrders);

        var nothingUsable = new AmbientChapterContext(Guid.NewGuid(), ChapterOrder: 99);
        var resolvedNothing = await ReadAsync(provider, bookId, HebrewDeicticQuestion, "he", nothingUsable);
        Assert.Empty(resolvedNothing.Keys.ChapterOrders);
        Assert.True(resolvedNothing.Keys.NeedsChapterClarification);
    }

    /// <summary>
    /// AN AMBIENT-RESOLVED CHAPTER EXCERPTS EXACTLY LIKE AN EXPLICITLY-NAMED ONE, AND CARRIES THE LABEL.
    /// The label is a d1 section (3) safety property that both gates passed 12/12: it is what decides
    /// whether the answer may say "this chapter does not mention X" or must say "the parts I could read
    /// do not mention X". The owner's real book is a single 2,708-word Hebrew chapter, so the ambient
    /// path's COMMON case is the excerpt, not the whole chapter - which makes this the row that would
    /// have to fail before bucket (f) could regress unnoticed.
    /// </summary>
    [Fact]
    public async Task AnAmbientChapterTooLargeToRideWhole_ExcerptsAndSaysSo()
    {
        // SIZED OFF THE BUDGET CONSTANT, NOT OFF A LITERAL REPEAT COUNT (w9). It used to be a flat 200
        // repetitions, which was "too large" only against the escalation slice of the day: raising that
        // slice from 3,500 to 7,200 made this fixture ride WHOLE and the test failed while asserting
        // nothing was wrong with the code. Deriving the length from the constant keeps "too large to ride
        // whole" true by construction, whatever the slice becomes.
        var sentence = "מרים פתחה את היומן והבינה שכתב היד אינו של אביה כלל.";
        var repeats = 1;
        while (ProductChatBudget.EstimateTokens(string.Join(" ", Enumerable.Repeat(sentence, repeats)))
               <= BookChatExcerpts.EscalationBudgetTokens)
        {
            repeats *= 2;
        }

        var longChapter = string.Join(" ", Enumerable.Repeat(sentence, repeats));

        using var provider = BuildProvider();
        var bookId = Seed(
            provider, bookLanguage: "he", artifactLanguage: "he", chapterTextForLastChapter: longChapter);

        var open = new AmbientChapterContext(ChapterIdOf(provider, bookId, 7), 7);
        var context = await ReadAsync(provider, bookId, HebrewDeicticQuestion, "he", open);

        Assert.Equal(new[] { 7 }, context.EscalatedExcerptChapters);
        Assert.Empty(context.EscalatedWholeChapters);

        var text = context.Blocks.Single(b => b.Kind == BookArtifactKind.ChapterText).Text;
        Assert.Contains("EXCERPT, not the whole chapter", text, StringComparison.Ordinal);

        // And NOT the whole-chapter label, which is the one that licenses "this chapter does not mention
        // X". The two labels share their tail, so the comma is load-bearing in this assertion.
        Assert.DoesNotContain(", whole chapter]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OTHER HALF OF F-7, and a state no fixture in this suite held either: a chapter the selector
    /// INTENDED to escalate whose text never rode along, because the chapter is empty. The exclusion used
    /// to key on the INTENT, so that chapter lost its brief and gained no text and the prompt carried
    /// nothing at all about the chapter the question named - while the citation vocabulary still
    /// advertised a <c>chapter-brief</c> ref that was never carried, which is one of the fabricated
    /// citations g1 recorded reaching the user's prose.
    /// </summary>
    [Fact]
    public async Task AChapterThatEscalatedButHasNoText_KeepsItsBrief()
    {
        using var provider = BuildProvider();
        var bookId = Seed(provider, bookLanguage: "he", artifactLanguage: "he");   // every chapter empty

        var context = await ReadAsync(provider, bookId, HebrewQuestionAboutChapter, answerLanguage: "he");

        Assert.Empty(context.EscalatedWholeChapters);
        Assert.Empty(context.EscalatedExcerptChapters);
        Assert.DoesNotContain(BookArtifactRefs.ChapterText(7), context.References);

        // The named chapter is still answerable FROM ITS BRIEF, which is the whole point.
        Assert.Contains(BookArtifactRefs.ChapterBrief(7), context.References);

        // An empty chapter is not a fault: "chapter 8 has no text yet" is a true answer.
        Assert.Empty(context.Faults);
    }
}
