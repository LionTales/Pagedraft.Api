using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Tests.LanguageEngine;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic tests for <see cref="ForeignRunClassifier"/> (todo d2-skip-classifier):
/// the REPAIR|LEAVE gate that sits between the d1 detector and the d3 span-repair model.
///
/// Two layers:
///   • TARGETED [Fact]/[Theory] tests document each heuristic in isolation (proper-noun
///     Title-Case mid-sentence, sentence-initial capitalization, the c3 LINE-HEAD
///     rule (7b), including the be-c03 SINGLE-break position, which is the production shape and
///     which no `PreservationFixtureBooks` value covers, plus the four BOUNDARY positions it
///     deliberately does not claim, ALL-CAPS acronym,
///     URL/email/code, number+unit, book-entity, single-letter guard, Hebrew-in-Latin,
///     name spans — one particle (van / da / de) AND two adjacent ones (van der / de la /
///     of the) — plus the bound that keeps a leaked English clause out, quoted foreign
///     idioms, the list conveniences).
///   • A LABELED FIXTURE (<see cref="Fixture"/>) of real-shaped analysis prose — both
///     genuine leaks (REPAIR) and legitimately-foreign tokens (LEAVE), including a
///     deliberately-hard Title-Case literary term the gate LEAVEs (a retained
///     false-negative) — over which the test computes the REPAIR-class precision &amp;
///     recall and asserts they clear a stated bar.
///
/// NO Ollama / no model / no I/O — pure deterministic, runs in CI always. The class name
/// matches the ~ForeignRun test filter.
/// </summary>
public class ForeignRunClassifierTests
{
    // ── Targeted heuristic tests ─────────────────────────────────────────────

    [Fact]
    public void PlainLowercaseForeignWord_MidSentence_IsRepair()
    {
        // "confusion" — a leaked English common noun in Hebrew prose. The leak case.
        AssertSingleRun("הדמות שקעה במצב של confusion מוחלט.", ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void TitleCaseProperNoun_MidSentence_IsLeave()
    {
        // "Jerusalem" — Title-Case mid-sentence => proper noun.
        AssertSingleRun("הם נסעו אל Jerusalem בבוקר קר.", ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void TitleCaseWord_SentenceInitial_IsRepair()
    {
        // Capital at the START of the value is orthography, not a name — still a leak.
        AssertSingleRun("Confusion שלטה בכל הבית.", ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void TitleCaseWord_AfterSentenceTerminator_IsRepair()
    {
        // Capital right after ". " is sentence-initial => not a proper-noun signal.
        AssertSingleRun("לאחר מכן. Panic התפשט במהירות.", ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void TitleCaseWord_AfterOpeningQuote_IsSentenceInitial_IsRepair()
    {
        // A leading quote is transparent to sentence-initial detection.
        AssertSingleRun("\"Confusion\" הייתה התחושה.", ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    // ── c3: rule (7b), the LINE-HEAD Title-Case LEAVE ────────────────────────
    //
    // Closes the q1 false positive (`Chekhov` -> `צ'כוב`, transliterated at the head of a synopsis's second
    // paragraph). ADDITIVE by construction: (7b) can only fire where rule (7) declined, so it converts
    // REPAIR -> LEAVE and never the reverse — its whole blast radius is RECALL. The three tests below the
    // first two are the BOUNDARY pins: they are the positions (7b) deliberately does NOT claim, and they are
    // what keeps the rule narrow rather than "Title-Case always LEAVEs".
    //
    // be-c03: the predicate (`IsLineInitial`) fires on ONE hard line break; a BLANK line is deliberately NOT
    // required, because the real persisted prose this layer repairs separates paragraphs with a single '\n'
    // (3 of 3 `BookProfiles.Synopsis` and 32 of 33 `ChunkSummaries.SummaryText` rows in the runtime DB carry a
    // lone break and ZERO carry a blank line). The fixtures below and every value in `PreservationFixtureBooks`
    // use `\n\n`, so the SINGLE-break position is pinned explicitly by
    // `TitleCaseWord_AfterSingleLineBreakFollowingSentenceEnd_IsLeave` rather than left to the corpus.

    [Fact]
    public void TitleCaseProperNoun_AtParagraphHead_IsLeave()
    {
        // THE q1 SHAPE: a synopsis opens its second paragraph by topic-fronting a named entity. Rule (7) is
        // mid-sentence-only and cannot fire; before (7b) this run reached the repair model and was
        // transliterated.
        AssertSingleRun(
            "המספר שומר מרחק מכוון מן הדמויות.\n\nChekhov הוא ההשוואה המתבקשת בפרק זה.",
            ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void TitleCaseProperNoun_AtParagraphHead_WithCrLfAndOpeningQuote_IsLeave()
    {
        // Real model output uses CRLF as often as LF, and a paragraph may open on a quote. Both are
        // transparent to the LINE-head scan (IsLineInitial), which skips the same transparent-open set
        // IsSentenceInitial does; the difference between the two is which character it stops on, not which
        // ones it skips.
        AssertSingleRun(
            "המספר שומר מרחק מכוון.\r\n\r\n\"Chekhov הוא ההשוואה המתבקשת בפרק זה.",
            ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Theory]
    [InlineData("המספר שומר מרחק מכוון.\nChekhov הוא ההשוואה המתבקשת בפרק זה.", "LF")]
    [InlineData("המספר שומר מרחק מכוון.\r\nChekhov הוא ההשוואה המתבקשת בפרק זה.", "CRLF")]
    [InlineData("המספר שומר מרחק מכוון.\rChekhov הוא ההשוואה המתבקשת בפרק זה.", "CR")]
    [InlineData("מי באמת סיפר את הסיפור?\nChekhov הוא ההשוואה המתבקשת בפרק זה.", "LF after '?'")]
    [InlineData("המספר שומר מרחק מכוון.\n  \"Chekhov הוא ההשוואה המתבקשת בפרק זה.", "LF + indent + opening quote")]
    public void TitleCaseWord_AfterSingleLineBreakFollowingSentenceEnd_IsLeave(string value, string shape)
    {
        // be-c03: THE SINGLE-BREAK PIN. Rule (7b) requires ONE hard line break, not a BLANK line, and this is
        // the only position where that breadth is load-bearing: the previous line ENDS on a sentence
        // terminator, so IsSentenceInitial is true and rule (7) declines, so only (7b) can spare the run.
        //
        // This is not a hypothetical shape. It is the PRODUCTION shape: read-only against the runtime DB, 3 of
        // 3 `BookProfiles.Synopsis` values (~1.2-1.6k chars, exactly 2 LF and 0 CR) and 32 of 33
        // `ChunkSummaries.SummaryText` values separate their paragraphs with a SINGLE '\n' and ZERO carry a
        // blank line, while every fixture in `PreservationFixtureBooks` uses '\n\n'. So if the predicate is
        // ever narrowed to require a blank line to "match the paragraph wording", rule (7b) goes DEAD on every
        // real Synopsis and Summary value and q1's measured false positive (`Chekhov` -> `צ'כוב`) returns on
        // exactly the shape `f2` shipped, with no fixture able to see it. This test is that fixture.
        //
        // REVERT-VERIFIED against a blank-line predicate: the LF, CR, "LF after '?'" and "LF + indent +
        // opening quote" rows go RED on the assertion below. The CRLF row does NOT discriminate (a lone
        // "\r\n" is two break characters, so a naive blank-line scan reads it as a blank line); it is kept
        // because CRLF is a real model-output shape, but the LF / CR rows are the ones that carry the pin.
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
        Assert.Single(runs);
        Assert.Equal("Chekhov", runs[0].Text);

        var decision = ForeignRunClassifier.Classify(runs[0], value, ExpectedScript.Hebrew, null);
        Assert.True(decision == ForeignRunDecision.Leave,
            $"[{shape}] a Title-Case Latin proper noun after a SINGLE hard line break that follows a completed " +
            "sentence was classified REPAIR. Rule (7b) must fire at a LINE head, not only at a BLANK line: the " +
            "real persisted Synopsis / SummaryText prose separates paragraphs with one '\\n' and never with a " +
            "blank line, so a blank-line predicate makes (7b) dead in production and hands q1's false-positive " +
            "shape back to the repair model.");
    }

    [Theory]
    [InlineData("הדמות הראשית שקעה בתחושה עמוקה של\nConfusion מוחלטת לאורך כל הפרק.", "soft wrap mid-sentence")]
    [InlineData("הנושאים המרכזיים:\nConfusion היא התחושה השלטת בפרק.", "list line, no marker")]
    [InlineData("הנושאים המרכזיים:\n- Confusion היא התחושה השלטת בפרק.", "list line, hyphen marker")]
    public void TitleCaseWord_AfterSingleLineBreakMidSentence_IsLeave_ByRule7_NotRule7b(string value, string shape)
    {
        // be-c03, the ATTRIBUTION pin. These are the three shapes rule (7b)'s breadth is usually suspected of
        // exposing: a soft line break inside one paragraph, a wrapped line, and a newline-separated list line
        // whose marker IsTransparentOpen does not cover. They are LEAVE, but NOT because of (7b): in each of
        // them the previous line does not end on a sentence terminator, so IsSentenceInitial is FALSE, rule (7)
        // is evaluated first and already returns Leave. That is PRE-c3 behaviour and narrowing (7b) would not
        // recover any of them. Recorded as a test so the exposure is attributed rather than argued.
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
        Assert.Single(runs);
        Assert.Equal("Confusion", runs[0].Text);

        var decision = ForeignRunClassifier.Classify(runs[0], value, ExpectedScript.Hebrew, null);
        Assert.True(decision == ForeignRunDecision.Leave,
            $"[{shape}] this run changed verdict. It is spared by rule (7) (Title-Case, NOT sentence-initial " +
            "because a line break is skipped as ordinary whitespace by IsSentenceInitial before its terminator " +
            "check), which predates rule (7b). A flip here means rule (7) or IsSentenceInitial moved, not (7b).");
    }

    [Fact]
    public void TitleCaseWord_AtSentenceHeadMidParagraph_StillRepairs()
    {
        // THE ADDITIVE BOUNDARY. A sentence head that is NOT a paragraph head keeps rule (7)'s verdict: the
        // orthographic argument ("a sentence-initial capital is not evidence of a name") is untouched there,
        // so a capitalized leaked common noun mid-paragraph is still sent to the model. If this ever flips,
        // rule (7b) has silently widened into "Title-Case always LEAVEs".
        AssertSingleRun(
            "הפרק נפתח בשקט גמור.\n\nהמתח עלה בהדרגה. Panic התפשט בין הנוכחים.",
            ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void TitleCaseWord_ValueInitial_WithLeadingWhitespace_StillRepairs()
    {
        // VALUE-initial is deliberately NOT a paragraph head: a short structured field whose whole value is one
        // sentence can plausibly OPEN with a capitalized leak, and there is no paragraph boundary to argue from.
        // Leading horizontal whitespace must not turn the value start into one.
        AssertSingleRun("  Confusion שלטה בכל הבית.", ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void LowercaseLeak_AtParagraphHead_StillRepairs()
    {
        // The CASE gate: (7b) requires Title-Case, so a lowercase leak opening a paragraph is untouched.
        AssertSingleRun(
            "הפרק הראשון בונה את הדמות לאט.\n\nconfusion היא התחושה השלטת לאורך כל הפרק השני.",
            ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void HebrewRun_AtParagraphHead_InLatinBook_StillRepairs()
    {
        // The SCRIPT gate: (7b) lives inside the Hebrew-expected/Latin-run block, because Hebrew has no letter
        // case and therefore no Title-Case signal to read. A Hebrew run opening a paragraph in an English book
        // must still REPAIR (only the entity lever can spare it).
        AssertSingleRun(
            "The narrator keeps a deliberate distance.\n\nמתח builds slowly through the second act.",
            ExpectedScript.Latin, ForeignRunDecision.Repair);
    }

    [Fact]
    public void Q1MeasuredFalsePositive_TheSynopsisChekhovValue_IsNowDeterministicallyGated()
    {
        // ANCHORED TO THE MEASUREMENT, not to a re-authored shape: this is q1's actual d6 case, read from the
        // fixture it was measured with (PreservationFixtureBooks.SynopsisCases). It scored 83% preservation
        // because this ONE value reached the model. With (7b) it must be gated deterministically — zero model
        // calls — which is what q2 scope (ii) re-measures live.
        var chekhov = PreservationFixtureBooks.SynopsisCases.Single(c => c.Token == "Chekhov");

        var runs = LatinInHebrewContentDetector.DetectForeignRuns(chekhov.Value, chekhov.Expected);
        Assert.Single(runs);
        Assert.Equal("Chekhov", runs[0].Text);

        // NO entity set at all: the point is that the CLASSIFIER carries it, with the entity lever inert
        // (the synopsis book's manuscript never names Chekhov, and no widening of the entity set could
        // contain a name the synopsis itself introduces).
        var repairRuns = ForeignRunClassifier.RunsToRepair(runs, chekhov.Value, chekhov.Expected, null);
        Assert.Empty(repairRuns);
    }

    [Fact]
    public void Rule7b_SparesNoLeakInEitherD5CleaningCorpus()
    {
        // THE RECALL PIN, and the model-free half of q2's regression scope (iii). Rule (7b) is additive, so it
        // cannot damage PRESERVATION — the only thing it can cost is CLEANING. Assert the prediction directly:
        // every leak in the shipped d5 corpus AND in q1's synopsis cleaning corpus is still classified REPAIR
        // with no entity set. A failure here IS the predicted recall regression, visible without a GPU.
        foreach (var leak in PreservationFixtureBooks.LeakCases.Concat(PreservationFixtureBooks.SynopsisLeakCases))
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(leak.Value, ExpectedScript.Hebrew);
            var repairRuns = ForeignRunClassifier.RunsToRepair(runs, leak.Value, ExpectedScript.Hebrew, null);

            Assert.True(repairRuns.Any(r => r.Text == leak.Leak),
                $"[{leak.Label}] the leak '{leak.Leak}' is no longer classified REPAIR. Rule (7b) (c3) was " +
                "supposed to cost ZERO cleaning on both d5 corpora — this is the recall regression that " +
                "reverts c3 under q2 scope (iii).");
        }
    }

    [Theory]
    [InlineData("הוא עבד ב NASA שנים רבות.")]
    [InlineData("סוכני ה FBI הגיעו למקום.")]
    public void AllCapsAcronym_IsLeave(string value)
    {
        AssertSingleRun(value, ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void Email_AllPartsAreLeave()
    {
        // user @ example . com => every Latin run borders '@' or a dotted host.
        AssertAllRuns("כתבו אל user@example.com מיד היום.", ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void UrlWithScheme_AllPartsAreLeave()
    {
        AssertAllRuns("בכתובת https://github.com נמצא הקוד.", ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void DottedHost_AllPartsAreLeave()
    {
        AssertAllRuns("ראו את האתר example.com לפרטים נוספים.", ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void DottedFileExtensionWithDigit_IsLeave()
    {
        // "file2.txt" — a real filename: the digit borders "file" (number+unit heuristic) and
        // the backward dotted-member check spares "txt". Regression guard: the be-f02 tightening
        // of the FORWARD dot check (lowercase/digit only) must not disturb genuine dotted hosts.
        AssertAllRuns("שמור את הקובץ file2.txt בתיקייה.", ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void ForeignRunFollowedByPeriodThenCapital_IsRepair_NotMistakenForDottedHost()
    {
        // "confusion.Then" — a missing space after a sentence period (a typo), NOT a dotted
        // host like "example.com". Before the be-f02 fix, IsHostChar accepted an UPPERCASE
        // Latin letter after the dot, so this run was wrongly classified LEAVE (the leaked
        // "confusion" slipped past the gate). A genuine host/member segment is lowercase- or
        // digit-led ("example.com", "file2.txt"); an uppercase letter right after a dot with no
        // space is a new-sentence boundary, so this must REPAIR.
        const string value = "הדמות חשה confusion.Then he left.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);

        var confusion = runs.Single(r => r.Text == "confusion");
        Assert.Equal(10, confusion.Start);
        Assert.Equal(9, confusion.Length);
        Assert.Equal("confusion", value.Substring(confusion.Start, confusion.Length));

        Assert.Equal(
            ForeignRunDecision.Repair,
            ForeignRunClassifier.Classify(confusion, value, ExpectedScript.Hebrew));
    }

    [Fact]
    public void SnakeCaseIdentifier_AllPartsAreLeave()
    {
        AssertAllRuns("הפונקציה get_user נקראה שוב.", ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Theory]
    [InlineData("המרחק היה 5km בקירוב.")]         // digit directly borders the unit
    [InlineData("המשקל היה 3 kg בלבד.")]          // unit one space from a digit
    [InlineData("רזולוציה של 100px גבוהה מאוד.")] // digit directly borders the unit
    public void NumberPlusUnit_IsLeave(string value)
    {
        AssertSingleRun(value, ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void BareUnitWordInProse_WithoutAdjacentDigit_IsRepair()
    {
        // "min" is a unit token, but with no adjacent digit it is just an English word => leak.
        AssertSingleRun("היא חיכתה עוד min ארוך אחד.", ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void SingleForeignLetterRun_IsLeave_DefensiveGuard()
    {
        // The detector never emits a length-1 run, but the guard must still LEAVE one.
        var decision = ForeignRunClassifier.Classify(
            new ForeignRun("A", 0, 1), "A story", ExpectedScript.Hebrew);
        Assert.Equal(ForeignRunDecision.Leave, decision);
    }

    // ── Name-particle lever ──────────────────────────────────────────────────

    [Theory]
    [InlineData("הצייר Vincent van Gogh היה מפורסם.", "van")]              // van of Vincent van Gogh
    [InlineData("הצייר Leonardo da Vinci צייר את המונה ליזה.", "da")]      // da of Leonardo da Vinci
    [InlineData("הסופרת Simone de Beauvoir כתבה רבות על חירות.", "de")]    // de of Simone de Beauvoir
    public void NameParticle_LowercaseBetweenTwoTitleCaseLatinRuns_IsLeave(string value, string particle)
    {
        // A lowercase Latin connective wedged between two Title-Case Latin names is part of the
        // name (not a leak) => LEAVE. Context-derived from the immediate neighbours, no word list.
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
        var run = runs.Single(r => r.Text == particle);
        Assert.Equal(ForeignRunDecision.Leave, ForeignRunClassifier.Classify(run, value, ExpectedScript.Hebrew));
    }

    [Theory]
    // TWO adjacent lowercase particles inside one Title-Case name span. Runs are WORD-level (a space
    // ends a run), so the old immediate-adjacency rule had each particle disqualify the OTHER and
    // classified BOTH as leaks — d3 then spliced Hebrew into a book title / surname, and
    // validation-by-re-detect could not see it (substituting Hebrew for "of" REDUCES the Latin-run
    // count, so the corruption read as a successful repair). Every run here must LEAVE.
    [InlineData("הוא קרא את The Lord of the Rings בשנית.")]        // "of the" — The/Lord/of/the/Rings
    [InlineData("האדריכל Mies van der Rohe עיצב את הביתן.")]        // "van der" — Mies/van/der/Rohe
    [InlineData("הסופר Charles de la Rue פרסם ספר חדש.")]           // "de la"  — Charles/de/la/Rue
    [InlineData("הרומן The Fall of the House of Usher נפתח כך.")]   // two chains in one span
    public void NameSpan_TwoAdjacentLowercaseParticles_AllRunsAreLeave(string value)
    {
        AssertAllRuns(value, ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Theory]
    [InlineData("היא נכנסה אל the van בחניון האחורי.", "van")]          // lowercase "the", then Hebrew => no anchor
    [InlineData("היא נכנסה אל the van בחניון האחורי.", "the")]          // Hebrew on the left => no anchor
    [InlineData("הדמות שקעה במצב של confusion מוחלט.", "confusion")]    // Hebrew neighbours => not a particle (a real leak)
    public void LowercaseLatinWord_NotInsideTitleCaseNameSpan_StaysRepair(string value, string word)
    {
        // The name-span rule must be inert unless a Title-Case Latin ANCHOR is reachable on BOTH
        // sides across lowercase Latin tokens only — a plain lowercase leak in ordinary Hebrew prose
        // still REPAIRs. Generalizing the walk must NOT loosen this.
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
        var run = runs.Single(r => r.Text == word);
        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(run, value, ExpectedScript.Hebrew));
    }

    [Fact]
    public void LowercaseLatinWord_TitleCaseAnchorOnOneSideOnly_StaysRepair()
    {
        // "Sarah with confusion" — a Title-Case anchor exists to the LEFT, but the right side runs
        // into Hebrew, so there is no enclosing name span. Both lowercase runs are leaks => REPAIR.
        const string value = "הוא תיאר את Sarah with confusion רבה.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);

        Assert.Equal(ForeignRunDecision.Leave, ForeignRunClassifier.Classify(
            runs.Single(r => r.Text == "Sarah"), value, ExpectedScript.Hebrew));
        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(
            runs.Single(r => r.Text == "with"), value, ExpectedScript.Hebrew));
        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(
            runs.Single(r => r.Text == "confusion"), value, ExpectedScript.Hebrew));
    }

    [Fact]
    public void LeakedEnglishClause_BetweenTwoTitleCaseWords_ExceedsNameSpanBound_StaysRepair()
    {
        // The BOUND. A Title-Case token sits on both sides, but FOUR lowercase tokens lie between
        // them — a leaked English clause, not a name span (the longest real particle chains are two:
        // "van der", "de la", "of the"). The whole chain must REPAIR, and it must do so UNIFORMLY:
        // a chain that came out half-LEAVE / half-REPAIR is exactly how a partial splice corrupts.
        const string value = "הדמות Sarah walked into the empty Cathedral בבוקר.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);

        Assert.Equal(
            new[] { "Sarah", "walked", "into", "the", "empty", "Cathedral" },
            runs.Select(r => r.Text).ToArray());

        Assert.Equal(
            new[]
            {
                ForeignRunDecision.Leave,  // Sarah — Title-Case mid-sentence proper noun
                ForeignRunDecision.Repair, // walked
                ForeignRunDecision.Repair, // into
                ForeignRunDecision.Repair, // the
                ForeignRunDecision.Repair, // empty
                ForeignRunDecision.Leave,  // Cathedral — Title-Case mid-sentence
            },
            runs.Select(r => ForeignRunClassifier.Classify(r, value, ExpectedScript.Hebrew)).ToArray());
    }

    [Fact]
    public void NameSpan_AnchorSeparatedByPunctuation_IsNotASpan_StaysRepair()
    {
        // A comma between the anchor and the particle breaks the span: the walk aborts the moment it
        // crosses a non-space separator, so "of" here has no left anchor and REPAIRs.
        const string value = "הוא קרא את Lord, of the Rings בשנית.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
        var run = runs.Single(r => r.Text == "of");
        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(run, value, ExpectedScript.Hebrew));
    }

    // ── Quoted-span lever ────────────────────────────────────────────────────

    [Fact]
    public void QuotedForeignIdiom_MultiWord_AllPartsAreLeave()
    {
        // "carpe diem" — an intentional quoted foreign idiom (do-not-translate span). Both runs
        // are inside the quotes with another word beside them => LEAVE.
        AssertAllRuns("הביטוי \"carpe diem\" מסמל אומץ לחיות.", ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    [Fact]
    public void SingleQuotedLowercaseWord_IsRepair_QuoteRuleNeedsMultiWordSpan()
    {
        // A LONE scare-quoted word is NOT a multi-word idiom, so the quote rule must NOT fire and
        // the leak still REPAIRs. Guards against a too-greedy quote rule sparing real leaks.
        AssertSingleRun("המילה \"confusion\" נשמעה מוזרה מאוד.", ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void LeakAdjacentToPossessiveApostrophe_StaysRepair()
    {
        // A stray English possessive apostrophe is not a quoted span (only one side carries a
        // quote-like char and it hugs a letter), so the leaked "confusion" must still REPAIR.
        const string value = "הוא תיאר את John's confusion בבירור.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
        var run = runs.Single(r => r.Text == "confusion");
        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(run, value, ExpectedScript.Hebrew));
    }

    [Theory]
    [InlineData("הביטוי «carpe diem» מסמל אומץ לחיות.")]  // guillemets  « … »
    [InlineData("הביטוי “carpe diem” מסמל אומץ לחיות.")]  // curly double “ … ”
    [InlineData("הביטוי ‘carpe diem’ מסמל אומץ לחיות.")]  // curly single ‘ … ’
    public void QuotedForeignIdiom_DirectionalQuotePairs_AllPartsAreLeave(string value)
    {
        // The be-c05 pairing table must recognise the DIRECTIONAL pairs, not just the ASCII quote:
        // an opening form on the left and its matching closing form on the right => a genuine
        // do-not-translate span => LEAVE both runs.
        AssertAllRuns(value, ExpectedScript.Hebrew, ForeignRunDecision.Leave);
    }

    // ── be-c05: the quote gate must be safe BY CONSTRUCTION ──────────────────
    //
    // The gate used to accept ANY quote-ish char on each side, requiring only that it be
    // "boundary-like" on its OUTER side (outward neighbour is a non-word char / the string edge).
    // Two legitimate, plausible inputs satisfy that on the RIGHT without being quotes at all:
    //
    //   • a Hebrew ABBREVIATION geresh — וכו׳ (etc.), עמ׳ (page) — a trailing ׳ followed by a space;
    //   • an English PLURAL POSSESSIVE apostrophe — "the girls' faces" — a trailing ' followed by a space.
    //
    // Put either one to the RIGHT of a leak while any ordinary opening quote sits to its LEFT within
    // the 64-char window, and the leak was wrongly LEFT. No input in the reference corpus happened to
    // have that shape, so a corpus A/B showed 0 diff — corpus luck, not a guarantee. The fix drops the
    // geresh from the delimiter set entirely and requires the two bounds to be a MATCHING PAIR.

    [Theory]
    [InlineData("הדמות שקעה במצב של confusion וכו׳ והלאה בפרק.")]   // וכו׳ = "etc."
    [InlineData("הוא תיאר confusion עמ׳ 42 בספר החדש.")]             // עמ׳ = "page"
    public void HebrewAbbreviationGeresh_NearLeak_DoesNotSpareIt(string value)
    {
        // Baseline shape: a Hebrew abbreviation's trailing geresh sitting beside a real lowercase
        // leak. The geresh is an ABBREVIATION mark, never a quote — it must not participate in the
        // quoted-span gate at all.
        AssertSingleRun(value, ExpectedScript.Hebrew, ForeignRunDecision.Repair);
    }

    [Fact]
    public void AdversarialQuoteGate_OpeningQuoteLeft_AbbreviationGereshRight_LeakStillRepairs()
    {
        // THE ADVERSARIAL CONSTRUCTION (be-c05). Deliberately built to trip the old gate:
        //   • LEFT  — an opening `"` preceded by a space (boundary-like), reachable across Hebrew
        //             letters and spaces only, well inside the 64-char window;
        //   • RIGHT — the trailing geresh of the abbreviation וכו׳, followed by a space (boundary-like).
        // The old gate saw "a quote on both sides + another word inside" and returned LEAVE, sparing a
        // genuine leak inside an ORDINARY Hebrew sentence. The new gate rejects it twice over: the
        // geresh is no longer a delimiter (it TERMINATES the candidate span), and `"` could not pair
        // with `׳` even if it were. This test is RED against the un-patched gate.
        const string value = "היא אמרה: \"אני מרגישה confusion וכו׳ ואין לי מילים.\"";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);

        var run = Assert.Single(runs);
        Assert.Equal("confusion", run.Text);
        Assert.Equal("confusion", value.Substring(run.Start, run.Length));

        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(run, value, ExpectedScript.Hebrew));
    }

    [Fact]
    public void AdversarialQuoteGate_OpeningDoubleQuote_ClosedByPluralPossessive_LeakStillRepairs()
    {
        // The same hole in the OTHER direction (a Hebrew leak in an English book) and via the OTHER
        // boundary-like non-quote: an English PLURAL POSSESSIVE apostrophe ("the girls' faces"). It is
        // followed by a space, so it is boundary-like and reachable as a right-hand bound — but `'`
        // cannot CLOSE a span opened by `"`. Mismatched pair => not a quoted span => the leaked מבוכה
        // still REPAIRs. RED against the un-patched gate (which required no pairing at all).
        const string value = "She said: \"I sensed מבוכה in the girls' faces.\"";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Latin);

        var run = Assert.Single(runs);
        Assert.Equal("מבוכה", run.Text);

        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(run, value, ExpectedScript.Latin));
    }

    [Fact]
    public void HebrewAcronymGershayim_NearLeak_DoesNotSpareIt()
    {
        // The gershayim of צה״ל sits BETWEEN two Hebrew letters, so its outer neighbour is a word char
        // and the boundary-like test already rejects it — defused BY CONSTRUCTION, unlike the trailing
        // geresh. Pinned so a future widening of the delimiter set cannot quietly re-open it.
        const string value = "הוא שירת ב צה״ל וחש confusion עמוק.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
        var run = runs.Single(r => r.Text == "confusion");
        Assert.Equal(ForeignRunDecision.Repair, ForeignRunClassifier.Classify(run, value, ExpectedScript.Hebrew));
    }

    // ── Book-entity lever ────────────────────────────────────────────────────

    [Fact]
    public void BookEntity_HebrewNameInLatinBook_IsLeave()
    {
        // A foreign HEBREW run has no case signal — only the entity list can spare it.
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "דגן" };
        AssertSingleRun("The hero דגן left the city gates.", ExpectedScript.Latin, ForeignRunDecision.Leave, entities);
    }

    [Fact]
    public void BookEntity_WithoutEntitySet_HebrewNameIsRepair()
    {
        // Same value, no entity set: a bare Hebrew run in English prose is a leak by default.
        AssertSingleRun("The hero דגן left the city gates.", ExpectedScript.Latin, ForeignRunDecision.Repair);
    }

    [Fact]
    public void BookEntity_MembershipIsCaseInsensitive_EvenWithOrdinalSet()
    {
        // Caller passed an ORDINAL set; the classifier still matches case-insensitively via the
        // linear fallback scan (Contains alone would miss "aragorn" against "Aragorn").
        var ordinal = new HashSet<string>(StringComparer.Ordinal) { "Aragorn" };
        AssertSingleRun("הגיבור aragorn יצא למסע ארוך.", ExpectedScript.Hebrew, ForeignRunDecision.Leave, ordinal);
    }

    [Fact]
    public void BookEntity_OrdinalIgnoreCaseSet_MatchesDifferentCase_ViaFastPath()
    {
        // Caller passed an OrdinalIgnoreCase set; a different-case value must still match, and it
        // does so entirely through set.Contains (the O(1) fast path), no linear scan needed.
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Aragorn" };
        AssertSingleRun("הגיבור aragorn יצא למסע ארוך.", ExpectedScript.Hebrew, ForeignRunDecision.Leave, entities);
    }

    [Fact]
    public void BookEntity_OrdinalIgnoreCaseSet_GenuineMiss_IsRepair()
    {
        // A true miss against an OrdinalIgnoreCase set must stay a miss: the redundant fallback
        // scan is skipped for such sets, so this only passes if the skip does not accidentally
        // widen matching.
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Aragorn" };
        AssertSingleRun("הגיבור legolas יצא למסע ארוך.", ExpectedScript.Hebrew, ForeignRunDecision.Repair, entities);
    }

    // ── Bidirectional: Hebrew-in-Latin leak ──────────────────────────────────

    [Fact]
    public void HebrewWordInEnglishProse_IsRepair()
    {
        AssertSingleRun("The mood is one of מתח throughout the scene.", ExpectedScript.Latin, ForeignRunDecision.Repair);
    }

    // ── List conveniences ────────────────────────────────────────────────────

    [Fact]
    public void ClassifyList_PreservesOrderAndPairsRuns()
    {
        const string value = "הצייר Vincent van Gogh היה מפורסם.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);

        var classified = ForeignRunClassifier.Classify(runs, value, ExpectedScript.Hebrew);

        Assert.Equal(runs.Count, classified.Count);
        Assert.Equal(runs.Select(r => r.Text).ToArray(), classified.Select(c => c.Run.Text).ToArray());
        // Vincent (proper noun) LEAVE, van (name particle between two Title-Case runs) LEAVE,
        // Gogh (proper noun) LEAVE — the whole personal name is preserved.
        Assert.Equal(
            new[] { ForeignRunDecision.Leave, ForeignRunDecision.Leave, ForeignRunDecision.Leave },
            classified.Select(c => c.Decision).ToArray());
    }

    [Fact]
    public void RunsToRepair_ReturnsOnlyRepairRuns()
    {
        const string value = "היא גללה ב Instagram אבל חשה confusion עמוקה.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);

        var toRepair = ForeignRunClassifier.RunsToRepair(runs, value, ExpectedScript.Hebrew);

        // Instagram (proper noun) is dropped; only the leaked "confusion" survives.
        Assert.Equal(new[] { "confusion" }, toRepair.Select(r => r.Text).ToArray());
    }

    [Fact]
    public void EmptyRunList_ConveniencesReturnEmpty()
    {
        var none = Array.Empty<ForeignRun>();
        Assert.Empty(ForeignRunClassifier.Classify(none, "anything", ExpectedScript.Hebrew));
        Assert.Empty(ForeignRunClassifier.RunsToRepair(none, "anything", ExpectedScript.Hebrew));
    }

    // ── Labeled fixture: precision / recall of the REPAIR class ───────────────

    /// <summary>REPAIR-class recall must be HIGH — missing a leak is the costly error.</summary>
    private const double RecallBar = 0.90;

    /// <summary>REPAIR-class precision must be REASONABLE — the d3 model no-ops a false REPAIR,
    /// so some over-repair is acceptable, but the gate must still filter most non-leaks.</summary>
    private const double PrecisionBar = 0.85;

    [Fact]
    public void Fixture_RepairClass_PrecisionAndRecall_MeetBar()
    {
        var tp = 0; // actual REPAIR, predicted REPAIR
        var fp = 0; // actual LEAVE,  predicted REPAIR
        var fn = 0; // actual REPAIR, predicted LEAVE
        var tn = 0; // actual LEAVE,  predicted LEAVE

        foreach (var c in Fixture())
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(c.Value, c.Expected);

            // Sanity: the fixture's per-run labels must line up with what the detector emits.
            Assert.True(
                runs.Count == c.Decisions.Length,
                $"Detector produced {runs.Count} run(s) for \"{c.Value}\" but the fixture labels {c.Decisions.Length}.");

            for (var i = 0; i < runs.Count; i++)
            {
                var predicted = ForeignRunClassifier.Classify(runs[i], c.Value, c.Expected, c.Entities);
                var actual = c.Decisions[i];

                if (actual == ForeignRunDecision.Repair && predicted == ForeignRunDecision.Repair) tp++;
                else if (actual == ForeignRunDecision.Leave && predicted == ForeignRunDecision.Repair) fp++;
                else if (actual == ForeignRunDecision.Repair && predicted == ForeignRunDecision.Leave) fn++;
                else tn++;
            }
        }

        // A meaningful fixture: real positives AND negatives. The lowercase name-particle case that
        // once produced a false-positive is now recovered by the name-particle rule (so REPAIR
        // precision is clean); the retained honest error is a Title-Case literary-term false-negative
        // ("Tension"), which keeps REPAIR recall an honest < 1.0 (the d3 model is the backstop there).
        Assert.True(tp + fn >= 12, $"Fixture too small: only {tp + fn} REPAIR-labeled runs.");
        Assert.True(tn + fp >= 12, $"Fixture too small: only {tn + fp} LEAVE-labeled runs.");

        var recall = (double)tp / (tp + fn);
        var precision = (double)tp / (tp + fp);

        Assert.True(
            recall >= RecallBar,
            $"REPAIR recall {recall:F3} < bar {RecallBar:F2} (tp={tp}, fn={fn}, fp={fp}, tn={tn}).");
        Assert.True(
            precision >= PrecisionBar,
            $"REPAIR precision {precision:F3} < bar {PrecisionBar:F2} (tp={tp}, fn={fn}, fp={fp}, tn={tn}).");
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private sealed record Case(
        string Value,
        ExpectedScript Expected,
        ForeignRunDecision[] Decisions,
        IReadOnlySet<string>? Entities = null);

    private static readonly IReadOnlySet<string> Dagan =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "דגן" };

    private static IEnumerable<Case> Fixture()
    {
        const ForeignRunDecision R = ForeignRunDecision.Repair;
        const ForeignRunDecision L = ForeignRunDecision.Leave;

        // ---- REPAIR: genuine foreign-vocabulary leaks -----------------------
        yield return new("הדמות שקעה במצב של confusion מוחלט.", ExpectedScript.Hebrew, new[] { R });
        yield return new("היא חשה claustrophobia בחדר הקטן.", ExpectedScript.Hebrew, new[] { R });
        yield return new("הרגש המרכזי הוא nuance עדין מאוד.", ExpectedScript.Hebrew, new[] { R });
        yield return new("התחושה הייתה של foreshadowing כבד.", ExpectedScript.Hebrew, new[] { R });
        yield return new("המתח יצר תחושת suspense חזקה.", ExpectedScript.Hebrew, new[] { R });
        yield return new("הקורא חש empathy כלפי הגיבור.", ExpectedScript.Hebrew, new[] { R });
        yield return new("הסצנה משדרת awe אל מול הנוף.", ExpectedScript.Hebrew, new[] { R });
        yield return new("Confusion שלטה בכל הבית.", ExpectedScript.Hebrew, new[] { R }); // sentence-initial cap
        yield return new("לאחר מכן. Panic התפשט במהירות.", ExpectedScript.Hebrew, new[] { R }); // after terminator
        // Hebrew-in-Latin leaks (ExpectedScript.Latin).
        yield return new("The mood is one of מתח throughout the scene.", ExpectedScript.Latin, new[] { R });
        yield return new("She felt deep שמחה in that quiet moment.", ExpectedScript.Latin, new[] { R });
        yield return new("A sudden כעס overtook him without warning.", ExpectedScript.Latin, new[] { R });
        // A Title-Case anchor on ONE side only: "Sarah" is a name (LEAVE) but "with confusion" runs
        // into Hebrew, so there is no enclosing name span and both lowercase runs are leaks. Guards
        // the generalized name-span walk against loosening into a one-sided rule.
        yield return new("הוא תיאר את Sarah with confusion רבה.", ExpectedScript.Hebrew, new[] { L, R, R });
        // be-c05 (quote gate safe by CONSTRUCTION): a real leak whose right-hand "closing quote" is
        // actually a Hebrew abbreviation geresh (וכו׳), with a genuine opening `"` to its left. The
        // un-patched gate LEFT this. Its twin, in the other script direction, is closed by an English
        // PLURAL POSSESSIVE apostrophe — boundary-like, but it cannot pair with `"`.
        yield return new("היא אמרה: \"אני מרגישה confusion וכו׳ ואין לי מילים.\"", ExpectedScript.Hebrew, new[] { R });
        yield return new("She said: \"I sensed מבוכה in the girls' faces.\"", ExpectedScript.Latin, new[] { R });
        // HARD (false-negative): a Title-Case literary term the cheap gate LEAVEs; the human
        // label is REPAIR. Kept in to make recall an honest < 1.0 (the d3 model is the backstop
        // for the tokens the gate cannot recover).
        yield return new("הסצנה בונה Tension לאורך כל הפרק.", ExpectedScript.Hebrew, new[] { R });

        // ---- LEAVE: legitimately-foreign tokens -----------------------------
        yield return new("הם נסעו אל Jerusalem בבוקר קר.", ExpectedScript.Hebrew, new[] { L }); // proper noun
        yield return new("הדמות פגשה את Sarah בגן העירוני.", ExpectedScript.Hebrew, new[] { L }); // proper noun
        yield return new("היא גללה ב Instagram במשך שעות.", ExpectedScript.Hebrew, new[] { L }); // brand (Title-Case)
        yield return new("הוא עבד ב NASA שנים רבות.", ExpectedScript.Hebrew, new[] { L }); // acronym
        yield return new("סוכני ה FBI הגיעו למקום.", ExpectedScript.Hebrew, new[] { L }); // acronym
        yield return new("ראו את האתר example.com לפרטים נוספים.", ExpectedScript.Hebrew, new[] { L, L }); // dotted host
        yield return new("כתבו אל user@example.com מיד היום.", ExpectedScript.Hebrew, new[] { L, L, L }); // email
        yield return new("בכתובת https://github.com נמצא הקוד.", ExpectedScript.Hebrew, new[] { L, L, L }); // url scheme
        yield return new("הפונקציה get_user נקראה שוב.", ExpectedScript.Hebrew, new[] { L, L }); // snake_case
        yield return new("המרחק היה 5km בקירוב.", ExpectedScript.Hebrew, new[] { L }); // number+unit
        yield return new("המשקל היה 3 kg בלבד.", ExpectedScript.Hebrew, new[] { L }); // number+unit (spaced)
        yield return new("רזולוציה של 100px גבוהה מאוד.", ExpectedScript.Hebrew, new[] { L }); // number+unit
        // Book entity: a Hebrew character name in an English book — only the entity list spares it.
        yield return new("The hero דגן left the city gates.", ExpectedScript.Latin, new[] { L }, Dagan);
        // Name particles: a lowercase Latin connective sandwiched between two Title-Case Latin names
        // is part of the name (LEAVE), recovered by the name-particle rule (previously a false-positive).
        yield return new("הצייר Vincent van Gogh היה מפורסם.", ExpectedScript.Hebrew, new[] { L, L, L });   // van
        yield return new("הצייר Leonardo da Vinci צייר את המונה ליזה.", ExpectedScript.Hebrew, new[] { L, L, L }); // da
        yield return new("הסופרת Simone de Beauvoir כתבה על חירות.", ExpectedScript.Hebrew, new[] { L, L, L });   // de
        // TWO ADJACENT particles inside one name span (the be-c01 P0). Under the old immediate-
        // adjacency rule each particle disqualified the other and BOTH were false-positive REPAIRs,
        // handing a book title / surname to the repair model. Every run must LEAVE.
        yield return new("הוא קרא את The Lord of the Rings בשנית.", ExpectedScript.Hebrew, new[] { L, L, L, L, L }); // of the
        yield return new("האדריכל Mies van der Rohe עיצב את הביתן.", ExpectedScript.Hebrew, new[] { L, L, L, L });   // van der
        yield return new("הסופר Charles de la Rue פרסם ספר חדש.", ExpectedScript.Hebrew, new[] { L, L, L, L });      // de la
        // Quoted foreign idiom: an intentional do-not-translate multi-word span => LEAVE both runs.
        // Both the ASCII quote and a DIRECTIONAL pair (be-c05) must be recognised.
        yield return new("הביטוי \"carpe diem\" מסמל אומץ לחיות.", ExpectedScript.Hebrew, new[] { L, L });
        yield return new("הביטוי «carpe diem» מסמל אומץ לחיות.", ExpectedScript.Hebrew, new[] { L, L });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertSingleRun(
        string value,
        ExpectedScript expected,
        ForeignRunDecision decision,
        IReadOnlySet<string>? entities = null)
    {
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, expected);
        Assert.Single(runs);
        Assert.Equal(decision, ForeignRunClassifier.Classify(runs[0], value, expected, entities));
    }

    private static void AssertAllRuns(
        string value,
        ExpectedScript expected,
        ForeignRunDecision decision,
        IReadOnlySet<string>? entities = null)
    {
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, expected);
        Assert.NotEmpty(runs);
        foreach (var run in runs)
        {
            Assert.Equal(decision, ForeignRunClassifier.Classify(run, value, expected, entities));
        }
    }
}
