using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// b4 — near-duplicate collapse. The dedup key is a SHA-256 of the exact rationale, so it cannot absorb the
/// model RE-WORDING the same finding; <see cref="NearDuplicateCollapser"/> is the pass that does.
///
/// EVERY Hebrew string below is a REAL rationale, lifted verbatim from the 20 BookFinding rows that were deleted
/// from book 2cf6fcf2-959c-4d4b-afbe-df54831b8973 on 2026-07-12 (all 20 had DISTINCT dedup keys; roughly 10 were
/// real findings). Nothing here is invented: the threshold is tuned against this set and these tests are the
/// regression fence around that tuning.
/// </summary>
public class BookFindingNearDuplicateCollapseTests
{
    // ── The gold set: dimension, severity, rationale (verbatim from the deleted rows) ─────────────────

    // tone — ONE finding (rapid shift from excitement to painful drama) emitted FOUR times.
    private const string ToneA = "המעבר המהיר מאווירת ריגוש וביטחון עצמי לדרמה כואבת ומלומלת יוצר מתח ספרותי משמעותי."; // sev 2
    private const string ToneB = "המעבר המהיר מאווירת ריגוש לדרמה כואבת יוצר מתח ספרותי משמעותי.";                        // sev 1
    private const string ToneC = "המעבר בין אווירת ריגוש לדרמה כואבת מחזק את המתח הסיפורי לאורך העלילה.";                 // sev 1
    private const string ToneD = "השינוי באווירה מריגוש לדרמה כואבת מחזק את המתח הסיפורי.";                                // sev 1

    // character — Morgan's arc, emitted twice; the second is a strict SUPERSET (one adjective added: "ומרשימה").
    private const string MorganShort = "מורגן מציג קשת דמויות ברורה של התמודדות עם פחד וניסיון להתגבר עליו באופן עצמאי.";
    private const string MorganLong = "מורגן מציג קשת דמויות ברורה ומרשימה של התמודדות עם פחד וניסיון להתגבר עליו באופן עצמאי.";

    // theme — physical healing vs the unfixable psychological break, emitted twice ("הניכר" inserted).
    private const string HealingShort = "הטקסט מצליח להמחיש את המרחק שבין ריפוי פיזי לבין השבר הנפשי שאינו ניתן לתיקון.";
    private const string HealingLong = "הטקסט מצליח להמחיש את המרחק הניכר בין ריפוי פיזי לבין השבר הנפשי שאינו ניתן לתיקון.";

    // theme — fear as a physical/spiritual force, emitted twice at DIFFERENT severities (1 and 3).
    private const string FearSev1 = "התמה של הפחד ככוח פיזי ורוחני מודגשת היטב דרך המורחת של מירנדה והפציעה של מורגן.";
    private const string FearSev3 = "התמה של הפחד ככוח פיזי ורוחני המשתק את הגוף והנפש מודגשת היטב דרך פציעותיהם של מורגן ומירנדה.";

    // theme — a DIFFERENT finding that also talks about fear as a force. This is the closest DISTINCT pair in the
    // whole gold set (it scores 0.273 against FearSev1) and is therefore the precision fence for the threshold.
    private const string BookFear = "הספר בונה תמטיות חזקות סביב הפחד ככוח משותט והגבול שבין הכוח הפיזי לחוזקה הנפשית.";

    // character — Mitran's silence (x2), and the Tanari/Mitran contrast (x2): two DIFFERENT findings.
    private const string SilenceA = "השתיקה של מיטרן בסוף הפרק מעוררת סוגיה על תפקידו כמטפל ועל המצוקה האישית שלו.";     // sev 2
    private const string SilenceB = "שתיקתו של מיטרן מעוררת סוגיות עמוקות על תפקידו כמטפל ועל המצוקה האישית שלו.";        // sev 1
    private const string ContrastA = "הניגודיות בין המנהיגות הקרה של טנארי לבין הסבל האישי של מיטרן יוצרת עומק לדמויות."; // sev 1
    private const string ContrastB = "הניגודיות בין המנהיגות הקרה והאדישה של טנארי לבין הסבל האישי והמוסרי של מיטרן מעמיקה את הדרמה."; // sev 2

    // theme — Tanari embodies the price of leadership, emitted twice ("הדמות של" → "דמותו של", "המחיר הכבד").
    private const string TanariA = "הדמות של טנארי ממחישה את נושא אחריות המנהיגות והמחיר של בחירות קשות.";
    private const string TanariB = "דמותו של טנארי ממחישה את נושא אחריות המנהיגות והמחיר הכבד של בחירות קשות.";

    // plot — Mitran's involvement with Miranda: the same finding, most heavily re-worded of all the true dupes.
    private const string PlotA = "המצוקה של מירנדה מעוררת שאלות על תפקידו של מיטרן כמגן לעומת המשימה המוטלת עליו.";
    private const string PlotB = "המעורבות של מיטרן במצבה של מירנדה מעוררת שאלות על תפקידו כמגן או כמשתמע בזכות המשימה.";

    private const string Continuity = "המבנה של המבחן משמש ככלי לחתירה על גבולות האחריות והמוסר של מנהיגות.";

    /// <summary>Builds a candidate exactly as the build pipeline would: the dedup key is the REAL one, derived
    /// from (dimension, resolved order, rationale), so the survivor tie-break sees production values.</summary>
    private static NearDuplicateCollapser.Candidate Candidate(string dimension, int severity, string rationale, int? order)
        => new(
            new BookFinding
            {
                Dimension = dimension,
                Severity = severity,
                Rationale = rationale,
                Verdict = "keep",
                Status = "open",
                DedupKey = BookFinding.ComputeDedupKey(dimension, order, rationale),
            },
            order);

    private static List<string> Rationales(IEnumerable<BookFinding> findings) => findings.Select(f => f.Rationale).ToList();

    // ── 1. The real captured near-dupes collapse ─────────────────────────────────────────────────────

    [Fact]
    public void Collapse_FourRealTuneVariantsOfOneFinding_CollapseToOne()
    {
        // The user-visible symptom: FOUR rows in the tone dimension, all saying the excitement gives way to
        // painful drama. Four distinct dedup keys, so the exact-key dedup let all four through.
        var candidates = new[]
        {
            Candidate("tone", 2, ToneA, 0),
            Candidate("tone", 1, ToneB, 0),
            Candidate("tone", 1, ToneC, 0),
            Candidate("tone", 1, ToneD, 0),
        };
        Assert.Equal(4, candidates.Select(c => c.Finding.DedupKey).Distinct().Count()); // premise: exact dedup is helpless

        var kept = NearDuplicateCollapser.Collapse(candidates);

        Assert.Single(kept);
        Assert.Equal(ToneA, kept[0].Rationale); // the severity-2 variant (see the survivor-rule tests)
    }

    [Fact]
    public void Collapse_RealCharacterPair_OneWordAdded_CollapsesToOne()
    {
        // "קשת דמויות ברורה" vs "קשת דמויות ברורה ומרשימה": a strict SUPERSET — the containment half of the metric.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("character", 1, MorganShort, 0),
            Candidate("character", 1, MorganLong, 0),
        });

        Assert.Single(kept);
        Assert.Equal(MorganLong, kept[0].Rationale);
    }

    [Fact]
    public void Collapse_RealThemePair_OneWordAdded_CollapsesToOne()
    {
        // "את המרחק שבין" vs "את המרחק הניכר בין".
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("theme", 1, HealingShort, 0),
            Candidate("theme", 1, HealingLong, 0),
        });

        Assert.Single(kept);
        Assert.Equal(HealingLong, kept[0].Rationale);
    }

    [Fact]
    public void Collapse_TheWholeGoldSet_TwentyRealRows_CollapseToTenRealFindings()
    {
        // ALL 20 rows the user actually saw, in the dimension buckets the model anchored them to (post-b1 the
        // book's single chapter resolves to order 0 for every one of them). 20 rows → 10 real findings, which is
        // exactly the "~8-10 real ones" the live-DB diagnosis called out.
        var kept = NearDuplicateCollapser.Collapse(GoldSet());

        Assert.Equal(10, kept.Count);
        Assert.Equal(1, kept.Count(f => f.Dimension == "continuity"));
        Assert.Equal(1, kept.Count(f => f.Dimension == "tone"));   // 4 → 1
        Assert.Equal(1, kept.Count(f => f.Dimension == "plot"));   // 2 → 1
        Assert.Equal(3, kept.Count(f => f.Dimension == "character")); // 6 → 3 (silence, contrast, Morgan)
        Assert.Equal(4, kept.Count(f => f.Dimension == "theme"));  // 7 → 4 (Tanari, fear, healing, + the DISTINCT BookFear)
    }

    private static List<NearDuplicateCollapser.Candidate> GoldSet() => new()
    {
        Candidate("continuity", 2, Continuity, 0),
        Candidate("theme", 1, TanariA, 0),
        Candidate("tone", 2, ToneA, 0),
        Candidate("plot", 2, PlotA, 0),
        Candidate("theme", 1, TanariB, 0),
        Candidate("character", 2, SilenceA, 0),
        Candidate("tone", 1, ToneB, 0),
        Candidate("character", 1, SilenceB, 0),
        Candidate("theme", 1, FearSev1, 0),
        Candidate("plot", 2, PlotB, 0),
        Candidate("character", 1, ContrastA, 0),
        Candidate("tone", 1, ToneC, 0),
        Candidate("theme", 3, FearSev3, 0),
        Candidate("character", 2, ContrastB, 0),
        Candidate("theme", 1, BookFear, 0),
        Candidate("tone", 1, ToneD, 0),
        Candidate("character", 1, MorganShort, 0),
        Candidate("theme", 1, HealingShort, 0),
        Candidate("character", 1, MorganLong, 0),
        Candidate("theme", 1, HealingLong, 0),
    };

    // ── 2. Genuinely distinct findings must NOT collapse (the precision side) ────────────────────────

    [Fact]
    public void Collapse_DistinctFindingsInTheSameDimensionAndChapter_AreAllKept()
    {
        // Three REAL theme findings and three REAL character findings from the same book, same chapter. A
        // threshold that collapses everything is not a fix — it silently DELETES findings the user paid for.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("theme", 1, FearSev1, 0),
            Candidate("theme", 1, BookFear, 0),      // closest distinct pair in the gold set (0.273)
            Candidate("theme", 1, HealingShort, 0),
            Candidate("character", 2, SilenceA, 0),
            Candidate("character", 1, ContrastA, 0),
            Candidate("character", 1, MorganShort, 0),
        });

        Assert.Equal(6, kept.Count);
    }

    [Fact]
    public void Similarity_TheMarginBetweenTheClosestTrueDupeAndTheClosestDistinctPair_StraddlesTheThreshold()
    {
        // The tuning evidence, asserted. If a future normalization change narrows this margin, THIS test is what
        // catches it — the collapse counts above would still pass long after the margin had gone.
        var closestTrueDupe = NearDuplicateCollapser.Similarity(          // the most-reworded tone variant vs its survivor
            NearDuplicateCollapser.ContentTokens(ToneA),
            NearDuplicateCollapser.ContentTokens(ToneC));
        var closestDistinctPair = NearDuplicateCollapser.Similarity(      // two DIFFERENT theme findings, both about fear
            NearDuplicateCollapser.ContentTokens(FearSev1),
            NearDuplicateCollapser.ContentTokens(BookFear));

        Assert.True(closestTrueDupe >= NearDuplicateCollapser.DefaultThreshold,
            $"true near-dupe scored {closestTrueDupe:0.000}, below the {NearDuplicateCollapser.DefaultThreshold} threshold");
        Assert.True(closestDistinctPair < NearDuplicateCollapser.DefaultThreshold,
            $"distinct pair scored {closestDistinctPair:0.000}, at/above the {NearDuplicateCollapser.DefaultThreshold} threshold");

        // P3-12: PIN THE EXACT NUMBERS, not just the boolean outcome above — mirroring b4c's
        // Similarity_TheCrossDimensionMargin test below. A test that only checks ">= threshold" / "< threshold"
        // would still pass long after a normalization change eroded the margin to a hair's width; pinning the
        // literal scores makes ANY drift in either number fail loudly, not just a drift past the threshold itself.
        Assert.Equal(0.600, closestTrueDupe, 3);
        Assert.Equal(0.273, closestDistinctPair, 3);

        // The measured window on the real data is (0.273, 0.600]; keep a visible floor under the margin so a
        // change that merely *squeaks* past the two assertions above still fails loudly.
        Assert.True(closestTrueDupe - closestDistinctPair > 0.2,
            $"margin collapsed to {closestTrueDupe - closestDistinctPair:0.000} (true={closestTrueDupe:0.000}, distinct={closestDistinctPair:0.000})");
    }

    // ── 3. Bucketing: dimension + RESOLVED chapter order ─────────────────────────────────────────────

    [Fact]
    public void Collapse_SameRationaleUnderDifferentChapterOrders_DoesNotCollapse()
    {
        // The SAME sentence about chapter 3 and about chapter 7 is TWO findings. Bucketing on the resolved order
        // is what keeps them apart — collapsing them would silently drop a real, correctly anchored finding.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("tone", 2, ToneA, 3),
            Candidate("tone", 2, ToneA, 7),
        });

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Collapse_NoAnchorFinding_FoldsIntoTheChapterZeroFinding_ThoughTheirKeysStillDiffer()
    {
        // RE-PINNED BY b4b (this test previously asserted the two rows do NOT collapse). Chapter 0 is a REAL,
        // ANCHORED chapter — it is 0-based, not a sentinel — so under the b4b fold rule a book-wide copy of the
        // same finding folds INTO it, exactly as it would into chapter 9. Treating chapter 0 as a special
        // non-chapter here would resurrect the very sentinel thinking b3 killed.
        var noAnchor = Candidate("tone", 2, ToneA, null); // book-wide
        var chapterZero = Candidate("tone", 2, ToneA, 0); // first chapter

        var kept = NearDuplicateCollapser.Collapse(new[] { noAnchor, chapterZero });

        var survivor = Assert.Single(kept);
        Assert.Same(chapterZero.Finding, survivor); // the ANCHORED copy survives; the user keeps the chapter link.

        // What b3 fixed is INTACT and is a DIFFERENT property: the two still hash to DIFFERENT dedup keys. That
        // matters — pre-b3 they COLLIDED on one key, so one row was dropped silently and arbitrarily (it could
        // just as easily have been the navigable chapter-0 copy that lost). b4b also reduces them to one row, but
        // by a STATED rule that always keeps the anchored copy, which is not the same thing as a hash collision.
        Assert.NotEqual(
            BookFinding.ComputeDedupKey("tone", null, ToneA),
            BookFinding.ComputeDedupKey("tone", 0, ToneA));

        // ...and two BOOK-WIDE re-wordings of one finding still collapse with each other (b4, unchanged).
        var bookWide = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("tone", 2, ToneA, null),
            Candidate("tone", 1, ToneC, null),
        });
        Assert.Single(bookWide);
    }

    // ═══ 6. b4b — THE CROSS-BUCKET FOLD ══════════════════════════════════════════════════════════════
    //
    // b4 bucketed strictly on (dimension, resolved order), so a finding the WINDOWED engine emitted twice with
    // DIFFERENT anchors never met itself and both copies persisted. Measured on the live 17-chapter book
    // A63A6E02 AFTER the b1..b4 rebuild: SEVEN surviving duplicate pairs, every single one a NO-ANCHOR copy
    // beside an ANCHORED copy of the same finding. Every Hebrew string in this section is a REAL rationale,
    // lifted verbatim from that book's BookFindings rows on 2026-07-12.
    //
    // THE RULE: fold a no-anchor copy into an anchored one; NEVER merge two copies anchored to different REAL
    // chapters (that would silently delete a finding the user paid for).

    // The 7 real cross-bucket pairs (no-anchor copy, anchored copy, the chapter the anchored copy names).
    // Measured similarities, in order: 0.889, 0.750, 0.857, 0.889, 1.000, 0.889, 0.917 — all far above the 0.45
    // threshold, while the closest DISTINCT pair in the same book scores 0.077.
    private static readonly (string Dimension, string NoAnchor, string Anchored, int Order)[] RealCrossBucketPairs =
    {
        ("character",
            "דמותה של מררה מוסיפה רובד רגשי חשוב דרך דאגה שאינה מתבטאת.",
            "הדמות של מררה מוסיפה רובד רגשי חשוב; הדאגה שלה שאינה מתבטאת יוצרת עומק ומתח פנימי.", 1),
        ("character",
            "התפתחותה של תמר מתוך שתיקה אל מעורבות פעילה וקבלת אחריות מהווה עמוד שדרה רגשי חזק.",
            "התפתחותה של תמר מתוך שתיקה וערפל זכרון אל מעורבות פעילה וקבלת אחריות היא חזקה מרכזית.", 0),
        ("continuity",
            "המצב הפיזי של דניאל נשמר בעקביות לאורך הפרקים.",
            "המצב הפיזי של דניאל (חוסר שינה ושימוש במונח 'כרגיל') נשמר בעקביות בין הפרקים.", 1),
        ("theme",
            "השילוב בין המדע הגיאולוגי לבין הזיכרון האישי יוצר עומק פואטי ומשמעותי המעניק לספר את ייחודו.",
            "השילוב בין המדע (תדרים) לבין הזיכרון האישי יוצר עומק פואטי ומשמעותי.", 2),
        ("theme",
            "שימוש בחוסר שינה ככלי לתיאור הבידוד של דניאל מחזק את נושא המרחק מהסביבה.",
            "השימוש בחוסר שינה ככלי לתיאור המרחק בין דניאל לסביבתו המיידית מחזק את נושא הבידוד.", 1),
        ("tone",
            "המעבר בין המציאות הפיזית של דניאל לבין עולם הכתיבה של הסופר הוא חד מדי.",
            "המעבר בין המציאות הפיזית של דניאל לבין עולם הכתיבה של הסופר הוא חד מאוד; כדאי לשקול מעבר רעיוני חלק יותר.", 2),
        ("tone",
            "השימוש בשפה פואטית לתיאור המדבר והאבן כחומר חי מעניק לטקסט עושר רגשי ומרחב ייחודי.",
            "השימוש בשפה פואטית כדי לתאר את המדבר והאבן כחומר חי מעניק לטקסט עושר רגשי.", 4),
    };

    // A real THEME rationale from A63A6E02, and a real CONTINUITY one. The b5 gate caught this book persisting
    // rationales BYTE-IDENTICALLY twice — once with no anchor, once anchored (theme at order 9, continuity at
    // order 12) — because the dedup key folds the primary order in, so identical prose + different order = two
    // different hashes = two rows. Not even the EXACT-text dedup can see these; only the fold can.
    private const string ByteIdenticalTheme = "המעבר מזיכרון אישי לזיכרון קולקטיבי (יומן של כולם) מעניק לסוף הספר משמעות רחבה.";
    private const string ByteIdenticalContinuity = "המעבר בין עולם נווה-חול לבין המרחב של הנמל יוצר תחושת ניתוק פיזי שדורשת חיבור טוב יותר.";

    [Fact]
    public void Collapse_ByteIdenticalRationale_NoAnchorAndOrderNine_CollapsesToOneAnchoredRow()
    {
        var noAnchor = Candidate("theme", 2, ByteIdenticalTheme, null);
        var anchored = Candidate("theme", 2, ByteIdenticalTheme, 9);

        // PREMISE: the two dedup keys DIFFER even though the prose is byte-identical, because the key hashes the
        // primary chapter order alongside the text. This is why the exact-key dedup persisted both rows.
        Assert.NotEqual(noAnchor.Finding.DedupKey, anchored.Finding.DedupKey);

        var kept = NearDuplicateCollapser.Collapse(new[] { noAnchor, anchored });

        var survivor = Assert.Single(kept);
        Assert.Same(anchored.Finding, survivor); // ANCHORED survives → the user keeps the chapter-9 link.
    }

    [Fact]
    public void Collapse_ByteIdenticalRationale_NoAnchorAndOrderTwelve_CollapsesToOneAnchoredRow()
    {
        var noAnchor = Candidate("continuity", 2, ByteIdenticalContinuity, null);
        var anchored = Candidate("continuity", 2, ByteIdenticalContinuity, 12);
        Assert.NotEqual(noAnchor.Finding.DedupKey, anchored.Finding.DedupKey);

        var kept = NearDuplicateCollapser.Collapse(new[] { noAnchor, anchored });

        var survivor = Assert.Single(kept);
        Assert.Same(anchored.Finding, survivor);
    }

    [Fact]
    public void Collapse_TheSevenRealCrossBucketPairsFromBookA63A6E02_EachFoldIntoTheirAnchoredCopy()
    {
        // The whole residual the b5 gate found: 14 rows the user actually sees, which are 7 findings.
        var candidates = new List<NearDuplicateCollapser.Candidate>();
        foreach (var (dimension, noAnchor, anchored, order) in RealCrossBucketPairs)
        {
            candidates.Add(Candidate(dimension, 1, noAnchor, null));
            candidates.Add(Candidate(dimension, 1, anchored, order));
        }

        var kept = NearDuplicateCollapser.Collapse(candidates);

        Assert.Equal(7, kept.Count);
        // EVERY survivor is the ANCHORED copy: not one finding lost its chapter link to the fold.
        foreach (var (_, _, anchored, _) in RealCrossBucketPairs)
            Assert.Contains(kept, f => f.Rationale == anchored);
        foreach (var (_, noAnchor, _, _) in RealCrossBucketPairs)
            Assert.DoesNotContain(kept, f => f.Rationale == noAnchor);
    }

    // ── 6a. THE PRECISION FENCE: two copies on DIFFERENT REAL chapters NEVER fold ────────────────────

    [Fact]
    public void MayFold_TwoDifferentRealChapters_ReturnsFalse()
    {
        // P3-13. The different-real-chapters fence is implemented in THREE places: pass 1's bucket key (different
        // orders land in different buckets, so they are never compared at all), pass 2's structure (only
        // no-anchor-vs-anchored pairs are ever compared, so two anchored orders never reach a similarity check),
        // and MayFold itself (the only one of the three pass 3 — the cross-dimension fold — actually calls). The
        // tests around this one exercise the fence through Collapse, which mostly pins the BUCKET KEY / pass
        // structure rather than MayFold's own boolean. This test pins MayFold directly, with teeth: if its
        // predicate is ever "simplified" to something permissive (e.g. treating any two non-null orders as
        // foldable), THIS test goes red even though every bucket-key-level test above might still pass.
        Assert.False(NearDuplicateCollapser.MayFold(3, 9));
        Assert.False(NearDuplicateCollapser.MayFold(0, 1)); // real chapter 0 (the sentinel-collision case b3 fixed) too

        // The two permissive cases, pinned alongside so a future edit cannot "fix" the assertion above by making
        // MayFold permissive in the other direction instead.
        Assert.True(NearDuplicateCollapser.MayFold(null, 9));  // one side book-wide: the b4b wildcard
        Assert.True(NearDuplicateCollapser.MayFold(5, 5));     // same real chapter: b4's own bucket
        Assert.True(NearDuplicateCollapser.MayFold(null, null)); // both book-wide
    }

    [Fact]
    public void Collapse_TwoCopiesAnchoredToDifferentRealChapters_NeverFold_EvenWhenByteIdentical()
    {
        // THE ASYMMETRY IS THE POINT. "The pacing drags" about chapter 3 and about chapter 9 may be two genuinely
        // distinct findings, and a false merge SILENTLY DELETES one the user paid for — strictly worse than the
        // visible duplicate it would be curing. So the fence holds even at similarity 1.000: identical prose about
        // two different chapters is TWO findings. Only the no-anchor/anchored asymmetry is ever merged.
        var chapterThree = Candidate("theme", 2, ByteIdenticalTheme, 3);
        var chapterNine = Candidate("theme", 3, ByteIdenticalTheme, 9);

        var kept = NearDuplicateCollapser.Collapse(new[] { chapterThree, chapterNine });

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, f => ReferenceEquals(f, chapterThree.Finding));
        Assert.Contains(kept, f => ReferenceEquals(f, chapterNine.Finding));

        // ...and adding a BOOK-WIDE copy of the same text does not licence merging the two anchored ones. The
        // book-wide copy folds into exactly ONE of them (deterministically, the better target under the survivor
        // ordering); BOTH anchored findings survive.
        var bookWide = Candidate("theme", 1, ByteIdenticalTheme, null);
        var threeWay = NearDuplicateCollapser.Collapse(new[] { bookWide, chapterThree, chapterNine });

        Assert.Equal(2, threeWay.Count);
        Assert.Contains(threeWay, f => ReferenceEquals(f, chapterThree.Finding));
        Assert.Contains(threeWay, f => ReferenceEquals(f, chapterNine.Finding));
        Assert.DoesNotContain(threeWay, f => ReferenceEquals(f, bookWide.Finding));
    }

    [Fact]
    public void Collapse_CrossBucketFold_NeverCrossesDimensions_AtTheWithinDimensionThreshold()
    {
        // ★ THE PRECISION FENCE FOR b4c. A REAL pair from A63A6E02: the same observation about the world shifting
        // from Neve-Hol to the harbour, filed by the model under CONTINUITY and under PLOT — but they are TWO
        // findings (one says the transition needs a better bridge, the other places it in the late chapters), and
        // the user reads them in two lists. They score 0.818 on the production metric.
        //
        // THIS IS THE PAIR THAT FORBIDS THE OBVIOUS FIX. The cross-dimension duplicate b4c exists to kill could
        // NOT be closed by simply dropping the dimension from the bucket key, or by re-using the 0.45
        // within-dimension threshold across dimensions: either would merge THIS pair at 0.818 and silently DELETE
        // a finding the user paid for. So b4c collapses across dimensions only at NEAR-IDENTITY (0.90), and this
        // pair — which sits 0.082 below that line, and would fold on text alone under the 0.45 rule — stays split.
        Assert.True(
            NearDuplicateCollapser.Similarity(
                NearDuplicateCollapser.ContentTokens(RealDistinctContinuity),
                NearDuplicateCollapser.ContentTokens(RealDistinctPlot)) >= NearDuplicateCollapser.DefaultThreshold,
            "premise: at the WITHIN-dimension threshold these two would merge — the cross-dimension cut-off is what keeps them apart");

        // MayFold permits it (one copy is book-wide), the dimensions differ, and the text is close: the ONLY thing
        // standing between this real finding and deletion is the near-identity threshold. Assert it hard.
        Assert.True(NearDuplicateCollapser.MayFold(null, 12));

        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("continuity", 2, RealDistinctContinuity, null),
            Candidate("plot", 2, RealDistinctPlot, 12),
        });

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, f => f.Dimension == "continuity" && f.Rationale == RealDistinctContinuity);
        Assert.Contains(kept, f => f.Dimension == "plot" && f.Rationale == RealDistinctPlot);
    }

    [Fact]
    public void Collapse_CrossBucketFold_ObeysTheShortRationaleGuard()
    {
        // MinContentTokens applies ACROSS buckets exactly as it does within one: "the pacing is too slow" and
        // "the pacing is too fast" mean OPPOSITE things and share 2 of 3 content tokens, so no threshold can tell
        // them apart. Anchoring one of them must not open a back door around the guard.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("pacing", 2, "הקצב איטי מדי.", null),
            Candidate("pacing", 2, "הקצב מהיר מדי.", 3),
        });

        Assert.Equal(2, kept.Count);
    }

    // ── 6b. The fold's survivor rule ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Collapse_TheAnchoredCopySurvives_EvenWhenTheNoAnchorCopyWouldWinTheSurvivorRule()
    {
        // b4's survivor rule is severity-first, then most-specific. Here the NO-ANCHOR copy wins on BOTH: it is
        // severity 3 (the anchored copy is 1) and it is the longer, more specific phrasing. It STILL must not
        // survive — winning would cost the user the chapter link, and an anchored finding they can navigate to is
        // worth more than a slightly better sentence they cannot. Anchoredness is a CONSTRAINT here, not a
        // tie-break.
        var noAnchor = Candidate("theme", 3,
            "השילוב בין המדע הגיאולוגי לבין הזיכרון האישי יוצר עומק פואטי ומשמעותי המעניק לספר את ייחודו.", null);
        var anchored = Candidate("theme", 1,
            "השילוב בין המדע (תדרים) לבין הזיכרון האישי יוצר עומק פואטי ומשמעותי.", 2);

        var kept = NearDuplicateCollapser.Collapse(new[] { noAnchor, anchored });

        var survivor = Assert.Single(kept);
        Assert.Same(anchored.Finding, survivor);

        // ...but the survivor takes the MAX severity of the folded pair. Keeping the anchored copy's severity 1
        // verbatim would let an arbitrary anchor coin-flip silently DOWNGRADE a major finding to a minor one —
        // the exact harm b4's severity-first rule exists to prevent, and severity is what the user triages on.
        // Severity is a scalar, not prose: lifting it fabricates no text (no rationale, action or evidence is ever
        // merged across copies), and it is not a dedup-key input, so no key moves.
        Assert.Equal(3, survivor.Severity);
    }

    [Fact]
    public void Collapse_TheFoldIsIndependentOfTheOrderTheWindowsEmittedTheCopiesIn()
    {
        // The windowed engine unions several passes, so input order carries no meaning. Max-severity is
        // commutative and anchored targets are never absorbed, so the fold is order-independent by construction.
        static List<NearDuplicateCollapser.Candidate> Pairs()
        {
            var list = new List<NearDuplicateCollapser.Candidate>();
            foreach (var (dimension, noAnchor, anchored, order) in RealCrossBucketPairs)
            {
                list.Add(Candidate(dimension, 3, noAnchor, null)); // the book-wide copy is the HIGHER severity
                list.Add(Candidate(dimension, 1, anchored, order));
            }
            return list;
        }

        var forward = NearDuplicateCollapser.Collapse(Pairs());
        var reversed = NearDuplicateCollapser.Collapse(Enumerable.Reverse(Pairs()).ToList());

        Assert.Equal(
            Rationales(forward).OrderBy(r => r, StringComparer.Ordinal),
            Rationales(reversed).OrderBy(r => r, StringComparer.Ordinal));
        Assert.All(forward, f => Assert.Equal(3, f.Severity));  // every survivor took the max severity
        Assert.All(reversed, f => Assert.Equal(3, f.Severity));
    }

    [Fact]
    public void Collapse_ByteIdenticalRationaleInDifferentDimensions_CollapsesToOne_UnderTheNearIdentityRule()
    {
        // RE-PINNED BY b4c (this test previously asserted the two rows do NOT collapse). A BYTE-IDENTICAL sentence
        // filed under two dimensions scores 1.000: it is one finding the model filed twice, and it is exactly what
        // the user reported seeing ("multiple identical items"). Under b4c's near-identity rule it now renders once.
        //
        // What b4/b4b established is INTACT and is a DIFFERENT property — the dimension still bucket-separates
        // everything BELOW near-identity, which is what the 0.818 fence test above pins. Only identity crosses.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("tone", 2, ToneA, 0),
            Candidate("theme", 2, ToneA, 0),
        });

        var survivor = Assert.Single(kept);

        // The survivor keeps its OWN dimension label, verbatim. No merged label is fabricated and neither copy is
        // relabelled into the other's dimension — Dimension is a DEDUP-KEY input, so rewriting it would move the
        // key b3 owns and re-orphan the user's Status on rebuild.
        Assert.Contains(survivor.Dimension, new[] { "tone", "theme" });
        Assert.Equal(ToneA, survivor.Rationale);

        // Here the two copies are indistinguishable on every axis that matters (both anchored to chapter 0, same
        // severity, same tokens), so the winner falls to the ordinal-smallest dedup key: arbitrary, but STABLE —
        // an identical build always yields the identical survivor, whichever order the windows emitted them in.
        var reversed = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("theme", 2, ToneA, 0),
            Candidate("tone", 2, ToneA, 0),
        });
        Assert.Equal(survivor.Dimension, Assert.Single(reversed).Dimension);
    }

    // ═══ 7. b4c — THE CROSS-DIMENSION NEAR-IDENTITY FOLD ═════════════════════════════════════════════
    //
    // The THIRD duplicate class, found ON SCREEN after the b5 acceptance gate had passed: the model emits ONE
    // sentence and files it under TWO dimensions. b4 buckets by dimension and b4b folds only within one, so the two
    // copies never met and BOTH reached the user. Live in book A63A6E02 on 2026-07-13 (both rationales below are
    // verbatim from the DB):
    //   card 3  [character, chapter 2]   "...לדמות הסופר בפרק Ktiv יוצר שינוי חד בטון..."
    //   card 5  [continuity, NO ANCHOR]  the same sentence, minus "בפרק Ktiv"
    // They score 1.000 on the production metric — the shorter copy's tokens are a strict SUBSET of the longer's,
    // which is what "the same sentence, filed twice" looks like to a bag-of-words metric.
    //
    // THE HAZARD that shapes the whole design is the fence test above: the SAME book holds a genuinely DISTINCT
    // continuity/plot pair scoring 0.818, so this pass runs at NEAR-IDENTITY (0.90) and NOT at b4's 0.45.

    private const string CrossDimCharacterAnchored =
        "המעבר לדמות הסופר בפרק Ktiv יוצר שינוי חד בטון ובמצב הנפשי של הגיבור, מה שעלול ליצור תחושת ניתוק.";
    private const string CrossDimContinuityNoAnchor =
        "המעבר לדמות הסופר יוצר שינוי חד בטון ובמצב הנפשי של הגיבור, מה שעלול ליצור תחושת ניתוק.";

    // The 0.818 DISTINCT pair. The continuity half is the very same real row as ByteIdenticalContinuity above —
    // one book, one sentence, two roles in these tests: b4b's cross-bucket duplicate and b4c's precision fence.
    private const string RealDistinctContinuity = ByteIdenticalContinuity;
    private const string RealDistinctPlot = "המעבר בין עולם נווה-חול לעולם הנמל בפרקים המאוחרים יוצר תחושת ניתוק פיזי.";

    [Fact]
    public void Similarity_TheCrossDimensionMargin_RealDuplicateAboveTheThreshold_RealDistinctPairBelowIt()
    {
        // THE TUNING EVIDENCE, ASSERTED — measured on the live rows with the PRODUCTION metric, not on invented
        // strings. If a future normalization change moves either number, THIS test is what fails: the collapse
        // counts below would keep passing long after the margin had gone.
        var realDuplicate = NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(CrossDimCharacterAnchored),
            NearDuplicateCollapser.ContentTokens(CrossDimContinuityNoAnchor));
        var realDistinctPair = NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(RealDistinctContinuity),
            NearDuplicateCollapser.ContentTokens(RealDistinctPlot));

        Assert.Equal(1.000, realDuplicate, 3);      // strict subset → containment 1.0
        Assert.Equal(0.818, realDistinctPair, 3);

        Assert.True(realDuplicate >= NearDuplicateCollapser.CrossDimensionThreshold,
            $"the real cross-dimension DUPLICATE scored {realDuplicate:0.000}, below the {NearDuplicateCollapser.CrossDimensionThreshold} cut-off");
        Assert.True(realDistinctPair < NearDuplicateCollapser.CrossDimensionThreshold,
            $"the real DISTINCT pair scored {realDistinctPair:0.000}, at/above the {NearDuplicateCollapser.CrossDimensionThreshold} cut-off — a real finding would be DELETED");

        // The safe window is (0.818, 1.000]; 0.90 sits near its middle. The margins are MODEST — +0.100 of recall
        // headroom and 0.082 of precision headroom, thinner than b4's 0.177 — which is precisely why the cut-off is
        // near-identity and not "similar". Pin a floor under both sides so a change that merely SQUEAKS past the two
        // assertions above still fails loudly.
        Assert.True(realDuplicate - NearDuplicateCollapser.CrossDimensionThreshold >= 0.05,
            $"recall margin collapsed to {realDuplicate - NearDuplicateCollapser.CrossDimensionThreshold:0.000}");
        Assert.True(NearDuplicateCollapser.CrossDimensionThreshold - realDistinctPair >= 0.05,
            $"precision margin collapsed to {NearDuplicateCollapser.CrossDimensionThreshold - realDistinctPair:0.000}");

        // And the two thresholds must stay far apart: re-using b4's 0.45 across dimensions would merge the 0.818
        // pair and silently delete a real finding.
        Assert.True(NearDuplicateCollapser.CrossDimensionThreshold > NearDuplicateCollapser.DefaultThreshold + 0.3);
    }

    [Fact]
    public void Collapse_TheRealCrossDimensionPairFromBookA63A6E02_CollapsesToOneAnchoredRow()
    {
        // THE USER-VISIBLE BUG. Two cards, one sentence. Note the model even gave them the same severity, so
        // nothing but the anchoredness rule can decide the survivor.
        var characterAnchored = Candidate("character", 2, CrossDimCharacterAnchored, 2); // card 3
        var continuityNoAnchor = Candidate("continuity", 2, CrossDimContinuityNoAnchor, null); // card 5

        // PREMISE: neither the exact key nor b4/b4b could ever have caught this — different dimension AND different
        // order, so the two keys differ, and the dimension bucket keeps the collapser from ever comparing them.
        Assert.NotEqual(characterAnchored.Finding.DedupKey, continuityNoAnchor.Finding.DedupKey);

        var kept = NearDuplicateCollapser.Collapse(new[] { characterAnchored, continuityNoAnchor });

        var survivor = Assert.Single(kept);

        // THE ANCHORED COPY SURVIVES (b4b's constraint, carried across dimensions): the user keeps the chapter-2
        // link, which a book-wide copy cannot give them...
        Assert.Same(characterAnchored.Finding, survivor);
        // ...and the dimension label they see is the SURVIVOR'S OWN — character (דמויות on screen) — verbatim.
        // Nothing is relabelled and no merged label is invented: losing the "continuity" label costs nothing the
        // surviving sentence does not already say, while losing the chapter link would have cost navigation.
        Assert.Equal("character", survivor.Dimension);
        Assert.Equal(CrossDimCharacterAnchored, survivor.Rationale);
    }

    [Fact]
    public void Collapse_CrossDimension_TheSurvivorTakesTheMaxSeverityOfThePair()
    {
        // The anchored copy is FORCED to win regardless of severity, so taking its severity verbatim would let an
        // arbitrary anchor coin-flip silently DOWNGRADE a major finding to a minor one — the exact harm b4's
        // severity-first rule exists to prevent. Severity is a scalar the user triages on, not prose: lifting it
        // fabricates no text (no rationale, action or evidence is EVER merged across copies) and moves no key.
        var characterAnchored = Candidate("character", 1, CrossDimCharacterAnchored, 2);
        var continuityNoAnchor = Candidate("continuity", 3, CrossDimContinuityNoAnchor, null);

        var survivor = Assert.Single(NearDuplicateCollapser.Collapse(new[] { characterAnchored, continuityNoAnchor }));

        Assert.Same(characterAnchored.Finding, survivor);
        Assert.Equal(3, survivor.Severity);
        Assert.Equal(CrossDimCharacterAnchored, survivor.Rationale); // prose untouched — only the scalar moved
    }

    [Fact]
    public void Collapse_CrossDimension_TwoCopiesAnchoredToDifferentRealChapters_NeverMerge_EvenWhenByteIdentical()
    {
        // b4b's precision fence buys NO exemption for crossing a dimension. Identical prose about chapter 3 and
        // about chapter 9 is TWO findings whatever the model filed them under, and a false merge silently deletes
        // one the user paid for — strictly worse than the visible duplicate it would be curing.
        var themeChapterThree = Candidate("theme", 2, ByteIdenticalTheme, 3);
        var plotChapterNine = Candidate("plot", 3, ByteIdenticalTheme, 9);

        var kept = NearDuplicateCollapser.Collapse(new[] { themeChapterThree, plotChapterNine });

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, f => ReferenceEquals(f, themeChapterThree.Finding));
        Assert.Contains(kept, f => ReferenceEquals(f, plotChapterNine.Finding));
    }

    [Fact]
    public void Collapse_CrossDimension_ABookWideCopyDoesNotLicenceMergingTwoAnchoredCopies()
    {
        // The transitivity trap: A (theme, ch3) and B (plot, ch9) may not merge, but a book-wide copy in a THIRD
        // dimension near-duplicates BOTH. It must fold into exactly ONE of them (deterministically, the better
        // target under the survivor order) and must NOT become a bridge that merges A with B — anchored copies are
        // never absorbed as a side effect of someone else's fold, so both real findings survive.
        var themeChapterThree = Candidate("theme", 2, ByteIdenticalTheme, 3);
        var plotChapterNine = Candidate("plot", 3, ByteIdenticalTheme, 9);
        var bookWide = Candidate("continuity", 1, ByteIdenticalTheme, null);

        var kept = NearDuplicateCollapser.Collapse(new[] { bookWide, themeChapterThree, plotChapterNine });

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, f => ReferenceEquals(f, themeChapterThree.Finding));
        Assert.Contains(kept, f => ReferenceEquals(f, plotChapterNine.Finding));
        Assert.DoesNotContain(kept, f => ReferenceEquals(f, bookWide.Finding));
    }

    [Fact]
    public void Collapse_CrossDimension_ObeysTheShortRationaleGuard()
    {
        // MinContentTokens applies across DIMENSIONS exactly as it does within one. These two say OPPOSITE things
        // and share 2 of 3 content tokens; filing them under different dimensions must not open a back door around
        // the guard any more than anchoring one of them did (b4b).
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("pacing", 2, "הקצב איטי מדי.", null),
            Candidate("tone", 2, "הקצב מהיר מדי.", 3),
        });

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Collapse_CrossDimensionPass_DoesNotChangeAnyWithinDimensionOutcome()
    {
        // THE ASYMMETRY, STATED AS A TEST. The same 0.600 similarity means "one finding" inside a dimension and
        // "two findings" across dimensions — because a shared vocabulary across two dimensions is WEAK evidence of
        // a shared finding (the 0.818 fence pair proves it). So the cross-dimension pass runs at a SEPARATE, much
        // higher cut-off and can only ever compare candidates whose dimensions DIFFER: it cannot raise b4's bar,
        // and it cannot lower it.
        var withinDimension = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("tone", 2, ToneA, 0),
            Candidate("tone", 1, ToneC, 0), // scores 0.600 against ToneA — above 0.45, below 0.90
        });
        Assert.Single(withinDimension); // b4, unchanged

        var acrossDimensions = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("tone", 2, ToneA, 0),
            Candidate("theme", 1, ToneC, 0), // the SAME 0.600, now across dimensions
        });
        Assert.Equal(2, acrossDimensions.Count); // b4c does not reach down to 0.45

        // ...and the whole 20-row gold set still collapses to exactly the 10 real findings it did under b4.
        Assert.Equal(10, NearDuplicateCollapser.Collapse(GoldSet()).Count);
    }

    [Fact]
    public void Collapse_CrossDimensionFold_IsIndependentOfTheOrderTheWindowsEmittedTheCopiesIn()
    {
        // The windowed engine unions several passes, so input order carries no meaning. The pass walks a TOTAL
        // order derived from the data (anchoredness, severity, specificity, key), never from the input sequence.
        var pairs = new List<NearDuplicateCollapser.Candidate>
        {
            Candidate("character", 2, CrossDimCharacterAnchored, 2),
            Candidate("continuity", 3, CrossDimContinuityNoAnchor, null),
            Candidate("continuity", 2, RealDistinctContinuity, null), // the 0.818 fence pair rides along:
            Candidate("plot", 2, RealDistinctPlot, 12),               // it must survive in BOTH orders
        };

        var forward = NearDuplicateCollapser.Collapse(pairs);
        var reversed = NearDuplicateCollapser.Collapse(Enumerable.Reverse(pairs).ToList());

        Assert.Equal(3, forward.Count);
        Assert.Equal(
            forward.Select(f => f.Dimension + "|" + f.Rationale).OrderBy(s => s, StringComparer.Ordinal),
            reversed.Select(f => f.Dimension + "|" + f.Rationale).OrderBy(s => s, StringComparer.Ordinal));

        // The anchored character copy survived (with the max severity), the book-wide continuity copy of it did
        // not, and the DISTINCT continuity/plot pair is untouched — in both directions.
        foreach (var kept in new[] { forward, reversed })
        {
            Assert.Contains(kept, f => f.Rationale == CrossDimCharacterAnchored && f.Dimension == "character" && f.Severity == 3);
            Assert.DoesNotContain(kept, f => f.Rationale == CrossDimContinuityNoAnchor);
            Assert.Contains(kept, f => f.Rationale == RealDistinctContinuity);
            Assert.Contains(kept, f => f.Rationale == RealDistinctPlot);
        }
    }

    // ═══ 8. be-c07 (P2-1) — THE STRICTER BAR FOR CLAIMING A USER-ACTED ROW ═══════════════════════════
    //
    // The persisted fuzzy tier is the ONE place this subsystem's fail-safe bias INVERTS. Every other wrong merge
    // still leaves the user A CARD; a wrong claim on a DISMISSED row means the fresh finding is NOT INSERTED and the
    // row stays dismissed, so a genuinely DISTINCT finding NEVER REACHES THE USER. And MayFold cannot help: a
    // book-wide row's ComparisonOrder is null, which is a WILDCARD, so on such a row the THRESHOLD is the only guard.
    //
    // The bar therefore depends on the COST OF BEING WRONG about the row, not on the text: 0.60 for a user-acted row
    // claimed across an anchor MISMATCH; 0.45 everywhere else. The two numbers below are the measured evidence.

    /// <summary>The REAL severity-3 factual contradiction from book A63A6E02 — the most valuable finding in the
    /// corpus, and the pair that BINDS the precision side of the new bar.</summary>
    private const string Sev3Contradiction =
        "קיימת סתירה עובדתית בין מצב הדמויות בפרקי המעקב (12-15) לבין פרקי העלילה המרכזיים; דמות 'דניאל' אינה מוזכרת בשום מקום אחר בספר, ואין קשר ברור בינה לבין תמר או אדם.";

    /// <summary>A REAL, GENUINELY DISTINCT severity-1 praise from the SAME book and the SAME dimension. It scores
    /// 0.462 against the contradiction — two findings of OPPOSITE polarity that a bag-of-words cannot separate.</summary>
    private const string Sev1DanielPraise = "המשך מצב חוסר השינה של דניאל מפרק 14 ל-15 יוצר רצף פיזי ורגשי ברור.";

    [Fact]
    public void UserActedBar_TheTuningEvidence_TheDistinctPairIsBELOWIt_AndEveryRealRewordingIsABOVEIt()
    {
        // THE PRECISION SIDE, and it is the whole reason the number exists. If the praise had been DISMISSED and left
        // BOOK-WIDE (a MayFold wildcard — and since b7 the engine produces book-wide findings routinely), then at the
        // ordinary 0.45 bar the CONTRADICTION would have claimed that row, never been inserted, and VANISHED.
        var distinctPair = NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(Sev3Contradiction),
            NearDuplicateCollapser.ContentTokens(Sev1DanielPraise));
        Assert.Equal(0.462, distinctPair, 3);
        Assert.True(distinctPair >= NearDuplicateCollapser.DefaultThreshold,
            "premise: the ORDINARY bar does not stop this — that is the bug");
        Assert.True(distinctPair < NearDuplicateCollapser.UserActedAnchorMismatchThreshold,
            $"a real DISTINCT pair scored {distinctPair:0.000}, at/above the {NearDuplicateCollapser.UserActedAnchorMismatchThreshold} " +
            "user-acted bar — the most valuable finding in the corpus would be silently deleted");

        // THE RECALL SIDE — b4b's actual promise ("a dismissed finding must not be resurrected by a rephrase") must
        // survive. The real anchor-MISMATCHED re-wordings are the SEVEN cross-bucket pairs of book A63A6E02: every one
        // of them a book-wide copy beside an anchored copy of the SAME finding. ALL of them must still clear the bar.
        var worstRecall = 1.0;
        foreach (var (_, noAnchor, anchored, _) in RealCrossBucketPairs)
        {
            var sim = NearDuplicateCollapser.Similarity(
                NearDuplicateCollapser.ContentTokens(noAnchor),
                NearDuplicateCollapser.ContentTokens(anchored));
            Assert.True(sim >= NearDuplicateCollapser.UserActedAnchorMismatchThreshold,
                $"a REAL re-wording scored {sim:0.000}, under the {NearDuplicateCollapser.UserActedAnchorMismatchThreshold} bar — " +
                "a dismissed finding would come back as an open card every time the model rephrased it");
            worstRecall = Math.Min(worstRecall, sim);
        }
        Assert.Equal(0.750, worstRecall, 3); // the LOWEST of the seven

        // THE SAFE WINDOW IS (0.462, 0.750] AND 0.60 SITS AT ITS MIDPOINT (0.606). Pin a floor under BOTH margins so
        // a change that merely SQUEAKS past the assertions above still fails loudly.
        Assert.True(NearDuplicateCollapser.UserActedAnchorMismatchThreshold - distinctPair >= 0.10,
            $"precision margin collapsed to {NearDuplicateCollapser.UserActedAnchorMismatchThreshold - distinctPair:0.000}");
        Assert.True(worstRecall - NearDuplicateCollapser.UserActedAnchorMismatchThreshold >= 0.10,
            $"recall margin collapsed to {worstRecall - NearDuplicateCollapser.UserActedAnchorMismatchThreshold:0.000}");

        // ...and the ORDINARY bar keeps its OWN measured window, which this change does not touch: the closest true
        // re-wording in the 2cf6fcf2 gold set is 0.600 and the closest DISTINCT same-dimension pair is 0.273.
        Assert.True(NearDuplicateCollapser.UserActedAnchorMismatchThreshold > NearDuplicateCollapser.DefaultThreshold);
    }

    [Fact]
    public void RequiredPersistedThreshold_IsStricterOnlyWhereAWrongClaimCanMakeAFindingINVISIBLE()
    {
        const double ordinary = NearDuplicateCollapser.DefaultThreshold;                  // 0.45
        const double strict = NearDuplicateCollapser.UserActedAnchorMismatchThreshold;    // 0.60

        // THE ANCHOR MISMATCH (exactly one side book-wide) — MayFold's WILDCARD, where the anchors say NOTHING.
        // On a row the user ACTED on, this is the only fuzzy decision that can SUPPRESS a finding. Stricter.
        Assert.Equal(strict, NearDuplicateCollapser.RequiredPersistedThreshold(12, null, "dismissed"));
        Assert.Equal(strict, NearDuplicateCollapser.RequiredPersistedThreshold(null, 12, "dismissed"));

        // DONE and ACKNOWLEDGED get the SAME bar as DISMISSED, deliberately. All three are rows the user has ACTED
        // on: none is ever deleted (so each is a permanent absorption surface that only accumulates), and a fresh
        // finding that claims one is NOT INSERTED and inherits a status saying the user is finished with it.
        // `dismissed` is the extreme (the card is not even rendered); `done` arrives pre-resolved; `acknowledged`
        // arrives pre-triaged. That is a difference of DEGREE, not of kind — and keying the rule on ONE "the user
        // acted on this row" predicate means no FE or API rename can quietly move a row out of the fence.
        Assert.Equal(strict, NearDuplicateCollapser.RequiredPersistedThreshold(12, null, "done"));
        Assert.Equal(strict, NearDuplicateCollapser.RequiredPersistedThreshold(12, null, "acknowledged"));

        // ...and an UNKNOWN future status is treated as user-acted too. Fail-CLOSED: a new status must not be able to
        // opt itself out of a fence that guards against silently deleting the user's findings.
        Assert.Equal(strict, NearDuplicateCollapser.RequiredPersistedThreshold(12, null, "snoozed"));
        Assert.Equal(strict, NearDuplicateCollapser.RequiredPersistedThreshold(12, null, null));

        // AN OPEN ROW KEEPS 0.45, IN BOTH ANCHOR REGIMES. Claiming an open row is not a suppression: the row is
        // refreshed and the user still sees one card. Refusing would just delete it as vanished and insert the fresh
        // copy — one card either way. No asymmetry, so no recall is paid for it. (b4b's cross-bucket fold, which is
        // the ENTIRE reason the wildcard exists, lives here and is untouched.)
        Assert.Equal(ordinary, NearDuplicateCollapser.RequiredPersistedThreshold(12, null, "open"));
        Assert.Equal(ordinary, NearDuplicateCollapser.RequiredPersistedThreshold(null, 12, "OPEN")); // case-insensitive
        Assert.Equal(ordinary, NearDuplicateCollapser.RequiredPersistedThreshold(12, 12, "open"));

        // A USER-ACTED ROW WHOSE ANCHORS *AGREE* ALSO KEEPS 0.45 — the same real chapter, or BOTH book-wide. That is
        // b4's own bucket (one dimension, one chapter scope) and the anchor evidence AGREES rather than being absent,
        // so it keeps b4's measured margins. Raising it would start resurrecting dismissed findings for no gain.
        Assert.Equal(ordinary, NearDuplicateCollapser.RequiredPersistedThreshold(12, 12, "dismissed"));
        Assert.Equal(ordinary, NearDuplicateCollapser.RequiredPersistedThreshold(null, null, "dismissed"));
        Assert.Equal(ordinary, NearDuplicateCollapser.RequiredPersistedThreshold(0, 0, "acknowledged")); // ch0 is REAL
    }

    [Fact]
    public void FindPersistedNearDuplicate_ADistinctFinding_CannotClaimABookWideDISMISSEDRow_ButAnOpenOneYes()
    {
        // The same 0.462 pair, at the unit seam, in BOTH row states — so the difference is provably the STATUS and
        // nothing else. Book-wide row (ComparisonOrder null = the MayFold wildcard) vs an anchored fresh finding.
        var fresh = new BookFinding
        {
            Dimension = "continuity", Severity = 3, Rationale = Sev3Contradiction, Verdict = "improve", Status = "open",
            DedupKey = BookFinding.ComputeDedupKey("continuity", 12, Sev3Contradiction),
        };

        static NearDuplicateCollapser.PersistedCandidate Row(string status) =>
            NearDuplicateCollapser.Prepare(
                new BookFinding
                {
                    Id = Guid.NewGuid(), Dimension = "continuity", Severity = 1, Rationale = Sev1DanielPraise,
                    Verdict = "keep", Status = status,
                    DedupKey = BookFinding.ComputeDedupKey("continuity", null, Sev1DanielPraise),
                },
                comparisonOrder: null); // BOOK-WIDE → the wildcard

        var dismissed = Row("dismissed");
        Assert.Null(NearDuplicateCollapser.FindPersistedNearDuplicate(
            fresh, freshOrder: 12, new[] { dismissed }, new HashSet<Guid>(), out _, out _));

        // ...while an OPEN row with the IDENTICAL text and the IDENTICAL anchors IS still claimed at 0.45.
        var open = Row("open");
        var match = NearDuplicateCollapser.FindPersistedNearDuplicate(
            fresh, freshOrder: 12, new[] { open }, new HashSet<Guid>(), out var similarity, out var required);
        Assert.NotNull(match);
        Assert.Same(open.Row, match!.Value.Row);
        Assert.True(similarity > 0);
        Assert.Equal(NearDuplicateCollapser.DefaultThreshold, required); // open row -> the ordinary bar, not be-c07's 0.60
    }

    [Fact]
    public void FindPersistedNearDuplicate_AnUNLABELLEDRow_IsNotFiledUnderPlot_SoItCannotSUPPRESSAPlotFinding()
    {
        // final-r01 — THE TWO `NormalizeDimension`s DISAGREE, AND THE DISAGREEMENT IS THE SAFE ANSWER.
        //
        // BookReviewService.NormalizeDimension falls back to "plot" for an unknown/blank dimension. The collapser's
        // own helper (now named BucketDimension, precisely so nobody reads them as the same function) deliberately
        // does NOT: it case-folds and stops. be-c09 flagged the pair as drift and left the call to this review; the
        // call is to KEEP THEM DIFFERENT, and this test is the fence around that decision.
        //
        // WHY. This value gates a SUPPRESSING operation. A fresh finding that claims a persisted row is NOT INSERTED
        // — and when the row is DISMISSED, the finding never reaches the user at all (the P2-1 harm be-c07 raised the
        // bar to 0.60 for). "Single-sourcing" the fallback would file every unlabelled row INTO THE PLOT BUCKET,
        // where a real plot finding could then claim it. That is a WIDENING of what may be suppressed, dressed up as
        // a tidy-up. Refusing to guess is fail-OPEN: the unlabelled row sits alone, nothing matches it, and the fresh
        // copy is inserted as its own visible card. A duplicate, never a silence.
        //
        // This test goes RED the moment somebody makes the collapser adopt the "plot" fallback.
        var freshPlot = new BookFinding
        {
            Dimension = "plot", Severity = 3, Rationale = Sev3Contradiction, Verdict = "improve", Status = "open",
            DedupKey = BookFinding.ComputeDedupKey("plot", 12, Sev3Contradiction),
        };

        // A row a FOREIGN/legacy writer left with a blank dimension. Its prose is a NEAR-IDENTICAL re-wording of the
        // fresh finding, so similarity is emphatically NOT what keeps them apart — only the bucket key is.
        static NearDuplicateCollapser.PersistedCandidate Row(string dimension) =>
            NearDuplicateCollapser.Prepare(
                new BookFinding
                {
                    Id = Guid.NewGuid(), Dimension = dimension, Severity = 3, Rationale = Sev3Contradiction,
                    Verdict = "improve", Status = "dismissed",
                    DedupKey = BookFinding.ComputeDedupKey(dimension, 12, Sev3Contradiction),
                },
                comparisonOrder: 12); // anchors AGREE with the fresh finding → the ordinary 0.45 bar, not be-c07's 0.60

        // PREMISE: the two really are the same prose, so they clear the bar with room to spare. If the bucket keys
        // matched, this row WOULD be claimed — which is exactly what the control below proves.
        var blank = Row("");
        Assert.True(
            NearDuplicateCollapser.Similarity(
                NearDuplicateCollapser.ContentTokens(Sev3Contradiction),
                NearDuplicateCollapser.ContentTokens(Sev3Contradiction))
            >= NearDuplicateCollapser.DefaultThreshold,
            "premise: identical prose clears the ordinary threshold");

        // THE FENCE: the blank-dimension row is NOT claimed. The fresh plot finding will be inserted as its own open
        // card — visible. Under the "plot" fallback it would have claimed this DISMISSED row instead and vanished.
        Assert.Null(NearDuplicateCollapser.FindPersistedNearDuplicate(
            freshPlot, freshOrder: 12, new[] { blank }, new HashSet<Guid>(), out _, out _));

        // THE CONTROL — the identical row, correctly labelled "plot", IS claimed. So the test above cannot be passing
        // for some unrelated reason (a threshold, MayFold, MinContentTokens): the ONLY difference is the bucket key.
        var labelled = Row("plot");
        var match = NearDuplicateCollapser.FindPersistedNearDuplicate(
            freshPlot, freshOrder: 12, new[] { labelled }, new HashSet<Guid>(), out _, out _);
        Assert.NotNull(match);
        Assert.Same(labelled.Row, match!.Value.Row);
    }

    // ── 4. The survivor rule ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Collapse_SurvivorIsTheHighestSeverityVariant_NotTheFirstOccurrence()
    {
        // The gold set's fear-as-a-force finding was emitted at severity 1 FIRST and severity 3 second. The
        // exact-key dedup's rule is first-occurrence-wins; this pass deliberately does NOT inherit it, because
        // keeping the severity-1 phrasing would silently DOWNGRADE a major finding to a minor one.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("theme", 1, FearSev1, 0), // first in
            Candidate("theme", 3, FearSev3, 0),
        });

        Assert.Single(kept);
        Assert.Equal(FearSev3, kept[0].Rationale);
        Assert.Equal(3, kept[0].Severity);
    }

    [Fact]
    public void Collapse_OnEqualSeverity_SurvivorIsTheMostSpecificVariant()
    {
        // Both Morgan rows are severity 1; the longer one is a strict superset of the shorter, so it is kept even
        // when the shorter one is supplied first.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("character", 1, MorganShort, 0), // first in, and it is NOT the survivor
            Candidate("character", 1, MorganLong, 0),
        });

        Assert.Single(kept);
        Assert.Equal(MorganLong, kept[0].Rationale);
    }

    [Fact]
    public void Collapse_IsIndependentOfTheOrderTheModelEmittedTheVariantsIn()
    {
        // The windowed engine unions several passes, so input order carries no meaning. Reversing it must not
        // change WHICH finding the user is left with.
        var forward = Rationales(NearDuplicateCollapser.Collapse(GoldSet()));
        var reversed = Rationales(NearDuplicateCollapser.Collapse(Enumerable.Reverse(GoldSet()).ToList()));

        Assert.Equal(forward.OrderBy(r => r, StringComparer.Ordinal), reversed.OrderBy(r => r, StringComparer.Ordinal));
    }

    // ── 5. Degenerate / defensive ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Collapse_VeryShortRationales_AreNeverFuzzyMerged_EvenWhenTheyLookSimilar()
    {
        // "the pacing is too slow" vs "the pacing is too fast": 2 of 3 content tokens shared (Jaccard 0.5,
        // containment 0.67) — ANY useful threshold would merge two findings that say OPPOSITE things. The
        // MinContentTokens floor makes the pass inert exactly where the token metric stops being trustworthy.
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("pacing", 2, "הקצב איטי מדי.", 0),
            Candidate("pacing", 2, "הקצב מהיר מדי.", 0),
        });

        Assert.Equal(2, kept.Count);

        // The guard is one-sided in neither direction: a short rationale cannot be swallowed by a long one either.
        var mixed = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("tone", 2, ToneA, 0),
            Candidate("tone", 1, "המעבר חד.", 0),
        });
        Assert.Equal(2, mixed.Count);
    }

    [Fact]
    public void Collapse_EmptyOrNullInput_ReturnsEmpty_AndNeverThrows()
    {
        Assert.Empty(NearDuplicateCollapser.Collapse(Array.Empty<NearDuplicateCollapser.Candidate>()));
        Assert.Empty(NearDuplicateCollapser.Collapse(null!));
    }

    [Fact]
    public void ContentTokens_StemmingIsConfluent_SoTheSameWordUnderDifferentParticlesIsTheSameToken()
    {
        // The normalization that makes the metric work at all. "והמחיר" (and-the-price), "המחיר" (the-price) and
        // "מחיר" (price) are ONE content word; "הדמות" (the-character) and "דמותו" (his-character) share a stem.
        //
        // REGRESSION FENCE. This is not a cosmetic assertion: an earlier draft capped the proclitic strip at TWO,
        // which is NOT confluent — "והמחיר" (two particles) stemmed to "מחיר" while "המחיר" (one) stemmed to
        // "חיר", so the very rewording the pass exists to absorb produced two different tokens. Stemming must run
        // to a FIXED POINT; if someone re-caps that loop, this test is what fails.
        Assert.Equal(
            NearDuplicateCollapser.ContentTokens("והמחיר"),
            NearDuplicateCollapser.ContentTokens("המחיר"));
        Assert.Equal(
            NearDuplicateCollapser.ContentTokens("המחיר"),
            NearDuplicateCollapser.ContentTokens("מחיר"));

        var a = NearDuplicateCollapser.ContentTokens("והמחיר הדמות");
        var b = NearDuplicateCollapser.ContentTokens("המחיר דמותו");
        Assert.Equal(a.OrderBy(t => t, StringComparer.Ordinal), b.OrderBy(t => t, StringComparer.Ordinal));

        // Stopwords carry no signal and are dropped, so they cannot pad the overlap.
        Assert.Empty(NearDuplicateCollapser.ContentTokens("של את על עם"));
        Assert.Empty(NearDuplicateCollapser.ContentTokens("   "));
        Assert.Empty(NearDuplicateCollapser.ContentTokens(null));
    }

    [Fact]
    public void ContentTokens_StemmingIsConfluent_EvenPastFourStackedProclitics()
    {
        // P3-1 (extend past the two-particle case above). "וכשהמחיר" (and-when-the-price: ו + כש + ה + מחיר,
        // four proclitic characters stacked on the SAME root as the test above) must still land on the identical
        // stem as the bare root — a capped-at-two loop would have stopped one strip short and produced a
        // different token. Both this word and "מחיר" alone happen to reduce to "חיר" (the root itself begins
        // with a proclitic letter, מ — see Stem's remarks on symmetric over-stripping), which is exactly the
        // fixed-point behaviour under test: however many particles are glued on, the loop keeps going until none
        // of them are left to strip.
        Assert.Equal(
            NearDuplicateCollapser.ContentTokens("וכשהמחיר"),
            NearDuplicateCollapser.ContentTokens("מחיר"));
    }

    [Fact]
    public void ContentTokens_StemmingIsNotConfluentForA2LetterRoot_TheKnownLimitOfP31()
    {
        // P3-1. The 3-letter FLOOR that makes the two tests above true is exactly what breaks confluence for a
        // root shorter than 3 letters: the prefix-strip loop refuses to run once the token is already 3
        // characters ("t.Length >= 4" in Stem), so a bare 2-letter root ("בן", son) and the SAME root with one
        // proclitic attached ("הבן", the son — now 3 characters) land on DIFFERENT stems. This is a RECALL loss
        // only — it can never cause a false merge, because two genuinely different 2-letter roots still stem to
        // different tokens (the 4-way distinct check below). If this ever starts passing, the xmldoc's "true only
        // for a 3+-letter root" caveat needs re-checking, not deleting.
        Assert.NotEqual(
            NearDuplicateCollapser.ContentTokens("בן"),
            NearDuplicateCollapser.ContentTokens("הבן"));

        // ...and the 2-letter-root case still cannot make two DIFFERENT short roots collide.
        Assert.NotEqual(
            NearDuplicateCollapser.ContentTokens("בן"),  // son
            NearDuplicateCollapser.ContentTokens("גן"));  // garden
    }

    [Fact]
    public void ContentTokens_AnEmbeddedUnicodeFormatChar_IsStrippedNotSplit_MatchingChapterAnchorResolver()
    {
        // NIT-3. ChapterAnchorResolver.NormalizeTitle strips LRM/RLM/ZWJ/ZWNJ (Hebrew titles pick these up from
        // Word/Syncfusion round-trips) rather than treating them as a word boundary. Before this fix ContentTokens
        // disagreed: it fell into the "anything else ends the token" branch for the SAME character class, so a
        // rationale carrying a stray RLM in the middle of a word split it into two tokens instead of one. Unicode
        // escapes are used deliberately (rather than pasting an invisible character into the source) so the test
        // is unambiguous about exactly which characters it is asserting on.
        const char rlm = '‏'; // RIGHT-TO-LEFT MARK
        const char lrm = '‎'; // LEFT-TO-RIGHT MARK

        // A single embedded format char must vanish, exactly as it does for chapter-title matching.
        Assert.Equal(
            NearDuplicateCollapser.ContentTokens("מילה"),
            NearDuplicateCollapser.ContentTokens("מי" + rlm + "לה"));

        // And it must hold for a whole rationale, not just one isolated word — RLM and LRM both.
        Assert.Equal(
            NearDuplicateCollapser.ContentTokens("הקצב איטי מדי בפרק הזה"),
            NearDuplicateCollapser.ContentTokens("הקצב איטי מדי" + rlm + " בפרק" + lrm + " הזה"));
    }

    // ── 7. Observability (P1-6 / be-f01): the coverage line must fire when the count is ZERO ──────────
    //
    // THE LESSON THIS SECTION GUARDS. b4c shipped with 136 GREEN tests while its own fold fired ZERO times on real
    // data, and only a live rebuild caught it, because the coverage log was gated behind `if (collapsedTotal > 0)`
    // — a guard that reports only its positive count is indistinguishable from a guard that never ran. The tests
    // below assert the ZERO case specifically: a test that only checks the positive case would reproduce that exact
    // bug. (Premise-verified by revert: re-wrapping the log in `if (collapsedTotal > 0)` fails every test here.)

    [Fact]
    public void Collapse_NothingCollapses_StillLogsOneInformationLine_WithZeroCounts()
    {
        var log = new CapturingLogger();

        // Two genuinely distinct findings, different dimensions AND different chapters — nothing here can collapse
        // under any pass (b4 within-bucket, b4b cross-bucket, b4c cross-dimension all require some similarity/fold
        // condition that is absent by construction).
        var kept = NearDuplicateCollapser.Collapse(new[]
        {
            Candidate("plot", 2, PlotA, 0),
            Candidate("continuity", 1, Continuity, 1),
        }, log);

        Assert.Equal(2, kept.Count); // premise: nothing actually collapsed this build

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("near-duplicate collapse", entry.Message, StringComparison.Ordinal);
        Assert.Contains("2 candidate(s) in", entry.Message, StringComparison.Ordinal);
        Assert.Contains("2 survivor(s) out", entry.Message, StringComparison.Ordinal);
        Assert.Contains("collapsed 0", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Collapse_EmptyCandidateSet_NeverReachesTheLogger()
    {
        // The degenerate empty-input short-circuit returns before the try block, so there is nothing to log — this
        // is NOT a violation of the zero-coverage rule (there was no build to report on), unlike the case above
        // where real candidates went in and zero collapsed.
        var log = new CapturingLogger();
        Assert.Empty(NearDuplicateCollapser.Collapse(Array.Empty<NearDuplicateCollapser.Candidate>(), log));
        Assert.Empty(log.Entries);
    }

    // ── 8. be-c08 — THE TWO DEFECTS IN THE PASSES' OWN MECHANICS (P3-5, P3-9) ─────────────────────────
    //
    // Both are about the ONE field these passes write: the survivor's Severity. It was written to the ENTITY, as the
    // passes went — which made the fail-safe's "un-collapsed set" a half-mutated set (P3-5), and made an EARLIER fold
    // able to change which target a LATER one picked, under a comment asserting the opposite (P3-9). Both are now
    // STAGED: the passes work on an array, the tie-break reads the FROZEN arrival values, and the entities are written
    // once, last, after everything that can fault.

    [Fact]
    public void Collapse_AFaultAfterTheFolds_LeavesEVERYSeverityExactlyAsItArrived()
    {
        // P3-5. "Returning the un-collapsed set restores the pre-2026-07-12 behavior for this build" — it returned the
        // un-collapsed LIST, but b4b and b4c had already LIFTED the severities of the survivors they folded into. So a
        // fault shipped a set that was neither collapsed nor original: the duplicates were all still there AND some of
        // them were now wearing a severity the model never gave them. Nothing lost, and nobody could have said what
        // the state WAS — which is exactly the class of comment this subsystem keeps having to un-ship.
        //
        // The fault is injected at the LOGGER, deliberately: it is the last thing the method touches, so it proves the
        // strongest form of the property — every statement in Collapse, right down to the coverage line, can fault
        // without a single finding having been written to.
        var candidates = new[]
        {
            Candidate("theme", 1, TanariA, 3),  // the anchored copy: b4b's target
            Candidate("theme", 3, TanariB, null), // the book-wide copy at a HIGHER severity: the lift the fold makes
        };
        Assert.True(
            NearDuplicateCollapser.Similarity(
                NearDuplicateCollapser.ContentTokens(TanariA), NearDuplicateCollapser.ContentTokens(TanariB))
            >= NearDuplicateCollapser.DefaultThreshold,
            "premise: these two really do fold (otherwise there is no severity lift to leave behind)");

        var kept = NearDuplicateCollapser.Collapse(candidates, new ThrowingLogger());

        // FAIL-SAFE: both findings come back, un-collapsed.
        Assert.Equal(2, kept.Count);

        // ...and UNMUTATED. Pre-fix the anchored copy came back at severity 3 — lifted by a fold that the caller was
        // simultaneously told had not happened.
        Assert.Equal(1, candidates[0].Finding.Severity);
        Assert.Equal(3, candidates[1].Finding.Severity);
    }

    // ── P3-9: THE TIE-BREAK MUST NOT READ A VALUE THE PASS ITSELF IS MUTATING ─────────────────────────
    //
    // These four rationales are SYNTHETIC, and deliberately so — the only synthetic ones in this file. Everything else
    // here is tuned against real captured rows because the NUMBERS are what is on trial. Here the number is not: what
    // is on trial is the tie-break's INPUTS, and the case needs an EXACT similarity tie between two anchored targets
    // plus a prior fold onto one of them — a coincidence the real corpora do not contain and that it would be
    // dishonest to pretend they do. The token sets are therefore built by construction (each word is one token; none
    // is a stopword; Hebrew stemming leaves ASCII alone), and every premise below is asserted through the SHIPPED
    // Similarity/ContentTokens rather than assumed.
    private const string TieAnchoredSix = "narrator pacing sequence dialogue banter wit";           // A1, chapter 1
    private const string TieAnchoredSeven = "rhythm cadence momentum imagery symbol colour texture"; // A2, chapter 2
    private const string TieBookWideTies = "narrator pacing sequence rhythm cadence momentum";       // N: ties A1 vs A2
    private const string TieBookWideLifts = "dialogue banter wit footnote margin";                   // M: folds into A1

    [Fact]
    public void Collapse_AnEarlierFold_DoesNotChangeWhichTargetALaterEquallySimilarCopyFoldsInto()
    {
        // THE FALSE INVARIANT: "Anchored candidates are NEVER absorbed by this pass, so the set of possible targets
        // does not shrink as we go and the outcome is independent of the order the no-anchor copies are visited in."
        // True of the target SET — but the fold also LIFTS its target's severity, and IsBetterTarget READ that severity
        // as its tie-break. So an earlier fold could change which target a later, equally-similar copy chose. (It was
        // deterministic, since the copies are visited in DedupKey order — so it could never flap between builds. It was
        // the stated REASON that was false, and a false reason is what stops the next reader from checking.)
        //
        // THE SETUP, all four premises asserted below:
        //   A1 (ch1, sev 1, 6 tokens)  ← M folds into it BY SIMILARITY (0.600 vs 0.000) and LIFTS it to severity 3
        //   A2 (ch2, sev 1, 7 tokens)  ← the tie-break's rightful winner: same base severity, MORE tokens
        //   N  (book-wide, sev 2)      ← EXACTLY as similar to A1 as to A2 (0.500 each) → the TIE-BREAK decides
        //   M  (book-wide, sev 3)      ← visited FIRST (its DedupKey sorts lower), and unrelated to N (0.000)
        // Pre-fix, N saw A1 already lifted to 3 and folded into IT. Post-fix the tie-break reads the ARRIVAL severities
        // (1 and 1), falls through to the token count, and N folds into A2 — which is where it goes with M absent, and
        // that is the whole point: an unrelated fold elsewhere in the dimension no longer moves this decision.
        var a1 = Candidate("tone", 1, TieAnchoredSix, 1);
        var a2 = Candidate("tone", 1, TieAnchoredSeven, 2);
        var n = Candidate("tone", 2, TieBookWideTies, null);
        var m = Candidate("tone", 3, TieBookWideLifts, null);

        double Sim(string x, string y) => NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(x), NearDuplicateCollapser.ContentTokens(y));

        // PREMISES, through the shipped metric.
        Assert.Equal(Sim(TieBookWideTies, TieAnchoredSix), Sim(TieBookWideTies, TieAnchoredSeven), 10); // N: a real TIE
        Assert.Equal(0.500, Sim(TieBookWideTies, TieAnchoredSix), 3);                                   // ...above 0.45
        Assert.Equal(0.600, Sim(TieBookWideLifts, TieAnchoredSix), 3);                                  // M prefers A1
        Assert.Equal(0.000, Sim(TieBookWideLifts, TieAnchoredSeven), 3);                                // M cannot see A2
        Assert.Equal(0.000, Sim(TieBookWideLifts, TieBookWideTies), 3);   // ...and b4 does not collapse M and N first
        Assert.True(
            string.CompareOrdinal(m.Finding.DedupKey, n.Finding.DedupKey) < 0,
            "premise: M is visited BEFORE N (DedupKey order), so its fold is the one that could poison N's tie-break");
        Assert.True(
            NearDuplicateCollapser.ContentTokens(TieAnchoredSeven).Count
            > NearDuplicateCollapser.ContentTokens(TieAnchoredSix).Count,
            "premise: at EQUAL base severity the tie-break prefers A2 (more content tokens)");

        // CONTROL — with M absent, N folds into A2 (the tie-break's answer on the arrival severities).
        var withoutM = new[] { Candidate("tone", 1, TieAnchoredSix, 1), Candidate("tone", 1, TieAnchoredSeven, 2), Candidate("tone", 2, TieBookWideTies, null) };
        Assert.Equal(2, NearDuplicateCollapser.Collapse(withoutM).Count);
        Assert.Equal(1, withoutM[0].Finding.Severity);          // A1 untouched
        Assert.Equal(2, withoutM[1].Finding.Severity);          // A2 took N's severity → N folded HERE

        // THE TEST — add M, which folds into A1 and lifts it to 3. N's choice must NOT move.
        var candidates = new[] { a1, a2, n, m };
        var kept = NearDuplicateCollapser.Collapse(candidates);

        Assert.Equal(2, kept.Count);                 // both book-wide copies folded away; both anchored copies remain
        Assert.Equal(3, a1.Finding.Severity);        // A1 lifted by M, as before
        Assert.Equal(2, a2.Finding.Severity);        // ★ N STILL folded into A2. Pre-fix this was 1: N followed the
                                                     //   severity M had just written and folded into A1 instead.
    }

    /// <summary>An ILogger that FAULTS. The only injectable fault surface in <c>Collapse</c> (every other statement in
    /// it is null-safe by construction), and the strictest one: it fires at the very END, so a test that survives it
    /// proves the entities were untouched right up to the last line of the method.</summary>
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScopeShim.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Warning; // let the fail-safe's own log through
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                throw new InvalidOperationException("logger faulted");
        }

        private sealed class NullScopeShim : IDisposable
        {
            public static readonly NullScopeShim Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>Minimal capturing ILogger for asserting a log line fired (and at what level), without pulling in
    /// BookReviewServiceTests' private CapturingLoggerProvider.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
