using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Phase B's ASSEMBLY: the priority order the trimmer gives things up in, the citation set it derives
/// from what survived, and the tutoring shape the statuses produce (chatbot phase B, c1; d1 sections
/// (2), (3) and (6)).
///
/// <para>THE FAILURE THIS FILE DEFENDS is the one phase A's latent-budget finding named: a trim that
/// drops the GROUNDING while keeping the CITATION. Ollama truncates from the START, where the context
/// sits, so an over-budget prompt does not fail - it returns a confident answer carrying chips that
/// point at artifacts the model never saw. The defence is structural rather than careful: the
/// acceptable citation set is COMPUTED from the surviving blocks, so there is no code path on which the
/// two can disagree, and these tests pin that.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE.</para>
/// </summary>
public class ProductChatBookAssemblyTests
{
    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private static GuideDocument Guide(string id)
        => new(id, "stage", "author", "2026-01-01", "en", $"50-{id}.en.md", 50,
               new[] { "# " + id }, new string('g', 400));

    /// <summary>A block with a real, non-trivial cost, so a budget test cannot pass by having nothing
    /// to trim.</summary>
    private static BookArtifactBlock Block(
        BookArtifactKind kind, string reference, double rank = 0, int bodyChars = 400)
        => new(kind, new[] { reference },
               $"=== ARTIFACT ref={reference} ===\n" + new string('b', bodyChars), rank);

    private static IReadOnlyList<BookArtifactBlock> FullSpread() => new[]
    {
        Block(BookArtifactKind.Status, "status:summary"),
        Block(BookArtifactKind.BookBrief, "book-brief"),
        Block(BookArtifactKind.ChapterText, "chapter-text:7", rank: 93),
        Block(BookArtifactKind.Register, "register"),
        Block(BookArtifactKind.ChapterBrief, "chapter-brief:3", rank: 100),
        Block(BookArtifactKind.ChapterBrief, "chapter-brief:9", rank: 10),
        Block(BookArtifactKind.History, "history"),
        Block(BookArtifactKind.Finding, "finding:strong", rank: 10),
        Block(BookArtifactKind.Finding, "finding:weak", rank: 0)
    };

    // ─── The priority order, read off a single maximally-starved composition ────────────────────

    /// <summary>
    /// THE WHOLE DROP ORDER IN ONE ASSERTION. Starved to an impossible budget, the trimmer gives
    /// everything up that it is allowed to give up, in order, and <c>DroppedBookRefs</c> is that order
    /// as a list. Reading it this way rather than through a dozen budget-tuned cases means the test
    /// cannot be satisfied by a fixture that merely happened to fit.
    ///
    /// <para>The order is d1's: findings (weakest first), then the analysis-history metadata, then
    /// chapter briefs (weakest first), then the register, then - after the guides have been given up
    /// down to the floor, which is asserted separately below - the escalated raw chapter text, and only
    /// then the book-level brief. The statuses appear NOWHERE in the dropped list at any pressure.</para>
    /// </summary>
    [Fact]
    public void UnderImpossiblePressure_TheDropOrderIsExactlyD1s()
    {
        var composed = ProductChatBudget.Compose(
            "en",
            new[] { Guide("export"), Guide("faq") },
            new[] { new ProductChatTurn(true, "one"), new ProductChatTurn(false, "two") },
            "question",
            budgetTokens: 1,
            FullSpread(),
            "A Book");

        Assert.Equal(
            new[]
            {
                "finding:weak",
                "finding:strong",
                "history",
                "chapter-brief:9",
                "chapter-brief:3",
                "register",
                "chapter-text:7",
                "book-brief"
            },
            composed.DroppedBookRefs);

        // The statuses survived the maximum pressure the trimmer can apply.
        Assert.Equal(new[] { "status:summary" }, composed.BookBlocks.SelectMany(b => b.References));
        Assert.Equal(2, composed.DroppedTurns);
        Assert.True(composed.StillOverBudget, "a 1-token budget cannot be met; the caller must be told so");
    }

    /// <summary>
    /// GUIDES SIT BETWEEN THE REGISTER AND THE ESCALATED TEXT, which is d1's placement and the only
    /// part of the order the drop list above cannot show (guides are not book artifacts). Read here as
    /// the state at the moment the register is gone but the chapter text is not: the guides must be down
    /// to the floor by then, and not before.
    /// </summary>
    [Fact]
    public void TheGuides_AreGivenUpAfterTheRegisterAndBeforeTheEscalatedText()
    {
        var blocks = FullSpread();
        var guides = new[] { Guide("export"), Guide("faq"), Guide("passes"), Guide("index") };

        // Walk the budget down and capture the first composition in which the register is gone.
        ProductChatBudget.Composition? atRegisterLoss = null;
        for (var budget = 4_000; budget >= 1; budget -= 25)
        {
            var c = ProductChatBudget.Compose("en", guides, Array.Empty<ProductChatTurn>(), "q", budget, blocks, "B");
            if (c.DroppedBookRefs.Contains("register"))
            {
                atRegisterLoss = c;
                break;
            }
        }

        Assert.NotNull(atRegisterLoss);

        // At that moment the escalated text and the book brief are STILL there...
        Assert.Contains("chapter-text:7", atRegisterLoss!.BookBlocks.SelectMany(b => b.References));
        Assert.Contains("book-brief", atRegisterLoss.BookBlocks.SelectMany(b => b.References));

        // ...and the guides have not yet been touched, because the register outranks them.
        Assert.Empty(atRegisterLoss.DroppedGuideIds);

        // VACUITY GUARD: guides ARE droppable on this fixture, just later. Squeezing further reaches them.
        var starved = ProductChatBudget.Compose(
            "en", guides, Array.Empty<ProductChatTurn>(), "q", 1, blocks, "B");
        Assert.NotEmpty(starved.DroppedGuideIds);
        Assert.Single(starved.Guides);
    }

    /// <summary>
    /// ESCALATION NEVER EVICTS THE STATUSES OR THE BOOK BRIEF, and it is the ORDERING that guarantees
    /// it, not a special case. A chapter's raw text is by far the largest block in the assembly, so this
    /// is the case where a naive "drop the biggest thing" trimmer would take the backbone out.
    /// </summary>
    [Fact]
    public void EscalatedText_NeverEvictsTheStatusesOrTheBookBrief()
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.Status, "status:summary", bodyChars: 300),
            Block(BookArtifactKind.BookBrief, "book-brief", bodyChars: 300),
            // A whole chapter at the escalation slice's own ceiling.
            Block(BookArtifactKind.ChapterText, "chapter-text:7", rank: 93, bodyChars: 12_000),
            Block(BookArtifactKind.ChapterBrief, "chapter-brief:3", rank: 100)
        };

        // Swept across the WHOLE budget range rather than pinned at one number, because the property is
        // an ordering invariant and a single budget only ever samples one point on it.
        var sawTextSurviveUnderPressure = false;
        var sawAnyDrop = false;

        for (var budget = 8_000; budget >= 1; budget -= 50)
        {
            var composed = ProductChatBudget.Compose(
                "en", new[] { Guide("export") }, Array.Empty<ProductChatTurn>(), "q", budget, blocks, "B");

            var kept = composed.BookBlocks.SelectMany(b => b.References).ToList();

            Assert.Contains("status:summary", kept);   // never dropped, at any pressure

            if (composed.DroppedBookRefs.Count > 0) sawAnyDrop = true;

            if (kept.Contains("chapter-text:7"))
            {
                // The escalated text survived, so the backbone above it must have survived too.
                Assert.Contains("book-brief", kept);
                if (composed.DroppedBookRefs.Count > 0) sawTextSurviveUnderPressure = true;
            }
        }

        // VACUITY GUARDS: the sweep really did exercise both halves of the invariant.
        Assert.True(sawAnyDrop, "no budget in the sweep forced a drop; the fixture never reached the trimmer");
        Assert.True(sawTextSurviveUnderPressure,
            "no budget in the sweep had the escalated text surviving WHILE something else was dropped, " +
            "so the ordering assertion never actually fired");
    }

    // ─── The citation set is derived from the SURVIVORS ─────────────────────────────────────────

    /// <summary>
    /// A DROPPED ARTIFACT TAKES ITS CITATION WITH IT. This is the exact failure phase A's latent-budget
    /// finding warned about, expressed at the seam where it would happen.
    /// </summary>
    [Fact]
    public void ADroppedArtifactsRef_IsNotAcceptableForCitation()
    {
        var composed = ProductChatBudget.Compose(
            "en", new[] { Guide("export") }, Array.Empty<ProductChatTurn>(), "q",
            budgetTokens: 1, FullSpread(), "B");

        Assert.DoesNotContain("chapter-brief:3", composed.AcceptableReferences);
        Assert.DoesNotContain("finding:strong", composed.AcceptableReferences);

        // VACUITY GUARD: at a generous budget those very refs ARE acceptable, so the exclusion above is
        // the trim and not a reference list that is empty for some unrelated reason.
        var roomy = ProductChatBudget.Compose(
            "en", new[] { Guide("export") }, Array.Empty<ProductChatTurn>(), "q",
            budgetTokens: 100_000, FullSpread(), "B");
        Assert.Contains("chapter-brief:3", roomy.AcceptableReferences);
        Assert.Contains("finding:strong", roomy.AcceptableReferences);
        Assert.Contains("export", roomy.AcceptableReferences);
    }

    /// <summary>
    /// The citation parser ROUND-TRIPS book-artifact refs alongside guide ids, in both the whole-line
    /// and the inline-trailing shapes phase A supports, and <c>chapter-text:&lt;n&gt;</c> specifically -
    /// the ref that licenses a raw-text answer, and therefore the one whose loss would silently downgrade
    /// a verbatim answer to an unattributed one.
    /// </summary>
    [Theory]
    [InlineData("Miriam lights the lamp.\nGuides: chapter-text:7, export")]
    [InlineData("Miriam lights the lamp. (Guides: chapter-text:7, export)")]
    [InlineData("Miriam lights the lamp.\n**Guides:** chapter-text:7, export")]
    public void ArtifactRefs_RoundTripThroughTheCitationParser(string answer)
    {
        var acceptable = new[] { "export", "chapter-text:7", "status:review" };

        var (prose, refs) = ProductChatCitations.Extract(answer, acceptable);

        Assert.Equal(new[] { "export", "chapter-text:7" }, refs);
        Assert.DoesNotContain("Guides:", prose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Miriam lights the lamp.", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every ref SHAPE the assembler emits is classified as an artifact ref and not as a guide id, so
    /// the response's two lists cannot cross-contaminate. The bare-word refs are the trap here:
    /// <c>register</c>, <c>history</c> and <c>book-brief</c> carry no colon, so a prefix-only test would
    /// silently file them under guide ids and the client would render them as guide chips.
    /// </summary>
    [Fact]
    public void EveryEmittedRefShape_IsClassifiedAsAnArtifactRef()
    {
        var refs = new[]
        {
            BookArtifactRefs.Register,
            BookArtifactRefs.History,
            BookArtifactRefs.BookBrief,
            BookArtifactRefs.StatusSummary,
            BookArtifactRefs.StatusReview,
            BookArtifactRefs.StatusStyleBaseline,
            BookArtifactRefs.ChapterBrief(7),
            BookArtifactRefs.ChapterSummary(7),
            BookArtifactRefs.ChapterText(7),
            BookArtifactRefs.Finding(Guid.NewGuid())
        };

        Assert.All(refs, r => Assert.True(
            BookArtifactRefs.LooksLikeArtifactRef(r), $"'{r}' must classify as a book-artifact ref"));

        // VACUITY GUARD: real guide ids must NOT classify as artifact refs, or the test above would pass
        // for a predicate that simply returns true.
        Assert.All(new[] { "export", "faq", "guides-index", "chapter-editing-passes" },
            id => Assert.False(BookArtifactRefs.LooksLikeArtifactRef(id),
                $"guide id '{id}' must not be mistaken for a book-artifact ref"));
    }

    // ─── The statuses: the tutoring shape ───────────────────────────────────────────────────────

    private static BookSummaryStatus Summary(
        int total = 10, int built = 10, int stale = 0, bool hasSummary = true, bool covers = true)
        => new()
        {
            TotalChapters = total, BuiltChapters = built, StaleCount = stale,
            HasSummary = hasSummary, SummaryCoversBuiltChapters = covers, Language = "he"
        };

    private static BookReviewStatus Review(
        bool hasBriefs = true, bool hasReview = true, bool stale = false,
        int findings = 9, int open = 4, int resolved = 3)
        => new()
        {
            HasBriefs = hasBriefs, HasReview = hasReview, StaleVsBriefs = stale,
            FindingCount = findings, OpenFindingCount = open, ResolvedFindingCount = resolved,
            ChaptersReviewed = 10, ChaptersTotal = 10, Language = "he"
        };

    private static BookStyleBaselineStatus Baseline(bool has = true, int stale = 0)
        => new() { TotalChapters = 10, BuiltChapters = 10 - stale, StaleCount = stale, HasBaseline = has };

    /// <summary>
    /// NOTHING BUILT YET is a TUTORING answer, not a refusal: the block says which stage is not built,
    /// which is what lets the model answer "build the briefs first" instead of "I cannot answer that".
    /// </summary>
    [Fact]
    public void WithNothingBuilt_TheStatusBlock_SaysWhichStageIsNotBuilt()
    {
        var block = BookArtifactBlocks.Statuses(
            Summary(built: 0, stale: 10, hasSummary: false, covers: false),
            Review(hasBriefs: false, hasReview: false, findings: 0, open: 0, resolved: 0),
            Baseline(has: false, stale: 10));

        Assert.Contains("status:summary (Book briefs) - ", block.Text, StringComparison.Ordinal);
        Assert.Contains("state: not built yet", block.Text, StringComparison.Ordinal);
        Assert.Contains("BLOCKED: the book briefs are not built, and the review reads them",
            block.Text, StringComparison.Ordinal);
        Assert.Equal(BookArtifactKind.Status, block.Kind);
    }

    /// <summary>
    /// BEHIND BY N names the magnitude AND the reason, because "your briefs are behind by 3 chapters,
    /// rebuild before trusting this" is a first-class answer and it is not sayable from a boolean.
    ///
    /// <para>THE REASON IS ITS OWN NAMED FIELD since f2 (g1 F-6). It used to be a parenthetical at the end
    /// of a compound line, and "why is my review out of date" was 3/3 in English and 0/6 in Hebrew on the
    /// same book, reciting the guides' generic causes instead of the one written right here. Same strings,
    /// same conditions, addressable shape.</para>
    /// </summary>
    [Fact]
    public void WhenTheBriefsAreBehind_TheStatusBlock_NamesTheCountAndTheReason()
    {
        var block = BookArtifactBlocks.Statuses(
            Summary(total: 10, built: 7, stale: 3), Review(stale: true), Baseline());

        Assert.Contains("missing or out of date: 3", block.Text, StringComparison.Ordinal);
        Assert.Contains("BEHIND; reason: 3 chapter brief(s) missing or out of date",
            block.Text, StringComparison.Ordinal);
        Assert.Contains("BEHIND; reason: the briefs were rebuilt after this review",
            block.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The findings are rendered as THREE buckets. <c>FindingCount - Resolved - Open</c> is the
    /// acknowledged bucket and is NOT zero, so a two-bucket sentence would be a WRONG status assertion -
    /// which this phase's gate counts as fabrication rather than as a rough edge.
    /// </summary>
    [Fact]
    public void TheFindingCounts_AreRenderedAsThreeBuckets()
    {
        var block = BookArtifactBlocks.Statuses(Summary(), Review(findings: 9, open: 4, resolved: 3), Baseline());

        Assert.Contains("findings: 9 total, 4 untouched, 2 acknowledged, 3 resolved",
            block.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO MODEL IDENTIFIERS AND NO BUILD-TIME-ONLY REVIEW FIELDS reach the prompt. The status DTOs drop
    /// the model names deliberately ("only the VERDICT crosses the wire") and this prompt reaches a model
    /// whose answer reaches the user, so it is the same boundary. The window/synthesis fields are 0/false
    /// on the STATUS probe by construction, so rendering them would hand the model a factual-looking
    /// "0 windows, synthesis did not run" that is purely an artifact of which probe ran.
    /// </summary>
    [Fact]
    public void TheStatusBlock_LeaksNoModelIdAndNoBuildTimeOnlyField()
    {
        var block = BookArtifactBlocks.Statuses(
            new BookSummaryStatus
            {
                TotalChapters = 10, BuiltChapters = 10, HasSummary = true, SummaryCoversBuiltChapters = true,
                BuiltWithModel = "gemma4:12b", ActiveModel = "qwen3.5:9b"
            },
            new BookReviewStatus
            {
                HasBriefs = true, HasReview = true, ChaptersReviewed = 10, ChaptersTotal = 10,
                BuiltWithModel = "dicta-lm-3.0", ActiveModel = "gemma4:12b",
                WindowCount = 0, RanSynthesis = false, RanContinuityReduce = false, FailedWindows = 0
            },
            new BookStyleBaselineStatus { BuiltWithModel = "nemotron-12b", ActiveModel = "gemma4:12b", HasBaseline = true });

        foreach (var modelId in new[] { "gemma4", "qwen3.5", "dicta-lm", "nemotron" })
        {
            Assert.DoesNotContain(modelId, block.Text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var buildOnly in new[] { "window", "synthesis", "continuity reduce", "failed window" })
        {
            Assert.DoesNotContain(buildOnly, block.Text, StringComparison.OrdinalIgnoreCase);
        }

        // VACUITY GUARD: the block is not empty - it rendered the three stages it is supposed to.
        Assert.Contains("status:summary", block.Text, StringComparison.Ordinal);
        Assert.Contains("status:review", block.Text, StringComparison.Ordinal);
        Assert.Contains("status:style-baseline", block.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RE-MEASUREMENT d1 asked for, now that a renderer exists to measure. d1 ESTIMATED the three
    /// status DTOs at ~200-350 tokens with nothing to measure against; the real renderer, under the
    /// shared estimator, in the WORST shape (a 40-chapter book with every stage behind, every reason
    /// set, a build running and a last-built stamp on all three, so every conditional clause fires at
    /// once) measures <b>193 tokens</b> on 2026-08-12 - inside d1's range and at its lower end.
    ///
    /// <para>It is ASSERTED rather than printed because these blocks are declared never-droppable: a
    /// renderer that quietly grew would eat straight into the budget d1 declared non-negotiable, and
    /// nothing else in the system would notice.</para>
    /// </summary>
    [Fact]
    public void TheStatusBlock_StaysWithinTheBudgetItWasSizedAgainst()
    {
        var block = BookArtifactBlocks.Statuses(
            new BookSummaryStatus
            {
                TotalChapters = 40, BuiltChapters = 12, StaleCount = 28, HasSummary = true,
                SummaryCoversBuiltChapters = false, BuiltWithDifferentModel = true,
                ActiveBuildJobId = Guid.NewGuid(), LastUpdatedAt = DateTimeOffset.UtcNow
            },
            new BookReviewStatus
            {
                HasBriefs = true, HasReview = true, StaleVsBriefs = true, BuiltWithDifferentModel = true,
                FindingCount = 68, OpenFindingCount = 40, ResolvedFindingCount = 20,
                ChaptersReviewed = 40, ChaptersTotal = 40,
                ActiveBuildJobId = Guid.NewGuid(), LastUpdatedAt = DateTimeOffset.UtcNow
            },
            new BookStyleBaselineStatus
            {
                TotalChapters = 40, BuiltChapters = 11, StaleCount = 29, HasBaseline = true,
                BuiltWithDifferentModel = true, ActiveBuildJobId = Guid.NewGuid(),
                LastUpdatedAt = DateTimeOffset.UtcNow
            });

        var tokens = ProductChatBudget.EstimateTokens(block.Text);

        Assert.True(tokens <= 350,
            $"the never-droppable status block estimated {tokens} tokens against d1's 200-350 ceiling. " +
            "Growing it silently spends the budget d1 declared non-negotiable.");

        // VACUITY GUARD: it is not trivially small either - it really did render three populated stages.
        Assert.True(tokens >= 150, $"the status block estimated only {tokens} tokens; it rendered too little. Measured on 2026-08-12: 193.");
    }

    /// <summary>An unreadable status renders as UNREADABLE, never as a state. A status the assistant
    /// states wrongly is counted as fabrication for this phase's gate, so "could not be read" has to be
    /// a thing the block can say.</summary>
    [Fact]
    public void AnUnreadableStatus_RendersAsUnreadable_NeverAsAState()
    {
        var block = BookArtifactBlocks.Statuses(null, null, null);

        Assert.Contains("status:summary (Book briefs) - could not be read.", block.Text, StringComparison.Ordinal);
        Assert.Contains("status:review (Developmental review) - could not be read.", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("not built yet", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("up to date", block.Text, StringComparison.Ordinal);
    }

    // ─── review finding #2 / its missing pin #13: every block's header must carry refs the parser
    //     actually accepts ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GENERALIZED ACROSS EVERY BLOCK KIND, not status-only (review finding #2, P1, and its missing pin,
    /// finding #13, P3). The status block's header used to read <c>=== ARTIFACT ref=status ===</c> - a
    /// token <see cref="BookArtifactRefs.LooksLikeArtifactRef"/> REJECTS (no colon, and the keyless
    /// allowlist is only register/history/book-brief) while the block's real citable refs are the three
    /// <c>status:&lt;key&gt;</c> values. So on the ONE block that is never dropped at any budget
    /// pressure, the header silently contradicted the prompt's own citation instruction ("name a book
    /// artifact by the ref in its own header", <see cref="ProductChatPrompt"/>'s book-aware citation
    /// clause), and g2 measured the model writing the unparseable placeholder back verbatim
    /// ("Sources: ref=status", 5 of 12).
    ///
    /// <para>THE TOKENS ARE DERIVED FROM THE RENDERED TEXT, never hand-listed, so a future block kind
    /// that repeats this exact mistake fails HERE instead of shipping invisibly. That is precisely how
    /// this one shipped invisibly: <c>ProductChatComposedSystemSlotTests</c> hand-writes
    /// <c>=== ARTIFACT ref=status ===</c> as a fixture while <c>ProductChatAmbientWireTests</c>
    /// hand-writes <c>=== ARTIFACT ref=status:summary ===</c> as a fixture, the two disagree, and
    /// neither calls the real renderer - so neither could ever go red. This test calls the real
    /// <see cref="BookArtifactBlocks"/> methods for every kind and checks the ACTUAL rendered header
    /// against <see cref="BookArtifactRefs.LooksLikeArtifactRef"/> itself, never a restatement of it.</para>
    /// </summary>
    [Fact]
    public void EveryBlockKind_HeaderNamesOnlyRefsTheParserAccepts_AndTheyAreLicensedByTheBlock()
    {
        var blocks = new List<BookArtifactBlock?>
        {
            BookArtifactBlocks.BookBrief(
                new BookBrief { Genre = "Fantasy", Synopsis = "A quest across the salt flats." },
                "Salt and Rope", maxTokens: 800),
            BookArtifactBlocks.ChapterBrief(
                language: "en",
                new ChapterBrief { Title = "The Departure", Order = 3, Summary = "They leave home." },
                authorSummary: "My own words about chapter 3.", rank: 100),
            BookArtifactBlocks.AuthorSummary(
                language: "en",
                order: 9, title: "The Long Road", authorSummary: "The author's own flat summary.", rank: 10),
            BookArtifactBlocks.ChapterText(
                language: "en", order: 7, title: "The Storm",
                excerpt: new BookChatExcerpts.Excerpt("Once upon a time, rain fell.", IsWholeChapter: true, EstimatedTokens: 8),
                rank: 93),
            BookArtifactBlocks.Finding(
                new BookFinding
                {
                    Id = Guid.NewGuid(), Dimension = "pacing", Verdict = "improve", Severity = 2,
                    Rationale = "The middle act drags.", Status = "open"
                },
                rank: 10),
            BookArtifactBlocks.Register(new CharacterRegister
            {
                Characters = new[] { new CharacterRegisterEntry { Name = "Dana", Role = "protagonist" } }
            }),
            BookArtifactBlocks.History(new[] { ("proofread", (int?)3, DateTimeOffset.UtcNow) }),
            BookArtifactBlocks.Statuses(Summary(), Review(), Baseline())
        };

        var checkedAnyToken = false;

        foreach (var block in blocks)
        {
            Assert.NotNull(block);

            // DERIVE, do not hand-list: pull every "ref=..." occurrence out of the RENDERED text and
            // keep only the tokens that satisfy the real shape guard. A block can carry more than one
            // header (ChapterBrief embeds a nested ChapterSummary header when the author edited it), so
            // this scans every line rather than just the first.
            var derivedRefs = new List<string>();
            foreach (var line in block!.Text.Split('\n'))
            {
                var idx = line.IndexOf("ref=", StringComparison.Ordinal);
                if (idx < 0) continue;

                var segment = line[(idx + "ref=".Length)..];
                var tokens = segment.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                // THE FIRST TOKEN IS ASSERTED STRICTLY, NOT FILTERED (final-r01). The line below keeps
                // only the tokens the parser ACCEPTS, which means a header printing a rejected token
                // BESIDE a valid one passes - and the shipped defect (`ref=status ===`) is exactly a
                // rejected token, in first position, on a block that now prints three. So the position
                // the whole class occupies is asserted rather than filtered, and every block but
                // Statuses carries exactly one ref, which makes first-position the only position.
                //
                // IT CANNOT BE WIDENED TO EVERY TOKEN WITHOUT CHANGING THE HEADER FORMAT, and that is
                // the reason it is not: `Header` renders `=== ARTIFACT ref={refs} {label} ===`, where
                // {label} is free prose that may itself contain a comma ("build state of this book,
                // always current") and the trailing `===` is a token too. There is no delimiter
                // separating the ref list from the label, so "every token after ref= is a ref" is not
                // a decidable claim over the current format. Adding one changes a string the model
                // reads, which is a prompt change and therefore a GPU re-measure, not a test edit.
                Assert.True(tokens.Length > 0, $"{block!.Kind}'s header has 'ref=' with nothing after it:\n{line}");
                Assert.True(
                    BookArtifactRefs.LooksLikeArtifactRef(tokens[0]),
                    $"{block!.Kind}'s header names '{tokens[0]}' where a ref belongs, and " +
                    $"BookArtifactRefs.LooksLikeArtifactRef REJECTS it - so the prompt's own citation " +
                    $"instruction (\"name a book artifact by the ref in its own header\") points the " +
                    $"model at a token nothing can parse. Rendered line:\n{line}");

                derivedRefs.AddRange(tokens.Where(BookArtifactRefs.LooksLikeArtifactRef));
            }

            // VACUITY GUARD: a parse that finds nothing must not pass silently - that would be exactly
            // the shape of the two disagreeing fixtures this test replaces.
            Assert.True(derivedRefs.Count > 0,
                $"{block.Kind}'s header printed no token BookArtifactRefs.LooksLikeArtifactRef accepts. " +
                $"Rendered text:\n{block.Text}");

            foreach (var token in derivedRefs)
            {
                Assert.Contains(token, block.References, StringComparer.OrdinalIgnoreCase);
                checkedAnyToken = true;
            }
        }

        Assert.True(checkedAnyToken, "no block produced a ref-shaped token to check - the derivation itself is broken.");
    }
}
