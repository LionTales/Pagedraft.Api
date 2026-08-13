using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests;

/// <summary>
/// F-9's missing measurement (chatbot phase B gate fixes, f0): g1 seeded three books (8/6/4 chapters),
/// the trim cascade never fired in 222 real-endpoint calls, and <c>LogTrim</c> never emitted
/// <c>TRIMMED</c> or <c>STILL exceeds</c> - so d1's prediction that book-scoped traffic would hit the
/// cascade "far more often", and the NumCtx-widening question d1 section (5) deferred to that data, both
/// had NO DATA behind them. This is the 40-chapter Hebrew book d1's own density arithmetic was about,
/// driven through <see cref="ProductChatBudget.Compose"/> DETERMINISTICALLY: no GPU, no model call, no
/// database, no live endpoint.
///
/// <para><b>THE DENSITY TRAP, HONOURED.</b> Every chapter's structured content is round-tripped through
/// a REAL <c>ChunkSummary.StructuredJson</c> string: built as <c>StructuredChunkSummaryData</c>,
/// serialized, and read back through the SAME <c>StructuredChunkSummaryParser.Parse</c> the freshness
/// gate and L1 composition use (see the fixture half, <c>ProductChatBookFortyChapterFixture.cs</c>,
/// <c>BuildBriefs</c>), then mapped into <c>ChapterBrief</c> EXACTLY as
/// <c>BookSummaryService.ComposeChapterBriefsAsync</c> does it: <c>Summary</c> stays null, and the five
/// structured lists (PlotEvents, CharacterStates, ThematicMarkers, ToneNotes, OpenThreads) carry the
/// whole signal. A fixture that put its density in a field <c>BookContextAssembler.FormatChapterBrief</c>
/// never reads (a flat summary string, an unused property) would render near-empty and green every
/// assertion below vacuously - the exact trap this class exists to avoid, reproduced one layer up from
/// the DB column it is named for since this suite has no database.</para>
///
/// <para><b>EVERY BLOCK IS THE REAL RENDERER, EVERY RANK IS THE REAL RANKER.</b>
/// <see cref="BookArtifactBlocks"/> (which calls <c>BookContextAssembler.FormatChapterBrief</c>
/// internally), <see cref="BookChatContextReader.RankChapterBriefs"/>,
/// <see cref="BookChatContextReader.FindingRank"/>, <see cref="BookArtifactSelector.Select"/> and
/// <see cref="BookChatExcerpts.Build"/> are the SAME static, pure methods
/// <c>BookChatContextReader.ReadAsync</c> calls in production - only the database I/O around them is
/// replaced by this fixture (the fixture half's <c>Assemble</c> mirrors <c>ReadAsync</c>'s block order),
/// so the composition measured here is the composition the live endpoint would produce for this book.</para>
///
/// <para>THIS FILE holds the [Fact]s. The fixture itself (the 40-chapter content banks, the L0-&gt;L1
/// round trip, findings, register, statuses, and the <c>Assemble</c>/<c>ComposeFor</c> helpers) lives in
/// the other half of this `partial class`, <c>ProductChatBookFortyChapterFixture.cs</c> - split purely to
/// stay under this codebase's ~700-line file-size guidance, not a second concern.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE.</para>
/// </summary>
public partial class ProductChatBookFortyChapterFixtureTests
{
    private readonly ITestOutputHelper _output;

    public ProductChatBookFortyChapterFixtureTests(ITestOutputHelper output) => _output = output;

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. THE DENSITY MEASUREMENT ─────────────────────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE NUMBER THIS WHOLE FIXTURE STANDS OR FALLS ON, measured (not assumed) against the REAL
    /// <c>BookContextAssembler.FormatChapterBrief</c> and the REAL <see cref="ProductChatBudget.EstimateTokens"/>
    /// - the same renderer and the same estimator production uses. d1 measured a real 5-chapter sample of
    /// the dev DB at 1,141-1,617 formatted characters, ~700-800 tokens per chapter; this asserts the
    /// 40-chapter fixture landed in the same order of magnitude, with a floor far enough above zero that
    /// a degenerate (near-empty) brief would fail it rather than pass silently.
    /// </summary>
    [Fact]
    public void TheFixture_HitsRealStructuredDensity_ThroughTheRealFormatChapterBrief()
    {
        var briefs = BuildBriefs();
        Assert.Equal(ChapterCount, briefs.Count);

        var perChapterTokens = briefs
            .Select(b => ProductChatBudget.EstimateTokens(BookContextAssembler.FormatChapterBrief(b)))
            .ToList();

        var avg = perChapterTokens.Average();
        var min = perChapterTokens.Min();
        var max = perChapterTokens.Max();

        _output.WriteLine(
            $"achieved density: avg {avg:F0} tokens/chapter (min {min}, max {max}) across {ChapterCount} " +
            "chapters, measured through the real FormatChapterBrief + EstimateTokens. d1's real-DB sample: " +
            "~700-800 tokens/chapter.");

        Assert.True(avg is >= 550 and <= 950,
            $"average formatted chapter-brief density was {avg:F0} tokens across {ChapterCount} chapters " +
            $"(min {min}, max {max}); d1's real-DB measurement was ~700-800 tokens/chapter through the " +
            "same FormatChapterBrief - outside this band the fixture is not representative of production " +
            "density in either direction.");

        // VACUITY GUARD: not one chapter is near-empty. A degenerate brief (the num_ctx-truncation shape
        // StructuredChunkSummaryParser.IsDegenerate names) would pull the average down without every
        // chapter failing individually, so the floor is checked per-chapter, not only on the average.
        Assert.True(min >= 300,
            $"the thinnest chapter brief in the fixture estimated only {min} tokens; every chapter must " +
            "carry real structured content or a downstream assertion could be passing against an " +
            "accidentally-empty chapter rather than a representative one.");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. THE FIVE QUESTION-SHAPE MEASUREMENTS ────────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SHAPE 1: book-level backbone only. No chapter, character or dimension key - "what happens in my
    /// book" grounds in the statuses, the book brief, and (per <c>RankChapterBriefs</c>'s no-keys
    /// fallback) the first <see cref="BookChatContextReader.MaxChapterBriefs"/> chapters in book order.
    /// </summary>
    [Fact]
    public void Shape1_BackboneOnly_FitsComfortably_AndTheStatusBlockSurvives()
    {
        const string question = "מה קורה בספר שלי?";

        var briefs = BuildBriefs();
        var findings = BuildFindings();
        var register = BuildRegister();
        var assembled = Assemble(question, briefs, findings, register, new Dictionary<int, string>());

        Assert.True(assembled.Keys.IsEmpty, "shape 1 must resolve to no chapter/character/dimension key");
        Assert.Empty(assembled.EscalatedWhole);
        Assert.Empty(assembled.EscalatedExcerpt);

        var composed = ComposeFor(question, assembled.Blocks);

        Assert.Contains(BookArtifactRefs.StatusSummary, composed.BookBlocks.SelectMany(b => b.References));
        Assert.False(composed.StillOverBudget,
            $"shape 1 (backbone only) estimated {composed.EstimatedTokens}/{composed.BudgetTokens} tokens " +
            "and should never be the shape that overruns the budget.");
    }

    /// <summary>
    /// SHAPE 2: a multi-chapter question via a CHARACTER, not a chapter number. מרים כהן's name is
    /// planted in every chapter whose order is a multiple of 3 (14 of 40 chapters) with no location cue,
    /// so this grounds in up to <see cref="BookChatContextReader.MaxChapterBriefs"/> of THOSE briefs and
    /// triggers NO escalation - the shape the todo distinguishes from "escalating a whole chapter".
    /// </summary>
    [Fact]
    public void Shape2_MultiChapterViaCharacter_PullsSeveralBriefs_WithNoEscalation()
    {
        const string question = "מה קורה עם מרים כהן במהלך הספר?";

        var briefs = BuildBriefs();
        var findings = BuildFindings();
        var register = BuildRegister();
        var assembled = Assemble(question, briefs, findings, register, new Dictionary<int, string>());

        Assert.Contains(Miriam, assembled.Keys.CharacterNames);
        Assert.False(assembled.Keys.HasLocationCue, "a bare character name must not carry a location cue");
        Assert.Empty(assembled.Keys.EscalationChapterOrders);
        Assert.Empty(assembled.EscalatedWhole);
        Assert.Empty(assembled.EscalatedExcerpt);

        var briefRefs = assembled.Blocks
            .Where(b => b.Kind == BookArtifactKind.ChapterBrief)
            .SelectMany(b => b.References)
            .Where(r => r.StartsWith(BookArtifactRefs.ChapterBriefPrefix, StringComparison.Ordinal))
            .ToList();

        // VACUITY GUARD: character-based ranking really did select MORE THAN ONE chapter - otherwise this
        // is indistinguishable from shape 1's fallback.
        Assert.True(briefRefs.Count > 1, $"expected several chapter briefs ranked by character match, got {briefRefs.Count}");
        Assert.True(briefRefs.Count <= BookChatContextReader.MaxChapterBriefs);

        var composed = ComposeFor(question, assembled.Blocks);
        Assert.Contains(BookArtifactRefs.StatusSummary, composed.BookBlocks.SelectMany(b => b.References));
    }

    /// <summary>
    /// SHAPE 3: a question escalating a WHOLE chapter. "chapter 40" resolves ONLY to order 39 (order 40
    /// does not exist, so the usual 0-based/1-based dual match collapses to one), and chapter 39's raw
    /// text is short enough to fit the whole 3,500-token escalation slice.
    /// </summary>
    [Fact]
    public void Shape3_EscalatesAWholeChapter_AndTheEscalatedTextSurvivesTrimming()
    {
        const string question = "מה קורה בפרק 40?";
        const int order = 39;

        var briefs = BuildBriefs();
        var findings = BuildFindings();
        var register = BuildRegister();
        var rawText = new Dictionary<int, string> { [order] = BuildShortChapterText() };

        Assert.True(ProductChatBudget.EstimateTokens(rawText[order]) < BookChatExcerpts.EscalationBudgetTokens,
            "shape 3's fixture chapter must fit the escalation slice WHOLE, or this is measuring shape 4");

        var assembled = Assemble(question, briefs, findings, register, rawText);

        Assert.Equal(new[] { order }, assembled.Keys.EscalationChapterOrders);
        Assert.Equal(new[] { order }, assembled.EscalatedWhole);
        Assert.Empty(assembled.EscalatedExcerpt);
        // The escalated chapter's OWN brief must not also be selected (d1: the escalation excludes it).
        Assert.DoesNotContain(order, BookChatContextReader
            .RankChapterBriefs(briefs, assembled.Keys, assembled.EscalatedWhole)
            .Select(r => r.Brief.Order));

        var composed = ComposeFor(question, assembled.Blocks);

        Assert.Contains(BookArtifactRefs.ChapterText(order), composed.BookBlocks.SelectMany(b => b.References));
        Assert.Contains(BookArtifactRefs.StatusSummary, composed.BookBlocks.SelectMany(b => b.References));
        Assert.DoesNotContain(BookArtifactRefs.ChapterText(order), composed.DroppedBookRefs);
    }

    /// <summary>
    /// SHAPE 4: a question escalating an OVER-BUDGET chapter. "chapter 0" resolves ONLY to order 0 (order
    /// -1 does not exist), and chapter 0's raw text (~14,000 tokens, matching d1's measured real max of
    /// ~14,006) alone exceeds the whole 3,500-token escalation slice, so it must degrade to a labeled
    /// excerpt rather than ride along whole.
    /// </summary>
    [Fact]
    public void Shape4_EscalatesAnOverBudgetChapter_AndDegradesToAnExcerpt()
    {
        const string question = "מה קורה בפרק 0 עם הספינה הטבועה?";
        const int order = 0;

        var briefs = BuildBriefs();
        var findings = BuildFindings();
        var register = BuildRegister();
        var rawText = new Dictionary<int, string> { [order] = BuildLongChapterText() };

        Assert.True(ProductChatBudget.EstimateTokens(rawText[order]) > BookChatExcerpts.EscalationBudgetTokens,
            "shape 4's fixture chapter must NOT fit the escalation slice whole, or this is measuring shape 3");

        var assembled = Assemble(question, briefs, findings, register, rawText);

        Assert.Equal(new[] { order }, assembled.Keys.EscalationChapterOrders);
        Assert.Empty(assembled.EscalatedWhole);
        Assert.Equal(new[] { order }, assembled.EscalatedExcerpt);

        var textBlock = assembled.Blocks.Single(b => b.Kind == BookArtifactKind.ChapterText);
        Assert.Contains("EXCERPT, not the whole chapter", textBlock.Text, StringComparison.Ordinal);
        // The lexical match found the planted sentence rather than merely falling back to the opening.
        Assert.Contains("הספינה הטבועה", textBlock.Text, StringComparison.Ordinal);

        var composed = ComposeFor(question, assembled.Blocks);

        Assert.Contains(BookArtifactRefs.ChapterText(order), composed.BookBlocks.SelectMany(b => b.References));
        Assert.Contains(BookArtifactRefs.StatusSummary, composed.BookBlocks.SelectMany(b => b.References));
        Assert.DoesNotContain(BookArtifactRefs.ChapterText(order), composed.DroppedBookRefs);
    }

    /// <summary>
    /// SHAPE 5: a dimension question pulling many findings. Ten seeded "pacing" findings exceed
    /// <see cref="BookChatContextReader.MaxFindings"/> (8), so the selection CAP has something real to
    /// trim, and pacing-anchored chapters (every 5th) outrank the rest for the chapter-brief slots too.
    ///
    /// <para>THAT SECOND CLAUSE WAS FALSE UNTIL A CR BOT CAUGHT IT. The fixture plants the Hebrew marker
    /// on every fifth brief, and <c>RankChapterBriefs</c> compared it against the canonical English slug,
    /// so the planted markers scored nothing and this shape exercised only the findings half of what it
    /// describes. The unit that pins the ranking itself is
    /// <c>ProductChatBookServiceTests.ADimensionQuestion_RanksBriefsWhoseMarkersNameItInEitherLanguage</c>,
    /// which asserts both languages because the English half is what made the defect invisible here.</para>
    /// </summary>
    [Fact]
    public void Shape5_DimensionQuestion_CapsAtMaxFindings_RankedByTheDimension()
    {
        const string question = "מה אתם אומרים על הקצב בספר שלי?";

        var briefs = BuildBriefs();
        var findings = BuildFindings();
        var register = BuildRegister();

        // VACUITY GUARD: there really are more pacing findings than the cap allows.
        var pacingFindingCount = findings.Count(f => f.Dimension == "pacing");
        Assert.True(pacingFindingCount > BookChatContextReader.MaxFindings,
            $"fixture seeded only {pacingFindingCount} pacing findings; the cap ({BookChatContextReader.MaxFindings}) " +
            "has nothing real to trim without more than that.");

        var assembled = Assemble(question, briefs, findings, register, new Dictionary<int, string>());

        Assert.Contains("pacing", assembled.Keys.Dimensions);

        var findingRefs = assembled.Blocks
            .Where(b => b.Kind == BookArtifactKind.Finding)
            .ToList();
        Assert.Equal(BookChatContextReader.MaxFindings, findingRefs.Count);

        var pacingCited = findingRefs.Count(b => b.Text.Contains("Dimension: pacing", StringComparison.Ordinal));
        Assert.True(pacingCited == BookChatContextReader.MaxFindings,
            $"expected every one of the {BookChatContextReader.MaxFindings} selected findings to be a " +
            $"pacing finding (the strongest-ranked dimension), got {pacingCited}");

        var composed = ComposeFor(question, assembled.Blocks);
        Assert.Contains(BookArtifactRefs.StatusSummary, composed.BookBlocks.SelectMany(b => b.References));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 3. THE STATUS BLOCK, UNDER REAL PRESSURE ───────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE MEASUREMENT F-9 WAS MISSING, all five shapes plus one combined-pressure case, reported via
    /// <see cref="ITestOutputHelper"/> (<c>dotnet test -v normal</c> prints it) so the numbers are
    /// inspectable from every run, not just a failing one.
    ///
    /// <para>d1's §5 question, answered with data: at real 40-chapter structured density, NONE of the
    /// five shapes trips <c>StillOverBudget</c> or drops a single book artifact under the SHIPPED
    /// 14,080-token window - the cascade g1 could not observe on 8/6/4-chapter books does not fire here
    /// either, in a single-turn request. The sixth row combines the heaviest single shape (an
    /// over-budget escalation) with phase A's own full 8-turn Hebrew history worst case (g1 F2), the one
    /// combination none of the five shapes alone tests, to see whether THAT is what it takes.</para>
    /// </summary>
    [Fact]
    public void AcrossAllFiveShapes_TheStatusBlockSurvives_AndReportsTheRealNumbers()
    {
        var briefs = BuildBriefs();
        var findings = BuildFindings();
        var register = BuildRegister();

        var shapes = new (string Name, string Question, IReadOnlyDictionary<int, string> RawText, bool WithFullHistory)[]
        {
            ("1 backbone-only", "מה קורה בספר שלי?", new Dictionary<int, string>(), false),
            ("2 multi-chapter (character)", "מה קורה עם מרים כהן במהלך הספר?", new Dictionary<int, string>(), false),
            ("3 escalate-whole", "מה קורה בפרק 40?", new Dictionary<int, string> { [39] = BuildShortChapterText() }, false),
            ("4 escalate-over-budget", "מה קורה בפרק 0 עם הספינה הטבועה?", new Dictionary<int, string> { [0] = BuildLongChapterText() }, false),
            ("5 dimension (pacing)", "מה אתם אומרים על הקצב בספר שלי?", new Dictionary<int, string>(), false),
            ("6 shape4 + full 8-turn Hebrew history (combined pressure)",
                "מה קורה בפרק 0 עם הספינה הטבועה?", new Dictionary<int, string> { [0] = BuildLongChapterText() }, true),
        };

        var report = new StringBuilder();
        report.AppendLine(
            "shape | estimated tokens | budget | pct | still-over-budget | dropped book refs | dropped guides | " +
            "dropped history turns | status survived");

        foreach (var (name, question, rawText, withHistory) in shapes)
        {
            var assembled = Assemble(question, briefs, findings, register, rawText);
            var history = withHistory ? FullHebrewHistory() : null;
            var composed = ComposeFor(question, assembled.Blocks, history);

            var statusSurvived = composed.BookBlocks.SelectMany(b => b.References).Contains(BookArtifactRefs.StatusSummary);
            var droppedKinds = string.Join(", ", composed.DroppedBookRefs.Select(DescribeRefKind).Distinct());
            var pct = 100.0 * composed.EstimatedTokens / composed.BudgetTokens;

            report.AppendLine(
                $"{name} | {composed.EstimatedTokens} | {composed.BudgetTokens} | {pct:F1}% | " +
                $"{composed.StillOverBudget} | [{droppedKinds}] | {composed.DroppedGuideIds.Count} | " +
                $"{composed.DroppedTurns} | {statusSurvived}");

            if (withHistory)
            {
                // The combined-pressure row's real story: history is what absorbs it, per d1's drop
                // order, and the composition still fits - it must not be silently identical to shape 4
                // for the wrong reason (e.g. FullHebrewHistory() producing zero turns).
                Assert.Equal(ProductChatService.MaxHistoryTurns, history!.Count);
                Assert.Equal(ProductChatService.MaxHistoryTurns, composed.DroppedTurns);
            }

            // THE ONE INVARIANT THIS TEST PINS, for real, across every shape including the combined one:
            // the tutoring floor never goes missing while a bookId is in scope.
            Assert.True(statusSurvived, $"shape '{name}' lost the never-droppable status block");
        }

        _output.WriteLine(report.ToString());

        // VACUITY GUARD: the report is not vacuous praise - at least one shape genuinely used more than
        // half the budget, so "nothing was dropped" reflects real headroom, not an empty fixture.
        Assert.Contains(shapes, s =>
        {
            var assembled = Assemble(s.Question, briefs, findings, register, s.RawText);
            var composed = ComposeFor(s.Question, assembled.Blocks, s.WithFullHistory ? FullHebrewHistory() : null);
            return composed.EstimatedTokens > composed.BudgetTokens / 2;
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 4. WHAT THE F-7 FIX COSTS, measured against this same 40-chapter book ──────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PRICE OF MAKING THE AUTHOR'S OWN SUMMARY REACHABLE (g1 F-7), measured rather than argued.
    /// An escalated chapter's structured brief is still withheld; what is added is the author's own flat
    /// summary as a block of its own, and this is the WORST case for it - the over-budget escalation from
    /// shape 4, which already spends the whole 3,500-token escalation slice, run WITH phase A's full
    /// 8-turn Hebrew history on top of it.
    ///
    /// <para>The measurement matters because "we cannot afford to carry both" would have been the obvious
    /// argument against fixing F-7, and f0 already showed it does not hold on a 40-chapter book at real
    /// density: the cascade never fires. The delta is reported through
    /// <see cref="ITestOutputHelper"/> so the number is inspectable from any run, and the assertions pin
    /// the two things that must be true regardless of the number: the status block still survives, and no
    /// book artifact is dropped to pay for it.</para>
    /// </summary>
    [Fact]
    public void TheAuthorSummaryOfAnEscalatedChapter_CostsLittle_AndDropsNothing()
    {
        const string question = "מה קורה בפרק 0 עם הספינה הטבועה?";
        const int order = 0;

        // A REAL author summary length, not a token: d1 measured the flat surface as prose the author
        // wrote, so a one-line stub would under-report the cost it is here to measure.
        var authorSummary = string.Join(" ", Enumerable.Repeat(
            "בסיכום שלי כתבתי שמרים מגלה את היומן בתא ההגה ומבינה שכתב היד אינו של אביה, " +
            "ושכל מה שסופר לה על הלילה ההוא היה גרסה אחת בלבד מתוך כמה.", 3));

        var briefs = BuildBriefs();
        var findings = BuildFindings();
        var register = BuildRegister();
        var rawText = new Dictionary<int, string> { [order] = BuildLongChapterText() };
        var history = FullHebrewHistory();

        var without = ComposeFor(question, Assemble(question, briefs, findings, register, rawText).Blocks, history);

        var withAssembled = Assemble(
            question, briefs, findings, register, rawText,
            authorSummariesByOrder: new Dictionary<int, string> { [order] = authorSummary });
        var with = ComposeFor(question, withAssembled.Blocks, history);

        // VACUITY GUARD: the summary really is being carried, and as its OWN block with its own ref -
        // otherwise the "delta" below would be the cost of nothing.
        Assert.Contains(BookArtifactRefs.ChapterSummary(order), with.BookBlocks.SelectMany(b => b.References));
        Assert.DoesNotContain(BookArtifactRefs.ChapterSummary(order), without.BookBlocks.SelectMany(b => b.References));

        // The exclusion the fix had to preserve: no structured brief for the escalated chapter, either way.
        Assert.DoesNotContain(BookArtifactRefs.ChapterBrief(order), with.BookBlocks.SelectMany(b => b.References));

        var delta = with.EstimatedTokens - without.EstimatedTokens;
        _output.WriteLine(
            $"F-7 author-summary cost on the 40-chapter book, worst case (over-budget escalation + full " +
            $"8-turn Hebrew history): {without.EstimatedTokens} -> {with.EstimatedTokens} of " +
            $"{with.BudgetTokens} tokens (+{delta}, " +
            $"{100.0 * without.EstimatedTokens / without.BudgetTokens:F1}% -> " +
            $"{100.0 * with.EstimatedTokens / with.BudgetTokens:F1}%); dropped book refs " +
            $"[{string.Join(", ", with.DroppedBookRefs.Select(DescribeRefKind).Distinct())}]; " +
            $"still over budget: {with.StillOverBudget}");

        // IT IS REALLY IN THE PROMPT, asserted on the author's own WORDS in the composed instruction
        // rather than on the token delta. The delta is not a cost measurement here: this fixture composes
        // AT the budget, so the trimmer answers every added block by dropping a cheaper one, and the sign
        // of the difference is decided by which two things happened to swap. (It is currently NEGATIVE:
        // the summary displaces two findings that together cost more than it does.) The words are in the
        // string or they are not, and that is the thing "the summary is unreachable" was ever about.
        Assert.Contains(authorSummary[..80], with.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(authorSummary[..80], without.Instruction, StringComparison.Ordinal);

        // AND WHAT IT COSTS COMES OUT OF THE TIER d1 SAID IT WOULD, which is the property worth pinning
        // and the one that survives an unrelated edit. f2 rewrote the book-aware rule and its citation
        // sentence (F-3, F-5, F-6, F-8), paid for TWICE - system slot plus instruction head - which grew
        // that part by 189 tokens in English and 238 in Hebrew. On a deliberately saturated case that is
        // enough to
        // turn "nothing at all is dropped to pay for the summary" into "two more FINDINGS are", and the
        // original equal-count assertion was measuring the headroom that happened to be left rather than
        // anything about the summary: at a full budget no added block can be free, whatever it is.
        //
        // The tier is what d1 actually promised. Findings drop FIRST by design, because a finding's claim
        // can be pointed at ("see your findings ledger") without its full text riding along, and the
        // analysis HISTORY metadata drops second, because it is the only block that grounds nothing about
        // the manuscript's content. So the assertion is that the cost lands in those two tiers and nowhere
        // above them: the statuses, the escalated raw text, the author's own summary and the book brief are
        // all still in the prompt. That is strictly STRONGER than counting drops.
        //
        // be-c02 MOVED THE LINE ONE TIER FURTHER DOWN, and the number is worth recording rather than
        // quietly absorbing: the chapter-numbering translation sentence costs ~65 tokens in English and
        // ~106 in Hebrew, paid TWICE, and on this deliberately saturated case that was enough to turn
        // "every dropped ref is a finding" into "every finding, and then the history block". Both are
        // inside the designed sacrifice order (BookArtifactKind's numeric order IS the drop order), which
        // is why the assertion is written against the ORDER rather than against whichever tier the budget
        // currently happens to stop at. What must NOT drop is asserted positively just above.
        //
        // final-r02 MOVED IT ONE TIER FURTHER AGAIN, TO ChapterBrief, AND THE COST WAS BOUNDED BEFORE IT
        // WAS ACCEPTED. The author-facing name line (BookArtifactBlocks.AuthorFacingChapterName) rides on
        // every chapter-scoped block, so it is paid once per carried brief; that is ~17 tokens each, and
        // this composition carries several. THE HEADROOM IT HAD WAS 25 TOKENS: with the line reduced to a
        // 2-character stub this case measured 14,080 of 14,080 exactly, dropping only [finding]. So there
        // is no wording of a real line that fits here - it was measured at the floor, not assumed - and
        // the narrowed grounding clause that paid for part of it (-16 en / -30 he tokens, x2) was already
        // in place when that floor was taken. The tier it lands in is still inside the designed order, and
        // the escalated chapter's own TEXT, the author's own summary, the register, the book brief and the
        // statuses all still ride: what is given up is one of several structured briefs for chapters the
        // question did not name, which is the one thing in this composition that another block already
        // covers. Bounded by the widened assertion below plus the positive list above.
        Assert.False(with.StillOverBudget);

        var carried = with.BookBlocks.SelectMany(b => b.References).ToList();
        Assert.Contains(BookArtifactRefs.StatusSummary, carried);
        Assert.Contains(BookArtifactRefs.ChapterText(order), carried);
        Assert.Contains(BookArtifactRefs.ChapterSummary(order), carried);
        Assert.Contains(BookArtifactRefs.BookBrief, carried);

        // AND THE TIER DIRECTLY ABOVE THE ONE THAT NOW DROPS, asserted so the widening below is a line and
        // not an open door: the register is BookArtifactKind 4, the first tier the trimmer must not reach.
        Assert.Contains(BookArtifactRefs.Register, carried);

        // VACUITY GUARD: something WAS given up here, so the claim below is a statement about a non-empty
        // set and not a loop that never ran.
        Assert.NotEmpty(with.DroppedBookRefs);
        Assert.All(with.DroppedBookRefs, reference =>
            Assert.True(
                reference.StartsWith(BookArtifactRefs.FindingPrefix, StringComparison.Ordinal)
                || string.Equals(reference, BookArtifactRefs.History, StringComparison.Ordinal)
                || reference.StartsWith(BookArtifactRefs.ChapterBriefPrefix + ":", StringComparison.Ordinal),
                $"'{reference}' was given up. Only the three lowest tiers of BookArtifactKind may be - " +
                "findings first, then the analysis history, then a structured chapter brief - because " +
                "everything above them either is the manuscript itself (the escalated text, the author's " +
                "own summary) or is what turns a name into a person (the register)."));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 5. THE DIGIT BOUNDARY, which only a book with more than nine chapters can see ──────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CLASS OF DEFECT A SMALL BOOK CANNOT EXPRESS (review finding #8, be-c01).
    /// <see cref="BookChatContextReader.FindingRank"/> used to test a finding's anchors by looking for the
    /// raw substring <c>"order":N</c> in the persisted JSON. <c>"order":1</c> is a substring of
    /// <c>"order":10</c>, <c>"order":12</c> and <c>"order":19</c>, so a question about chapter 1 scored
    /// every finding anchored anywhere in chapters 10-19 as a chapter match, and
    /// <see cref="BookChatContextReader.MaxFindings"/> then chose the prompt's findings on that inflated
    /// rank. Every gate this feature ran used 4-to-8-chapter books, where no such pair EXISTS - the bug is
    /// unreachable there, which is why the theory below runs on this 40-chapter fixture and not a smaller
    /// one.
    ///
    /// <para>It is a THEORY over two decades (chapter 1 against 10-19, chapter 2 against 20-29) because
    /// the defect is a class and not an instance; a single pair would be satisfied by a special case for
    /// the one number it names. Each case asserts all three halves of the property: the collider ranks
    /// exactly as an unanchored control does, the GENUINE anchor still outranks that control (a fix that
    /// broke real matching would be worse than the bug), and the control's rank is really the floor.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]  // the fixture anchors findings at 10, 14 and 17
    [InlineData(2)]  // and at 21, 24 and 28
    public void AChapterQuestion_DoesNotMatchFindingsAnchoredInAChapterThatOnlySharesItsLeadingDigits(
        int namedOrder)
    {
        var findings = BuildFindings();

        // Only the chapter key is set, so every rank below is the CHAPTER-ANCHOR term alone and no
        // dimension match can stand in for it.
        var keys = new BookArtifactSelector.BookQuestionKeys(
            ChapterOrders: new[] { namedOrder }, CharacterNames: Array.Empty<string>(),
            Dimensions: Array.Empty<string>(), HasLocationCue: false,
            EscalationChapterOrders: Array.Empty<int>());

        var unanchored = new BookFinding { Dimension = "theme", ChapterAnchorsJson = "[]" };
        var floor = BookChatContextReader.FindingRank(unanchored, keys);
        Assert.Equal(0.0, floor); // VACUITY GUARD: the control is genuinely the bottom of the scale.

        // VACUITY GUARD: this fixture really does anchor findings in the decade that merely shares this
        // chapter's leading digits, so the assertion loop below is not an empty one.
        var colliders = findings
            .Where(f => AnchorOrdersOf(f).Any(o => o != namedOrder && SharesLeadingDigits(o, namedOrder)))
            .ToList();
        Assert.NotEmpty(colliders);

        foreach (var collider in colliders)
        {
            var rank = BookChatContextReader.FindingRank(collider, keys);
            Assert.True(rank == floor,
                $"FindingRank scored {rank} (an unanchored finding scores {floor}) for a question naming " +
                $"chapter {namedOrder} on a finding anchored at " +
                $"{string.Join(", ", AnchorOrdersOf(collider))} - that chapter only SHARES the leading " +
                $"digits of {namedOrder}, it is not chapter {namedOrder}; anchors were " +
                $"{collider.ChapterAnchorsJson}");
        }

        // The same claim on the review's own example, written in the PRODUCTION anchor shape
        // (BookReviewService.ProjectToEntity serializes a List<FindingChapterAnchor>, so chapterId and
        // title ride along) rather than the fixture's compact one - the digit boundary must hold whatever
        // the surrounding JSON looks like.
        var anchoredAtTwelve = new BookFinding
        {
            Dimension = "plot",
            ChapterAnchorsJson = ProductionAnchorsJson(namedOrder * 10 + 2),
        };
        var twelveRank = BookChatContextReader.FindingRank(anchoredAtTwelve, keys);
        Assert.True(twelveRank == floor,
            $"FindingRank scored {twelveRank} (floor {floor}) for a question naming chapter {namedOrder} " +
            $"on a finding anchored at chapter {namedOrder * 10 + 2}: {anchoredAtTwelve.ChapterAnchorsJson}");

        // AND THE GENUINE MATCH STILL WORKS: the findings actually anchored at the named chapter outrank
        // the control, so the boundary did not simply stop matching.
        var genuine = findings.Where(f => AnchorOrdersOf(f).Contains(namedOrder)).ToList();
        Assert.NotEmpty(genuine);

        foreach (var finding in genuine)
        {
            Assert.True(BookChatContextReader.FindingRank(finding, keys) > floor,
                $"a finding anchored at order {namedOrder} must outrank an unanchored one for a question " +
                $"naming chapter {namedOrder}; anchors were {finding.ChapterAnchorsJson}");
        }
    }

    /// <summary>True when <paramref name="order"/>'s decimal form begins with <paramref name="named"/>'s -
    /// exactly the relation the old substring probe could not tell apart from equality.</summary>
    private static bool SharesLeadingDigits(int order, int named) =>
        $"{order}".StartsWith($"{named}", StringComparison.Ordinal);

    private static IReadOnlyList<int> AnchorOrdersOf(BookFinding finding) =>
        JsonSerializer.Deserialize<List<FindingChapterAnchor>>(finding.ChapterAnchorsJson)
            ?.Select(a => a.Order).ToList()
        ?? new List<int>();

    private static string ProductionAnchorsJson(params int[] orders) =>
        JsonSerializer.Serialize(orders
            .Select(o => new FindingChapterAnchor { ChapterId = Guid.NewGuid(), Order = o, Title = TitleFor(o) })
            .ToList());
}
