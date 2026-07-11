using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic tests for <see cref="ForeignRunClassifier"/> (todo d2-skip-classifier):
/// the REPAIR|LEAVE gate that sits between the d1 detector and the d3 span-repair model.
///
/// Two layers:
///   • TARGETED [Fact]/[Theory] tests document each heuristic in isolation (proper-noun
///     Title-Case mid-sentence, sentence-initial capitalization, ALL-CAPS acronym,
///     URL/email/code, number+unit, book-entity, single-letter guard, Hebrew-in-Latin,
///     the list conveniences).
///   • A LABELED FIXTURE (<see cref="Fixture"/>) of real-shaped analysis prose — both
///     genuine leaks (REPAIR) and legitimately-foreign tokens (LEAVE), including two
///     deliberately-hard cases (a Title-Case literary term the gate LEAVEs, and a
///     lowercase name particle the gate REPAIRs) — over which the test computes the
///     REPAIR-class precision &amp; recall and asserts they clear a stated bar.
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
        // Vincent (proper noun) LEAVE, van (lowercase particle) REPAIR, Gogh (proper noun) LEAVE.
        Assert.Equal(
            new[] { ForeignRunDecision.Leave, ForeignRunDecision.Repair, ForeignRunDecision.Leave },
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

        // A meaningful fixture: real positives AND negatives, and at least one of each error type
        // (a Title-Case literary-term false-negative and a lowercase name-particle false-positive)
        // so the measured rates are not a trivial 1.0.
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
        // HARD (false-positive): a lowercase name particle "van" the gate REPAIRs; the human label
        // is LEAVE (part of "Vincent van Gogh"). Kept in to make precision an honest < 1.0.
        yield return new("הצייר Vincent van Gogh היה מפורסם.", ExpectedScript.Hebrew, new[] { L, L, L });
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
