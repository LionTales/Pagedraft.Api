using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The retrieval ranking (chatbot phase A, c1; d1 item 1).
///
/// <para>This is the component most likely to regress silently: a ranking change never fails, it just
/// produces a subtly worse answer. It is also the only part of the feature that can be pinned
/// cheaply, because <see cref="GuideSelector"/> is pure - no model, no clock, no filesystem.</para>
///
/// <para>Two populations again. SYNTHETIC documents drive the rules themselves (weights, the
/// cross-language penalty, the tie-break), because a rule is only really pinned when the fixture is
/// built to isolate it. The REAL corpus then proves the rules produce the right document on the
/// questions an author actually asks, which a synthetic fixture cannot show.</para>
/// </summary>
public class ProductChatSelectorTests
{
    private static GuideDocument Doc(string id, string lang, int prefix, params string[] headings)
        => new(
            Id: id, Stage: id, Audience: "author", Updated: "2026-08-02", Lang: lang,
            FileName: $"{prefix:00}-{id}{(lang == "he" ? ".he" : "")}.md",
            NumericPrefix: prefix, Headings: headings, Body: $"body of {id} ({lang})");

    /// <summary>
    /// Five en/he PAIRS. Deliberately at least N of each language, mirroring the shipped corpus (7
    /// Hebrew guides against N=4): the "never the twin" property is about RANKING, and a fixture with
    /// fewer than N same-language documents would test the filler edge instead (which has its own
    /// test below).
    /// </summary>
    private static GuideDocument[] PairedCorpus() => new[]
    {
        Doc("workflow-overview", "en", 0, "How the work flows"),
        Doc("workflow-overview", "he", 0, "איך העבודה מתקדמת"),
        Doc("import", "en", 10, "Importing a manuscript"),
        Doc("import", "he", 10, "ייבוא כתב יד"),
        Doc("chapter-editing-passes", "en", 30, "The chapter editing passes"),
        Doc("chapter-editing-passes", "he", 30, "מעברי העריכה על פרק"),
        Doc("export", "en", 50, "Exporting your book"),
        Doc("export", "he", 50, "ייצוא הספר"),
        Doc("faq", "en", 90, "Questions the work raises"),
        Doc("faq", "he", 90, "שאלות שהעבודה מעלה")
    };

    // ─── The rules, on synthetic documents ──────────────────────────────────────────────────────

    /// <summary>Headings outrank frontmatter (d1 item 1 step 1): a document whose HEADING matches beats
    /// one whose id/stage matches, which is why the weights are separate constants.</summary>
    [Fact]
    public void AHeadingMatch_OutranksAFrontmatterMatch()
    {
        var corpus = new[]
        {
            Doc("alpha", "en", 10, "Something else entirely"),   // id/stage match only
            Doc("beta", "en", 20, "How alpha works")             // heading match only
        };

        var selected = GuideSelector.Select("alpha", corpus, "en", count: 2);

        Assert.Equal(2, selected.Count);
        Assert.Equal("beta", selected[0].Id);
        Assert.True(GuideSelector.HeadingWeight > GuideSelector.FrontmatterWeight);
    }

    /// <summary>
    /// A HEBREW QUESTION NEVER SELECTS THE ENGLISH TWIN - the property the todo names. Asserted over
    /// the WHOLE selection, not just the winner, and the selection is asserted non-empty first: the
    /// cross-language penalty only decides the top of the ranking, so a filler slot taken by an
    /// English twin would break the property just as badly and is exactly what the language tie-break
    /// exists to prevent.
    /// </summary>
    [Fact]
    public void AHebrewQuestion_SelectsTheHebrewGuide_AndNeverItsEnglishTwin()
    {
        var selected = GuideSelector.Select("איך עושים ייצוא של הספר?", PairedCorpus(), "he", count: 4);

        Assert.NotEmpty(selected);                                   // the population, before any "none is bad"
        Assert.Equal("export", selected[0].Id);
        Assert.Equal("he", selected[0].Lang);
        Assert.All(selected, d => Assert.Equal("he", d.Lang));
    }

    /// <summary>
    /// The mirror image, and it is DELIBERATELY WEAKER, which is worth knowing rather than papering
    /// over. The two directions are not symmetric because the frontmatter <c>id</c>/<c>stage</c> slugs
    /// are ENGLISH on both halves of a pair (<c>50-export.he.md</c> is also <c>id: export</c>): an
    /// English question can therefore reach the Hebrew twin lexically, at the 0.5 penalty, while a
    /// Hebrew question can reach an English twin only through English headings it has no tokens for.
    /// So a Hebrew question never selects the English twin, and an English question can pull the
    /// Hebrew twin into a filler slot ahead of a zero-scoring English guide.
    ///
    /// <para>That is d1's language PREFERENCE working exactly as specified (item 1 step 2: a penalty,
    /// not an exclusion, so a weak cross-language match still outranks a zero same-language match),
    /// not a defect to fix here. What must hold is the part that decides the answer: the English guide
    /// is FIRST and outranks its own twin.</para>
    /// </summary>
    [Fact]
    public void AnEnglishQuestion_RanksTheEnglishGuideFirst_AboveItsHebrewTwin()
    {
        var selected = GuideSelector.Select("how do I export my book", PairedCorpus(), "en", count: 4);

        Assert.NotEmpty(selected);
        Assert.Equal("export", selected[0].Id);
        Assert.Equal("en", selected[0].Lang);

        var english = selected.ToList().FindIndex(d => d.Id == "export" && d.Lang == "en");
        var hebrew = selected.ToList().FindIndex(d => d.Id == "export" && d.Lang == "he");
        Assert.Equal(0, english);
        Assert.True(hebrew < 0 || hebrew > english,
            "the English guide must outrank its Hebrew twin for an English question.");
    }

    /// <summary>
    /// THE HONEST EDGE OF THAT PROPERTY. "Never the twin" holds while there is enough same-language
    /// material to fill N, which the shipped corpus guarantees (7 Hebrew guides against N=4). When it
    /// runs out, d1 item 1 step 5 wins: the selector returns its top N rather than fewer, so the last
    /// slot goes to a cross-language document. Stated as a test rather than left as a surprise,
    /// because it is the behaviour a future corpus (a partially translated guide set) would hit first.
    /// </summary>
    [Fact]
    public void WhenSameLanguageMaterialRunsOut_ACrossLanguageGuideFillsTheSlot_RatherThanReturningFewer()
    {
        var corpus = new[]
        {
            Doc("export", "he", 50, "ייצוא הספר"),
            Doc("import", "he", 10, "ייבוא כתב יד"),
            Doc("faq", "en", 90, "Questions the work raises"),
            Doc("guides-index", "en", 99, "PageDraft guides")
        };

        var selected = GuideSelector.Select("איך עושים ייצוא של הספר?", corpus, "he", count: 4);

        Assert.Equal(4, selected.Count);
        Assert.Equal(new[] { "he", "he", "en", "en" }, selected.Select(d => d.Lang).ToArray());
    }

    /// <summary>
    /// THE PENALTY IS A PREFERENCE, NOT AN EXCLUSION (d1 item 1 step 2 and item 3). An English-only
    /// document - <c>README.md</c> is exactly that, it has no Hebrew sibling at all - must still be
    /// able to win a Hebrew question when it is the only relevant source. Without this, d1's "answer
    /// in Hebrew from the English source" would need a second code path.
    /// </summary>
    [Fact]
    public void ACrossLanguageGuide_StillWins_WhenItIsClearlyTheBestMatch()
    {
        var corpus = new[]
        {
            // English-only, and the only document that says anything about DOCX.
            Doc("guides-index", "en", 99, "DOCX and Syncfusion formats"),
            Doc("faq", "he", 90, "שאלות נפוצות")
        };

        var selected = GuideSelector.Select("מה קורה עם DOCX ועם Syncfusion?", corpus, "he", count: 2);

        Assert.Equal("guides-index", selected[0].Id);
        Assert.Equal("en", selected[0].Lang);
    }

    /// <summary>The penalty's arithmetic, stated as a number so a future weight change is visible.</summary>
    [Fact]
    public void TheCrossLanguagePenalty_HalvesTheScore_AndNothingElse()
    {
        var tokens = GuideSelector.Tokenize("export");
        var doc = Doc("export", "en", 50, "Exporting");

        var same = GuideSelector.Score(tokens, doc, "en");
        var cross = GuideSelector.Score(tokens, doc, "he");

        Assert.True(same > 0, "the fixture must actually match, or the ratio below is 0 == 0.");
        Assert.Equal(same * GuideSelector.CrossLanguagePenalty, cross, precision: 9);
        Assert.Equal(0.5, GuideSelector.CrossLanguagePenalty);
    }

    /// <summary>
    /// TIE-BREAK, deterministic and total. d1 says "numeric prefix", which does not order an en/he
    /// TWIN (both halves of <c>50-export</c> are prefix 50), so the order is: same language first,
    /// then numeric prefix, then filename. Without the language step the twin ordering would fall
    /// through to the filename and a zero-scoring English twin could take a filler slot.
    /// </summary>
    [Fact]
    public void EquallyScoredGuides_AreOrderedByLanguageThenNumericPrefixThenFileName()
    {
        var corpus = new[]
        {
            Doc("zeta", "en", 90),
            Doc("zeta", "he", 90),
            Doc("alpha", "en", 10),
            Doc("alpha", "he", 10),
            Doc("mid", "he", 50)
        };

        // A question that matches nothing: every score is 0, so the ORDER is entirely the tie-break.
        var selected = GuideSelector.Select("qqqq", corpus, "he", count: 5);

        Assert.Equal(
            new[] { "10-alpha.he.md", "50-mid.he.md", "90-zeta.he.md", "10-alpha.md", "90-zeta.md" },
            selected.Select(d => d.FileName).ToArray());
    }

    /// <summary>
    /// THE SELECTOR NEVER DECIDES "NO COVERAGE" (d1 item 1 step 5). On a question that matches nothing
    /// it still returns its top N: judging coverage is the model's job under the grounding rule, and a
    /// selector that refused early would turn a coverage question into a retrieval failure.
    /// </summary>
    [Fact]
    public void AQuestionThatMatchesNothing_StillReturnsN_RatherThanRefusing()
    {
        var corpus = new[] { Doc("a", "he", 10), Doc("b", "he", 20), Doc("c", "he", 30), Doc("d", "he", 40), Doc("e", "he", 50) };

        Assert.Equal(4, GuideSelector.Select("zzzz yyyy", corpus, "he").Count);
    }

    [Fact]
    public void AnEmptyCorpus_SelectsNothing_WithoutThrowing()
        => Assert.Empty(GuideSelector.Select("anything", Array.Empty<GuideDocument>(), "he"));

    [Fact]
    public void ACorpusSmallerThanN_ReturnsAllOfIt()
    {
        var corpus = new[] { Doc("a", "he", 10), Doc("b", "he", 20) };
        Assert.Equal(2, GuideSelector.Select("a", corpus, "he").Count);
    }

    /// <summary>Stop words must not decide a ranking: "how do I get the thing" is a question shape, not
    /// a topic. Pinned because the tokenizer silently dropping the wrong word is invisible otherwise.</summary>
    [Fact]
    public void StopWordsAndSingleCharacters_AreNotTokens()
    {
        var tokens = GuideSelector.Tokenize("How do I get the X for my book?");

        Assert.Contains("book", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("how", tokens);
        Assert.DoesNotContain("my", tokens);
        Assert.DoesNotContain("x", tokens);   // single character
    }

    [Fact]
    public void TokenizationIsCaseFolded_AndSplitsOnPunctuation()
        => Assert.Equal(new[] { "book", "export" }, GuideSelector.Tokenize("Export, BOOK.").OrderBy(t => t, StringComparer.Ordinal).ToArray());

    // ─── The real corpus ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ranking against the guides an author will actually be answered from. Each row states the
    /// question and the guide that must come FIRST; the population is pinned by
    /// <c>LoadRealCorpus</c>, so a corpus that failed to load cannot green this.
    /// </summary>
    [Theory]
    [InlineData("How do I export my book to Word?", "en", "export")]
    [InlineData("How do I import a DOCX manuscript?", "en", "import")]
    [InlineData("What does the whole-book review find?", "en", "whole-book-review")]
    [InlineData("איך מייצאים את הספר?", "he", "export")]
    [InlineData("איך כתב היד מפוצל לפרקים?", "he", "import")]
    public void TheRealCorpus_RanksTheRightGuideFirst(string question, string language, string expectedId)
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();

        var selected = GuideSelector.Select(question, corpus.Documents, language);

        Assert.Equal(GuideSelector.DefaultCount, selected.Count);
        Assert.Equal(expectedId, selected[0].Id);
        Assert.Equal(language, selected[0].Lang);
    }

    // ─── Hebrew inflection tolerance (be-c02) ───────────────────────────────────────────────────

    /// <summary>
    /// THE OWNER'S OWN QUESTION. g2 measured <c>chapter-editing-passes</c> ABSENT from the selection
    /// on all three runs of this exact string, so the guide w3 wrote to answer it was never in the
    /// prompt and the assistant drifted to <c>עריכת שורה</c> - the behaviour the owner originally
    /// reported. None of its three topic tokens reached the guide under exact matching:
    /// <c>ספרותית</c> against the heading <c>## ספרותי</c>, <c>מריץ</c> against <c>## איך מריצים
    /// מעבר</c>, and <c>עריכה</c> against the H1 <c># מעברי העריכה על פרק</c>.
    ///
    /// <para>The POPULATION IS PINNED FIRST, here as well as inside <c>LoadRealCorpus</c>:
    /// <see cref="GuidesCorpusReader"/> returns an empty corpus with a fault when its directory is
    /// missing, and <c>Select</c> over an empty corpus returns an empty list, which would green every
    /// ranking assertion below while proving nothing.</para>
    /// </summary>
    [Fact]
    public void TheOwnersLiteraryPassQuestion_SelectsTheGuideThatAnswersIt_AndRanksItFirst()
    {
        const string ownersQuestion = "איך אני מריץ עריכה ספרותית?";
        var corpus = ProductChatCorpusTests.LoadRealCorpus();

        Assert.NotEmpty(corpus.Documents);                       // the floor, before any ranking claim
        Assert.Equal(15, corpus.Documents.Count);

        var tokens = GuideSelector.Tokenize(ownersQuestion);
        var answering = Assert.Single(corpus.Documents, d => d.Id == "chapter-editing-passes" && d.Lang == "he");

        // The score is what actually moved: it was 0 before this tolerance existed, which is why the
        // filename tie-break pushed the guide out of a 4-slot selection entirely.
        Assert.True(GuideSelector.Score(tokens, answering, "he") > 0,
            "the guide that answers the owner's question must score for it, not reach the prompt by tie-break.");

        var selected = GuideSelector.Select(ownersQuestion, corpus.Documents, "he");

        Assert.Equal(GuideSelector.DefaultCount, selected.Count);
        Assert.Equal("chapter-editing-passes", selected[0].Id);
        Assert.Equal("he", selected[0].Lang);
    }

    /// <summary>
    /// THE REGRESSION BASELINE. Every question g1, g2 and c01 already measured as ranking its
    /// answering guide FIRST must still do so. Loosening a matcher is the silent-regression class this
    /// selector's own docstring warns about, so the proof that it did not regress is a pinned list of
    /// the rankings that were already right, not an argument that they are still plausible.
    ///
    /// <para>Rows: c01's six example chips (measured and recorded in the fix plan's
    /// <c>## Investigation findings</c>), g2's <c>e2</c> English twin and <c>e7</c> Hebrew
    /// own-words rephrasing - the two controls that proved the corpus was fine and retrieval was the
    /// miss - and g1's bucket (a) Hebrew covered questions.</para>
    /// </summary>
    [Theory]
    // c01's six example chips, as they ship in chat-strings.ts
    [InlineData("איך מייבאים כתב יד?", "he", "import")]
    [InlineData("מה נדרש כדי להריץ עריכה התפתחותית על הספר?", "he", "whole-book-review")]
    [InlineData("מהם מעברי העריכה על פרק?", "he", "chapter-editing-passes")]
    [InlineData("How do I import a manuscript?", "en", "import")]
    [InlineData("What does the developmental review need first?", "en", "whole-book-review")]
    [InlineData("Which editing passes does a chapter have?", "en", "chapter-editing-passes")]
    // g2's two controls on the owner's question
    [InlineData("How do I run the Literary pass on a chapter?", "en", "chapter-editing-passes")]
    [InlineData("איך מריצים מעבר על פרק?", "he", "chapter-editing-passes")]
    // g1 bucket (a), the Hebrew half (the English half is covered by the bound test below)
    [InlineData("מה צריך להיות מוכן לפני שאפשר להריץ עריכה התפתחותית?", "he", "whole-book-review")]
    [InlineData("אילו סוגי קבצים אפשר לייבא ל-PageDraft?", "he", "import")]
    public void TheAlreadyCorrectRankings_AreUnchangedByTheInflectionTolerance(
        string question, string language, string expectedFirstId)
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        Assert.NotEmpty(corpus.Documents);

        var selected = GuideSelector.Select(question, corpus.Documents, language);

        Assert.Equal(GuideSelector.DefaultCount, selected.Count);
        Assert.Equal(expectedFirstId, selected[0].Id);
        Assert.Equal(language, selected[0].Lang);
    }

    /// <summary>
    /// BOUND 1, THE ONE THAT PROTECTS EVERY ENGLISH MEASUREMENT: a token that is not all Hebrew
    /// letters has no inflection keys at all, so it can only ever be matched exactly. Every English
    /// question therefore scores exactly as it did before be-c02, against documents in both
    /// languages, and F3 (the wrong-language twin taking one of four slots, measured at an unchanged
    /// 25% of English selections across g1 and g2) cannot move.
    /// </summary>
    [Theory]
    [InlineData("export")]        // Latin
    [InlineData("pagedraft")]
    [InlineData("docx2")]         // digit
    [InlineData("עריכהa")]        // mixed script - one Latin letter is enough to disqualify it
    public void ATokenThatIsNotAllHebrew_HasNoInflectionKeys(string token)
        => Assert.Empty(GuideSelector.InflectionKeys(token));

    /// <summary>
    /// The same bound stated where it actually matters, on the shipped corpus: the two English
    /// selections g2 recorded are pinned WHOLE - every id, its language and its order - so a future
    /// loosening that reached Latin tokens would fail here rather than show up as a subtly worse
    /// English answer. <c>import[he]</c> in the first row IS F3, pinned deliberately: this test
    /// asserts F3 is UNCHANGED, not that it is fixed.
    /// </summary>
    [Fact]
    public void AnEnglishSelection_IsUnchanged_IncludingTheWrongLanguageTwinF3StillTakesASlot()
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        Assert.NotEmpty(corpus.Documents);

        Assert.Equal(
            new[] { "import[en]", "faq[en]", "import[he]", "workflow-overview[en]" },
            GuideSelector.Select("How do I import a manuscript?", corpus.Documents, "en")
                .Select(d => $"{d.Id}[{d.Lang}]").ToArray());

        Assert.Equal(
            new[] { "chapter-editing-passes[en]", "faq[en]", "book-setup-and-intelligence[en]", "chapter-editing-passes[he]" },
            GuideSelector.Select("How do I run the Literary pass on a chapter?", corpus.Documents, "en")
                .Select(d => $"{d.Id}[{d.Lang}]").ToArray());
    }

    /// <summary>
    /// BOUND 1's second half: the tolerance is applied to HEADINGS ONLY, never to the frontmatter
    /// <c>id</c>/<c>stage</c>. Those are English slugs on BOTH halves of an en/he pair and are the
    /// only mechanism by which a question reaches a wrong-language twin, so keeping the tolerance out
    /// of them is an independent guarantee that the cross-language behaviour cannot move.
    /// </summary>
    [Fact]
    public void TheInflectionTolerance_NeverReachesTheFrontmatter_OnlyTheHeadings()
    {
        var frontmatterOnly = new GuideDocument(
            Id: "עריכות", Stage: "עריכות", Audience: "author", Updated: "2026-08-06", Lang: "he",
            FileName: "10-x.he.md", NumericPrefix: 10, Headings: new[] { "משהו אחר לגמרי" }, Body: "b");
        var headingOnly = new GuideDocument(
            Id: "other", Stage: "other", Audience: "author", Updated: "2026-08-06", Lang: "he",
            FileName: "20-y.he.md", NumericPrefix: 20, Headings: new[] { "עריכות של פרק" }, Body: "b");

        var tokens = GuideSelector.Tokenize("עריכה");

        // The heading reaches it at the inflected weight...
        Assert.Equal(GuideSelector.InflectedHeadingWeight, GuideSelector.Score(tokens, headingOnly, "he"), precision: 9);
        // ...the identical string in the frontmatter does not reach it at all.
        Assert.Equal(0.0, GuideSelector.Score(tokens, frontmatterOnly, "he"), precision: 9);
    }

    /// <summary>
    /// BOUND 2: at least <see cref="GuideSelector.MinInflectionStemLength"/> letters shared, at most
    /// <see cref="GuideSelector.MaxInflectionLettersRemoved"/> removed. The MATCHES are the four
    /// shapes the measurements actually hit; the NON-matches are the collisions a naive short-stem
    /// rule would produce, and they are the reason the floor exists.
    /// </summary>
    [Theory]
    // The measured cases: the owner's question, and c01's two chip failures.
    [InlineData("ספרותית", "ספרותי", true)]      // the owner's word against the guide's pass name
    [InlineData("מריץ", "מריצים", true)]          // final-form fold plus a plural suffix
    [InlineData("עריכה", "העריכה", true)]         // the definite article
    [InlineData("עריכה", "עריכת", true)]          // construct state
    [InlineData("מעברי", "מעברים", true)]
    // A token of four letters or fewer is never stripped, so short stems cannot collide.
    [InlineData("הגהה", "הגה", false)]
    [InlineData("שכבה", "כבה", false)]
    [InlineData("הספר", "ספר", false)]
    // This is a single-affix tolerance, not a stemmer: it does not cross binyanim, and it will not
    // strip two prefixes or a prefix and a two-letter suffix together.
    [InlineData("לרוץ", "מריצים", false)]
    [InlineData("ספרותית", "ולספרותי", false)]
    [InlineData("ייצוא", "ייבוא", false)]         // one letter apart in the MIDDLE, never a match
    public void TheInflectionTolerance_MatchesOnlyASharedStemOfAtLeastFourLetters(
        string questionToken, string headingToken, bool shouldMatch)
    {
        var headingStems = GuideSelector.InflectionKeys(headingToken);

        Assert.Equal(shouldMatch, GuideSelector.MatchesByInflection(questionToken, headingStems));
        // ...and symmetrically, since neither side is privileged.
        Assert.Equal(shouldMatch, GuideSelector.MatchesByInflection(headingToken, GuideSelector.InflectionKeys(questionToken)));
    }

    /// <summary>The floor stated as a number, and the fact that it disables stripping entirely for a
    /// short token, which is where the collision risk lives.</summary>
    [Fact]
    public void ATokenAtOrBelowTheStemFloor_HasOnlyItselfAsAKey()
    {
        Assert.Equal(4, GuideSelector.MinInflectionStemLength);
        Assert.Equal(2, GuideSelector.MaxInflectionLettersRemoved);

        Assert.Equal(new[] { "הגהה" }, GuideSelector.InflectionKeys("הגהה").ToArray());
        Assert.Empty(GuideSelector.InflectionKeys("פרק"));    // three letters: below the floor entirely
    }

    /// <summary>
    /// BOUND 3: the tolerance is strictly ADDITIVE and strictly WEAKER. A guide whose heading carries
    /// the author's actual word must still outrank one that carries only a related form, so the
    /// tolerance can add a document to a selection but can never re-order two documents that both
    /// match exactly.
    /// </summary>
    [Fact]
    public void AnExactHeadingMatch_OutranksAnInflectedOne_AndTheyAreNeverBothCounted()
    {
        var exact = Doc("exact", "he", 90, "עריכה של פרק");
        var inflected = Doc("inflected", "he", 10, "העריכה של פרק");

        var selected = GuideSelector.Select("עריכה", new[] { exact, inflected }, "he", count: 2);

        // The inflected document has the LOWER numeric prefix, so if the two scored equally the
        // tie-break would put it first. It does not.
        Assert.Equal("exact", selected[0].Id);
        Assert.True(GuideSelector.HeadingWeight > GuideSelector.InflectedHeadingWeight);
        Assert.True(GuideSelector.InflectedHeadingWeight > GuideSelector.FrontmatterWeight);

        var tokens = GuideSelector.Tokenize("עריכה");
        // A heading carrying BOTH forms is worth one exact match, not an exact plus an inflected one.
        var both = Doc("both", "he", 20, "עריכה של פרק", "העריכה של פרק");
        Assert.Equal(GuideSelector.HeadingWeight, GuideSelector.Score(tokens, both, "he"), precision: 9);
    }

    // ─── Query-side synonym tolerance (A.2, c2) ─────────────────────────────────────────────────

    /// <summary>
    /// THE TABLE IS GROUNDED IN THE SHIPPED HEADINGS. Every expansion TARGET must occur verbatim as a
    /// heading token somewhere in the real corpus, because a target the headings do not carry can never
    /// fire and is therefore a claim about the corpus that nothing keeps true. This is also the test
    /// that catches the corpus moving underneath the table: a copy-edit that renames
    /// <c>## Proofread</c> takes <c>typos -> proofread</c> down with it, silently, and only this fails.
    ///
    /// <para>The heading population is proved non-empty and recognisable FIRST, in both scripts:
    /// <see cref="GuidesCorpusReader"/> returns an empty corpus with a fault when its directory is
    /// missing, and an empty heading set would green the sweep below while proving nothing.</para>
    /// </summary>
    [Fact]
    public void EveryExpansionTarget_OccursVerbatimInAShippedGuideHeading()
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        Assert.NotEmpty(corpus.Documents);

        var headingTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in corpus.Documents)
        foreach (var heading in doc.Headings)
        foreach (var token in GuideSelector.Tokenize(heading))
            headingTokens.Add(token);

        // Non-vacuity, in both scripts, before the "none is bad" sweep.
        Assert.True(headingTokens.Count > 50, $"only {headingTokens.Count} heading tokens: the corpus did not load.");
        Assert.Contains("proofread", headingTokens);
        Assert.Contains("ייבוא", headingTokens);

        var ungrounded = GuideQueryExpansion.Entries
            .SelectMany(entry => entry.Value.Select(target => $"{entry.Key} -> {target}"))
            .Where(pair => !headingTokens.Contains(pair.Split(" -> ")[1]))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Array.Empty<string>(), ungrounded);
    }

    /// <summary>
    /// THE STRUCTURAL BOUNDS, asserted as properties of the table rather than re-listed by hand. Every
    /// key must survive tokenization (a key that is a stop word or a single character could never be
    /// produced by <see cref="GuideSelector.Tokenize"/> and so could never fire), and no entry may
    /// cross scripts - which is what keeps an English question from reaching a Hebrew heading through
    /// this path and leaves the cross-language behaviour g1-g4 measured exactly where it was.
    /// </summary>
    [Fact]
    public void EveryExpansionEntry_KeepsItsKeyTokenizable_AndNeverCrossesScripts()
    {
        Assert.NotEmpty(GuideQueryExpansion.Entries);

        foreach (var (key, targets) in GuideQueryExpansion.Entries)
        {
            Assert.Equal(new[] { key }, GuideSelector.Tokenize(key).ToArray());
            Assert.NotEmpty(targets);

            var keyIsHebrew = GuideQueryExpansion.IsAllHebrew(key);
            foreach (var target in targets)
            {
                Assert.Equal(new[] { target }, GuideSelector.Tokenize(target).ToArray());
                Assert.True(
                    GuideQueryExpansion.IsAllHebrew(target) == keyIsHebrew,
                    $"'{key} -> {target}' crosses scripts. Query expansion must stay inside one script, or an " +
                    "English question gains a path to a Hebrew heading that F3's measurements never saw.");
            }
        }
    }

    /// <summary>
    /// The floor that bounds the clitic-stripped lookup: every HEBREW key is at least
    /// <see cref="GuideSelector.MinInflectionStemLength"/> letters, so stripping one leading clitic can
    /// only ever land on a curated four-letter-or-longer word. Without this a future two-letter key
    /// would start matching half the language.
    /// </summary>
    [Fact]
    public void EveryHebrewExpansionKey_IsAtLeastTheInflectionStemFloor()
    {
        var hebrewKeys = GuideQueryExpansion.Entries.Keys.Where(GuideQueryExpansion.IsAllHebrew).ToArray();

        Assert.NotEmpty(hebrewKeys);
        Assert.All(hebrewKeys, k => Assert.True(
            k.Length >= GuideSelector.MinInflectionStemLength,
            $"Hebrew expansion key '{k}' is shorter than the {GuideSelector.MinInflectionStemLength}-letter floor."));
    }

    /// <summary>
    /// The lookup itself: as typed, and through at most ONE leading Hebrew clitic. Latin tokens are
    /// never stripped, and a token the table does not carry expands to nothing.
    /// </summary>
    [Theory]
    [InlineData("upload", "import")]
    [InlineData("typos", "proofread")]
    [InlineData("קובץ", "קבצים")]
    [InlineData("הקובץ", "קבצים")]        // one definite article
    [InlineData("בסקירה", "התפתחותית")]   // one preposition
    public void Expand_ReachesTheHeadingTerm_AsTypedAndThroughOneHebrewClitic(string token, string expected)
        => Assert.Contains(expected, GuideQueryExpansion.Expand(token));

    [Theory]
    [InlineData("chapter")]          // an ordinary word the table does not carry
    [InlineData("shortcut")]         // deliberately absent: the corpus has no such topic to route to
    [InlineData("קיצור")]
    [InlineData("ההקובץ")]           // two clitics: only one is ever stripped
    public void Expand_ReturnsNothing_ForATokenTheTableDoesNotCarry(string token)
        => Assert.Empty(GuideQueryExpansion.Expand(token));

    /// <summary>
    /// THE PAYLOAD: a paraphrase of the question now reaches the guide that answers it. Every row here
    /// is a wording that ranked the WRONG guide first under exact and inflected matching alone, which
    /// was verified by disabling the tolerance and watching all six go red rather than by reasoning
    /// about the arithmetic. Two earlier candidates ("Where do I upload my manuscript?", "Which pass
    /// catches typos and spelling?") were dropped for failing exactly that check: each carried a second
    /// token that already won the ranking on its own, so they would have greened whether the tolerance
    /// existed or not.
    /// </summary>
    [Theory]
    [InlineData("Where do I upload the finished draft?", "en", "import")]
    [InlineData("How do I fix spelling in what I wrote?", "en", "chapter-editing-passes")]
    [InlineData("How do I download the finished book?", "en", "export")]
    [InlineData("איך עושים העלאה של הקובץ?", "he", "import")]
    [InlineData("מה כוללת הסקירה של הספר?", "he", "whole-book-review")]
    [InlineData("איך מתקנים שגיאות כתיב בפרק?", "he", "chapter-editing-passes")]
    public void AParaphrasedQuestion_NowRanksTheGuideThatAnswersIt_First(
        string question, string language, string expectedId)
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        Assert.NotEmpty(corpus.Documents);

        var tokens = GuideSelector.Tokenize(question);
        var answering = Assert.Single(corpus.Documents, d => d.Id == expectedId && d.Lang == language);
        Assert.True(GuideSelector.Score(tokens, answering, language) > 0,
            "the guide that answers the paraphrase must SCORE for it, not arrive by tie-break.");

        var selected = GuideSelector.Select(question, corpus.Documents, language);

        Assert.Equal(GuideSelector.DefaultCount, selected.Count);
        Assert.Equal(expectedId, selected[0].Id);
        Assert.Equal(language, selected[0].Lang);
    }

    /// <summary>
    /// THE REGRESSION FLOOR FOR THIS CHANGE, and the reason it is stated as WHOLE selections: the two
    /// English selections g2 pinned and two Hebrew ones are asserted id-by-id and in order, so a
    /// synonym that displaced a guide would fail here rather than surface as a subtly worse answer.
    /// The Hebrew rows deliberately include a question whose tokens DO hit the table
    /// (<c>לייבא -> ייבוא</c>), so what is pinned is the post-change selection of an already-correct
    /// question, not merely the selections the table cannot touch.
    ///
    /// <para>The English rows are the same two <see cref="AnEnglishSelection_IsUnchanged_IncludingTheWrongLanguageTwinF3StillTakesASlot"/>
    /// pins, restated here on purpose: that test's docstring is about the INFLECTION bound, and a
    /// reader deleting this tolerance should not have to infer that it also guarded this one.</para>
    /// </summary>
    [Theory]
    [InlineData("How do I import a manuscript?", "en",
        "import[en]|faq[en]|import[he]|workflow-overview[en]")]
    [InlineData("How do I run the Literary pass on a chapter?", "en",
        "chapter-editing-passes[en]|faq[en]|book-setup-and-intelligence[en]|chapter-editing-passes[he]")]
    // The Latin token "pagedraft" matches book-setup-and-intelligence[en]'s H1 exactly and takes the
    // last slot at the cross-language penalty. That is F3 in Hebrew, it predates this tolerance (which
    // cannot touch it: the table has no Latin key for it), and it is pinned rather than hidden.
    [InlineData("אילו סוגי קבצים אפשר לייבא ל-PageDraft?", "he",
        "import[he]|book-setup-and-intelligence[he]|faq[he]|book-setup-and-intelligence[en]")]
    [InlineData("איך מריצים מעבר על פרק?", "he",
        "chapter-editing-passes[he]|faq[he]|book-setup-and-intelligence[he]|workflow-overview[he]")]
    // g3/g4's adjacent-bucket `d4`. It is here because it is the ONE question in the measured gate set
    // whose tokens reach this table at all: "לקובץ" strips its clitic to the key "קובץ". A change to
    // what the model is shown on an ADJACENT question is the change most able to move the gate, so it
    // is pinned rather than left for g5 to discover.
    [InlineData("איך אני מייצא את הספר שלי לקובץ EPUB?", "he",
        "export[he]|book-setup-and-intelligence[he]|whole-book-review[he]|faq[he]")]
    public void TheseWholeSelections_AreUnchangedByTheSynonymTolerance(
        string question, string language, string expected)
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        Assert.NotEmpty(corpus.Documents);

        Assert.Equal(
            expected.Split('|'),
            GuideSelector.Select(question, corpus.Documents, language)
                .Select(d => $"{d.Id}[{d.Lang}]").ToArray());
    }

    /// <summary>
    /// BOUND 3 (strictly weakest, never double-counted), the synonym twin of
    /// <see cref="AnExactHeadingMatch_OutranksAnInflectedOne_AndTheyAreNeverBothCounted"/>. A guide
    /// whose heading carries the author's own word must outrank one reachable only by synonym, even
    /// when the tie-break would have favoured the other.
    /// </summary>
    [Fact]
    public void AnExactHeadingMatch_OutranksASynonymOne_AndTheyAreNeverBothCounted()
    {
        var exact = Doc("exact", "en", 90, "Where to start");
        var synonym = Doc("synonym", "en", 10, "How to run a pass");

        var selected = GuideSelector.Select("start", new[] { exact, synonym }, "en", count: 2);

        // `synonym` has the LOWER numeric prefix, so an equal score would put it first. It does not.
        Assert.Equal("exact", selected[0].Id);
        Assert.True(GuideSelector.InflectedHeadingWeight > GuideSelector.SynonymHeadingWeight);
        Assert.True(GuideSelector.SynonymHeadingWeight > GuideSelector.FrontmatterWeight);

        // A heading carrying BOTH the word and its synonym is worth one exact match, not both.
        var both = Doc("both", "en", 20, "Where to start", "How to run a pass");
        Assert.Equal(GuideSelector.HeadingWeight, GuideSelector.Score(GuideSelector.Tokenize("start"), both, "en"),
            precision: 9);
    }

    /// <summary>
    /// The expansion reaches HEADINGS ONLY, never the frontmatter <c>id</c>/<c>stage</c>. Those slugs
    /// are English on BOTH halves of an en/he pair and are the only mechanism by which a question
    /// reaches a wrong-language twin, so keeping the table out of them is an independent guarantee that
    /// the cross-language behaviour cannot move - the same guarantee be-c02's tolerance carries.
    /// </summary>
    [Fact]
    public void TheSynonymTolerance_NeverReachesTheFrontmatter_OnlyTheHeadings()
    {
        var frontmatterOnly = Doc("import", "en", 10, "Something else entirely");
        var headingOnly = Doc("other", "en", 20, "What import accepts");

        var tokens = GuideSelector.Tokenize("upload");

        Assert.Equal(GuideSelector.SynonymHeadingWeight, GuideSelector.Score(tokens, headingOnly, "en"), precision: 9);
        Assert.Equal(0.0, GuideSelector.Score(tokens, frontmatterOnly, "en"), precision: 9);
    }

    /// <summary>
    /// AND THE TWIN NEVER SNEAKS IN on the real corpus either, at any position - the same property as
    /// the synthetic test, verified where the filler slots are real. The Hebrew half of the corpus is
    /// exactly 7 documents and N is 4, so there is always enough same-language material to fill the
    /// selection; if that stopped being true this test is where it would show.
    /// </summary>
    [Fact]
    public void TheRealCorpus_NeverPutsAnEnglishGuideInAHebrewSelection()
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        var questions = new[]
        {
            "איך מייצאים את הספר?",
            "מה ההבדל בין הגהה לעריכת שורה?",
            "מתי סקירת הספר השלם יוצאת מעדכניות?",
            "איך מתקנים חלוקה לא נכונה לפרקים?"
        };

        var everySelection = questions
            .Select(q => GuideSelector.Select(q, corpus.Documents, "he"))
            .ToList();

        // The population, asserted before the "none is bad" sweep below.
        Assert.Equal(questions.Length, everySelection.Count);
        Assert.All(everySelection, s => Assert.Equal(GuideSelector.DefaultCount, s.Count));

        foreach (var selection in everySelection)
            Assert.All(selection, d => Assert.Equal("he", d.Lang));
    }
}
