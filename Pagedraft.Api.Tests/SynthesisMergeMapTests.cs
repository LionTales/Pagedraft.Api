using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// b8 — THE SYNTHESIS MERGE MAP (<see cref="SynthesisMergeMap"/>): the reduce pass's DELETE channel.
///
/// EVERY Hebrew string below is a REAL rationale, lifted verbatim from the 18 BookFinding rows of book
/// A63A6E02 captured on 2026-07-13 (scratchpad/a63-findings.json). The W-numbers in the comments are the
/// build-local ids the digest would print for them, in accumulation order. Nothing here is invented.
///
/// WHY THIS PASS EXISTS AT ALL, IN ONE LINE: on this corpus the token metric is EXHAUSTED —
/// <see cref="Similarity_TheDuplicateAndDistinctClasses_AreInterleaved_SoNoThresholdCanSeparateThem"/> proves
/// a genuinely DISTINCT pair (0.462) outscores a genuine DUPLICATE pair (0.455), so no cut-off on
/// max(Jaccard, containment) can tell them apart. Only something that reads MEANING can, and the synthesis model
/// is the only thing in the pipeline that has read them all. This gives it somewhere to put the answer.
/// </summary>
public class SynthesisMergeMapTests
{
    // ── The gold set: the 18 REAL rows of book A63A6E02 (verbatim) ────────────────────────────────────

    // [W1] character sev1 ch15
    private const string CharMarara = "הדמות של מררה מוסיפה רובד רגשי חשוב; הדאגה שלה שקטה אך עוצמתית, מה שמדגיש את הבדידות של דניאל.";
    // [W2] character sev1 ch0
    private const string CharTamar = "תמר עוברת תהליך פנימי ברור ומוחשי של חיבור לאבן ולזיכרון אמה.";

    // [W3] continuity sev3 ch[12,13,14,15] — THE FACTUAL CONTRADICTION. The most valuable finding in the book,
    // and the one this whole design exists to protect: it scores 0.462 against W5, ABOVE a real duplicate pair.
    private const string ContraDaniel = "קיימת סתירה עובדתית בין מצב הדמויות בפרקי המעקב (12-15) לבין פרקי העלילה המרכזיים; דמות 'דניאל' אינה מוזכרת בשום מקום אחר בספר, ואין קשר ברור בינה לבין תמר או אדם.";
    // [W4] continuity sev2 ch10
    private const string ContYahya = "מופיעה דמות בשם 'יחיא' בפרק 10 בלבד, ללא כל הכרזה או רקע קודם.";

    // [W5][W6][W7] continuity sev1 — THE DANIEL TRIPLE. ONE observation (Daniel's sleeplessness/exhaustion holds
    // consistently) emitted three times across primaries 14 / 13 / 13. No existing mechanism can close it:
    // W5×W6 = 0.455 but they sit on DIFFERENT REAL CHAPTERS, which b4b's MayFold fence refuses to merge (rightly:
    // that fence is what saves W3); W6×W7 share a bucket but score 0.444, UNDER b4's 0.45 threshold.
    private const string DanielSleep14_15 = "המשך מצב חוסר השינה של דניאל מפרק 14 ל-15 יוצר רצף פיזי ורגשי ברור.";
    private const string DanielSleep13_15 = "המצוקה הפיזית של דניאל (חוסר שינה ועייפות) נשמרת בעקביות ויוצרת רצף ריאליסטי.";
    private const string DanielExhaust13 = "המצב הפיזי של דניאל (עייפות קיצונית) נשמר בעקביות לאורך הסצנות.";

    // [W8] plot sev1 ch3 / [W9] plot sev1 no-anchor
    private const string PlotMap = "שימוש במפה הפיזית ובמרחב הכפול יוצר נקודת שיא ויזואלית וסיפורית משמעותית.";
    private const string PlotBreak = "השבר אינו אויב אלא כוח טבעי הדורש ריקוד ומחוות, מה שמשלם על ההבטחה של הסוף.";

    // [W10][W11][W12] theme sev1 ch9 — the personal-to-collective memory finding, emitted three times. W10×W11 =
    // 0.600 (b4 already collapses those two); W11×W12 = 0.375, under the threshold, so W12 survives as a duplicate.
    private const string ThemeMemAck = "המעבר מזיכרון אישי לזיכרון קולקטיבי (יומן של כולם) מעניק לסוף הספר משמעות רחבה.";
    private const string ThemeMemB = "המעבר מהאני האישי לזיכרון קולקטיבי של הקהילה מעניק לספר סיומת של התאוששות ושלום.";
    private const string ThemeMemC = "המעבר מהאני לקהילתי בפרק העשירי מחזק את תחושת השלום והתאוששות.";

    // [W13] theme sev1 ch15 / [W14] theme sev1 ch4
    private const string ThemeWorry = "השימוש בדאגה שאינה מבוטאת ככלי לתיאור המרחק בין דניאל לסביבתו מחזק את נושאי הזיכרון והד.";
    private const string ThemeMatter = "הקשר בין החומר הפיזי (אבן וחדל) לבין הרוח והצליל מובנה היט.";

    // [W15] tone sev2 ch16
    private const string ToneKtiv = "המעבר לטון של ביטחון וריכוז בפרק 16 הוא חד מאוד לעומת המצוקה הפיזית והבידוד של הפרקים הקודמים.";

    // [W16][W18] tone sev1 — THE SCIENCE-AND-POETICS PAIR: the identical clause, filed on ch1 AND on ch15 (0.875).
    // The highest-scoring true duplicate in the book, and MayFold refuses it for the same reason it refuses the
    // Daniel pair: two DIFFERENT REAL chapters. It is exactly the class b7 warned it could RESHAPE duplicates into.
    private const string ToneScience1 = "השילוב בין המדע לבין הפואטיקה יוצר אווירה ייחודית של קדושה ומתח.";
    private const string ToneScience15 = "השילוב בין המדע לבין הפואטיקה יוצר אווירה של קדושה ומתח שמתכתבת עם נושאי הריקנות והעצב.";

    // [W17] tone sev1 ch15 — GENUINELY DISTINCT from W18 (0.364) though they share a chapter AND a dimension AND
    // half their vocabulary. The second fence.
    private const string ToneCold15 = "הטון המבודד והקר בפרק 15 מתכתב היטב עם נושאי הריקנות והעצב המופיעים בתיאור הספר.";

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

    private sealed record Row(string Id, string Dimension, int Severity, int[] Anchors, string Rationale);

    /// <summary>The 18 real rows, in the accumulation order the digest numbered them W1..W18.</summary>
    private static Row[] GoldRows() => new[]
    {
        new Row("W1", "character", 1, new[] { 15 }, CharMarara),
        new Row("W2", "character", 1, new[] { 0 }, CharTamar),
        new Row("W3", "continuity", 3, new[] { 12, 13, 14, 15 }, ContraDaniel),
        new Row("W4", "continuity", 2, new[] { 10 }, ContYahya),
        new Row("W5", "continuity", 1, new[] { 14, 15 }, DanielSleep14_15),
        new Row("W6", "continuity", 1, new[] { 13, 14, 15 }, DanielSleep13_15),
        new Row("W7", "continuity", 1, new[] { 13 }, DanielExhaust13),
        new Row("W8", "plot", 1, new[] { 3 }, PlotMap),
        new Row("W9", "plot", 1, Array.Empty<int>(), PlotBreak),
        new Row("W10", "theme", 1, new[] { 9 }, ThemeMemAck),
        new Row("W11", "theme", 1, new[] { 9 }, ThemeMemB),
        new Row("W12", "theme", 1, new[] { 9 }, ThemeMemC),
        new Row("W13", "theme", 1, new[] { 15 }, ThemeWorry),
        new Row("W14", "theme", 1, new[] { 4 }, ThemeMatter),
        new Row("W15", "tone", 2, new[] { 16 }, ToneKtiv),
        new Row("W16", "tone", 1, new[] { 1 }, ToneScience1),
        new Row("W17", "tone", 1, new[] { 15 }, ToneCold15),
        new Row("W18", "tone", 1, new[] { 15 }, ToneScience15),
    };

    private static readonly JsonSerializerOptions AnchorOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A stable fake chapter id per order — the merge unions anchors, and an anchor without a chapterId
    /// is not navigable, so the union must carry the id across too.</summary>
    private static Guid ChapterIdFor(int order) => new($"0000000{order % 10}-0000-0000-0000-{order:D12}");

    /// <summary>
    /// Builds a candidate exactly as the build pipeline does: the entity carries the RESOLVED anchors as JSON and
    /// the REAL dedup key derived from (dimension, first-anchor order, rationale) — so every assertion about the
    /// key not moving is made against the production derivation, not a stand-in.
    /// </summary>
    private static (NearDuplicateCollapser.Candidate Candidate, BookFindingItem Item) Build(Row row)
    {
        int? primaryOrder = row.Anchors.Length > 0 ? row.Anchors[0] : null;

        var anchors = row.Anchors
            .Select(o => new FindingChapterAnchor { ChapterId = ChapterIdFor(o), Order = o, Title = $"פרק {o}" })
            .ToList();

        var finding = new BookFinding
        {
            Dimension = row.Dimension,
            Severity = row.Severity,
            Rationale = row.Rationale,
            Verdict = "keep",
            Status = "open",
            ChapterAnchorsJson = JsonSerializer.Serialize(anchors, AnchorOpts),
            EvidenceJson = "[]",
            DedupKey = BookFinding.ComputeDedupKey(row.Dimension, primaryOrder, row.Rationale),
        };

        var item = new BookFindingItem
        {
            Dimension = row.Dimension,
            Severity = row.Severity,
            Rationale = row.Rationale,
            ChapterAnchors = anchors,
        };

        return (new NearDuplicateCollapser.Candidate(finding, primaryOrder), item);
    }

    private sealed class Harness
    {
        public List<NearDuplicateCollapser.Candidate> Candidates { get; } = new();
        public List<BookFindingItem> Items { get; } = new();
        public Dictionary<string, BookFindingItem> IdMap { get; } = new(StringComparer.Ordinal);

        public static Harness FromGold()
        {
            var h = new Harness();
            foreach (var row in GoldRows())
            {
                var (candidate, item) = Build(row);
                h.Candidates.Add(candidate);
                h.Items.Add(item);
                h.IdMap[row.Id] = item;
            }
            return h;
        }

        /// <summary>Runs the FULL production pipeline over the gold set: PASS 0 (the merge map) then the shipped
        /// collapser (b4 / b4b / b4c). This is what UnionAndDedup does, in the same order, with the same objects.</summary>
        public List<BookFinding> Run(SynthesisMergeMap.Resolution? resolution, ILogger? logger = null)
        {
            var merged = SynthesisMergeMap.Apply(Candidates, Items, resolution, logger);
            return NearDuplicateCollapser.Collapse(merged, logger);
        }
    }

    private static SynthesisMergeItem Group(string keep, params string[] ids) =>
        new() { Ids = ids.ToList(), Keep = keep };

    private static SynthesisMergeMap.Resolution Resolve(Harness h, bool enabled, params SynthesisMergeItem[] groups) =>
        SynthesisMergeMap.Resolve(enabled, groups.ToList(), h.IdMap, NullLogger.Instance);

    private static IReadOnlyList<int> AnchorOrders(BookFinding f) =>
        JsonSerializer.Deserialize<List<FindingChapterAnchor>>(f.ChapterAnchorsJson, AnchorOpts)!
            .Select(a => a.Order).ToList();

    private static BookFinding Find(IEnumerable<BookFinding> kept, string rationale) =>
        kept.Single(f => f.Rationale == rationale);

    private static double Sim(string a, string b) => NearDuplicateCollapser.Similarity(
        NearDuplicateCollapser.ContentTokens(a), NearDuplicateCollapser.ContentTokens(b));

    // ── 1. WHY a model signal — the token metric is provably exhausted here ───────────────────────────

    [Fact]
    public void Similarity_TheDuplicateAndDistinctClasses_AreInterleaved_SoNoThresholdCanSeparateThem()
    {
        // THE MEASUREMENT THAT KILLS THE THRESHOLD APPROACH, scored with the SHIPPED metric (never a
        // reimplementation). Sorted, the live pairs of book A63A6E02 read:
        //
        //     0.875 DUP  >  0.462 DISTINCT  >  0.455 DUP  >  0.444 DUP  >  0.375 DUP  >  0.364 DISTINCT
        //
        // The two classes INTERLEAVE. Any threshold low enough to catch the 0.455 duplicate ALSO catches the 0.462
        // distinct pair — and that distinct pair is a SEVERITY-3 FACTUAL CONTRADICTION beside a SEVERITY-1 piece of
        // praise, i.e. two findings of OPPOSITE polarity that a bag-of-words simply cannot tell apart. There is no
        // number to tune. That is the entire justification for handing the decision to the model.
        var dup875 = Sim(ToneScience1, ToneScience15);      // one clause, two chapters
        var distinct462 = Sim(ContraDaniel, DanielSleep14_15); // sev3 contradiction vs sev1 praise
        var dup455 = Sim(DanielSleep14_15, DanielSleep13_15);  // the Daniel duplicate
        var dup444 = Sim(DanielSleep13_15, DanielExhaust13);   // the Daniel duplicate again
        var dup375 = Sim(ThemeMemB, ThemeMemC);                // the memory duplicate
        var distinct364 = Sim(ToneCold15, ToneScience15);      // same chapter, same dimension, DIFFERENT findings

        Assert.Equal(0.875, dup875, 3);
        Assert.Equal(0.462, distinct462, 3);
        Assert.Equal(0.455, dup455, 3);
        Assert.Equal(0.444, dup444, 3);
        Assert.Equal(0.375, dup375, 3);
        Assert.Equal(0.364, distinct364, 3);

        // THE INTERLEAVING, asserted as the property and not just as six numbers: a DISTINCT pair outscores a
        // DUPLICATE pair. If this ever stops being true the metric has improved and a threshold might work again —
        // and this test is where that news arrives.
        Assert.True(distinct462 > dup455,
            $"A genuinely distinct pair ({distinct462:0.000}) must still outscore a true duplicate ({dup455:0.000}) " +
            "for the merge map's premise to hold.");

        // And therefore: EVERY threshold that catches the duplicate destroys the contradiction.
        foreach (var threshold in new[] { 0.45, 0.44, 0.40, 0.375, 0.30 })
        {
            var catchesDuplicate = dup455 >= threshold;
            var destroysContradiction = distinct462 >= threshold;
            Assert.False(catchesDuplicate && !destroysContradiction,
                $"threshold {threshold:0.000} would have to separate them, and it cannot.");
        }
    }

    // ── 2. The map does what no threshold could ──────────────────────────────────────────────────────

    [Fact]
    public void MergeMap_TheSameChapterDanielPair_MergesAndUnionsItsAnchors_WhileTheSev3ContradictionSurvives()
    {
        // THE ACCEPTANCE CASE, as be-c07's anchor fence leaves it. The model names the two Daniel copies that share
        // a chapter scope (W6 and W7 both anchor primary 13). NO deterministic pass can close them — they score
        // 0.444, UNDER b4's 0.45 threshold, and no threshold can be lowered to catch them because 0.44 would also
        // catch the 0.462 DISTINCT contradiction pair (see the interleaving test). This is exactly the class the
        // merge map exists for, and the fence leaves it fully intact: a model reading MEANING closes it.
        var h = Harness.FromGold();
        var kept = h.Run(Resolve(h, enabled: true, Group("W7", "W6", "W7")));

        // The survivor the model chose, VERBATIM (not merged prose).
        Assert.Single(kept, f => f.Rationale == DanielExhaust13);
        Assert.DoesNotContain(kept, f => f.Rationale == DanielSleep13_15);

        // THE ANCHOR UNION, and THE INVARIANT THAT MATTERS MOST: W7 anchored only chapter 13; it absorbs W6's
        // [13,14,15] and now carries all three — but its OWN first anchor (13) is STILL at index 0. anchors[0].Order
        // is a DEDUP-KEY input; if the union had reordered it, the key would move, b3's legacy-key fallback would
        // stop matching, and every acknowledged/dismissed row would be re-orphaned.
        var survivor = Find(kept, DanielExhaust13);
        Assert.Equal(new[] { 13, 14, 15 }, AnchorOrders(survivor));
        Assert.Equal(13, AnchorOrders(survivor)[0]);
        Assert.Equal(BookFinding.ComputeDedupKey("continuity", 13, DanielExhaust13), survivor.DedupKey);

        // The unioned anchors are NAVIGABLE: they carry the absorbed copy's chapterIds, not empty Guids.
        var anchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(survivor.ChapterAnchorsJson, AnchorOpts)!;
        Assert.All(anchors, a => Assert.NotEqual(Guid.Empty, a.ChapterId));
        Assert.Equal(ChapterIdFor(14), anchors.Single(a => a.Order == 14).ChapterId);

        // THE FENCE, ASSERTED HARD. The severity-3 factual contradiction was never named, so it is NEVER TOUCHED —
        // whole, at severity 3, with all four of its own anchors — even though it scores 0.462 against a finding in
        // this very group, i.e. HIGHER than the 0.444 pair the merge just closed.
        var contradiction = Find(kept, ContraDaniel);
        Assert.Equal(3, contradiction.Severity);
        Assert.Equal("continuity", contradiction.Dimension);
        Assert.Equal(new[] { 12, 13, 14, 15 }, AnchorOrders(contradiction));

        // ...and the THIRD Daniel copy — anchored to a DIFFERENT primary chapter (14) — is NOT swept in with them.
        // A group spanning 14 and 13 is refused (the next test); this one names only the chapter-13 pair.
        Assert.Single(kept, f => f.Rationale == DanielSleep14_15);
    }

    // ═══ be-c07 (P2-4) — THE ANCHOR-COMPATIBILITY FENCE ══════════════════════════════════════════════
    //
    // Every other rule in Resolve validates the model against ITSELF (are the ids real, is the survivor in the group,
    // is a finding claimed twice). NOT ONE of them asked whether the findings it wants to merge are even ABOUT THE
    // SAME CHAPTER — so the model could name two findings on two different real chapters and one was DELETED. That is
    // not hypothetical: in the b8 LIVE GATE gemma4:12b kept a TONE finding on chapter 14 and deleted a CHARACTER
    // finding on chapter 12 (both merely mention דניאל). They were the book's ONLY TWO character findings, so the
    // whole דמויות dimension vanished from the score panel.
    //
    // THE FENCE IS MayFold — the same predicate, the same single source, applied to every PAIR in the group. Its
    // COST is real and is pinned below: b8 can no longer merge the 0.875 science/poetics clause across ch1 and ch15,
    // nor fold the ch14 Daniel copy into the ch13 one. Those become visible duplicate cards — which is the KNOWN,
    // ACCEPTED residual of this feature (its agreed fix is display-side grouping in the client, not a delete here).
    // ═════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MergeMap_AGroupSpanningTwoDifferentRealChapters_IsREJECTED_EvenAtTheHighestSimilarityInTheBook()
    {
        // THE 0.875 PAIR — the HIGHEST-scoring true duplicate in the whole corpus: ONE clause about science and
        // poetics, filed on chapter 1 AND on chapter 15. b8 used to merge it on the model's say-so. It no longer can:
        // the group spans two different real chapters, which is precisely the shape of the MEASURED false merge.
        //
        // The fence cannot tell this pair from that catastrophe, and it must not try: to a merge map, "two findings
        // on two different chapters" is one shape, and the ONE time we let the model judge it on a real book it
        // destroyed a dimension. So a real duplicate stays as a visible duplicate — the CHEAP failure, chosen on
        // purpose over the expensive one.
        Assert.Equal(0.875, Sim(ToneScience1, ToneScience15), 3); // premise: text alone says "merge them"

        var h = Harness.FromGold();
        var resolution = Resolve(h, enabled: true, Group("W18", "W16", "W18"));
        Assert.Single(resolution.Groups); // it passes every id/keep/claim rule in Resolve...

        var logger = new ListLogger();
        var kept = SynthesisMergeMap.Apply(h.Candidates, h.Items, resolution, logger);

        // ...and is then REFUSED by the anchor fence at apply time. BOTH copies survive.
        Assert.Equal(h.Candidates.Count, kept.Count);
        Assert.Contains(kept, c => c.Finding!.Rationale == ToneScience1);
        Assert.Contains(kept, c => c.Finding!.Rationale == ToneScience15);

        // The survivor was not half-mutated on the way out either: no anchor was unioned onto it.
        Assert.Equal(new[] { 15 }, AnchorOrders(kept.Single(c => c.Finding!.Rationale == ToneScience15).Finding!));

        // And the refusal is COUNTED and NAMED — a fence that fires silently is a fence nobody can audit.
        var coverage = Assert.Single(logger.Messages, m => m.Contains("synthesis proposed"));
        Assert.Contains("applied 0", coverage);
        Assert.Contains("rejected 1 (anchors-span-different-chapters=1)", coverage);
    }

    [Fact]
    public void MergeMap_TheFenceProtectsTheSev3Contradiction_EvenWhenItSHARESAnchorsWithTheCopyItWouldAbsorb()
    {
        // ★ THE PAIR THAT DICTATES THE FENCE'S EXACT SHAPE, and the reason it compares the PRIMARY order rather than
        // the anchor SETS. The obvious reading of "reject unless the members share an anchor" is a set INTERSECTION —
        // and on THIS corpus it is UNSAFE:
        //
        //     W3 (the SEVERITY-3 FACTUAL CONTRADICTION) is anchored [12,13,14,15]
        //     W5 (a SEVERITY-1 piece of praise)         is anchored    [14,15]
        //
        // THEY SHARE TWO ANCHORS. An intersection fence would let the model merge them and DELETE the single most
        // valuable finding in the book — the very finding b4b's MayFold is measured to be protecting. Comparing the
        // PRIMARY (first resolved) order — 12 vs 14 — refuses. This test is what stops a future "improvement" from
        // widening the fence into an overlap test.
        var h = Harness.FromGold();
        var contradictionAnchors = AnchorOrders(h.Candidates[2].Finding!); // W3
        var praiseAnchors = AnchorOrders(h.Candidates[4].Finding!);        // W5
        Assert.Equal(new[] { 12, 13, 14, 15 }, contradictionAnchors);
        Assert.Equal(new[] { 14, 15 }, praiseAnchors);
        Assert.NotEmpty(contradictionAnchors.Intersect(praiseAnchors));    // PREMISE: they DO share anchors

        var resolution = Resolve(h, enabled: true, Group("W5", "W3", "W5")); // the model says "keep the praise"
        Assert.Single(resolution.Groups);

        var kept = SynthesisMergeMap.Apply(h.Candidates, h.Items, resolution, NullLogger.Instance);

        // REFUSED. The contradiction is still here, whole, at severity 3, with its own four anchors.
        var contradiction = kept.Single(c => c.Finding!.Rationale == ContraDaniel).Finding!;
        Assert.Equal(3, contradiction.Severity);
        Assert.Equal(new[] { 12, 13, 14, 15 }, AnchorOrders(contradiction));
        Assert.Contains(kept, c => c.Finding!.Rationale == DanielSleep14_15); // ...and so is the praise
    }

    [Fact]
    public void MergeMap_ABookWideMemberMayJoinAGroup_ButCanNeverBRIDGETwoDifferentRealChapters()
    {
        // THE NO-ANCHOR CALL, BOTH HALVES. A book-wide finding NAMES NO CHAPTER, so it cannot "span two different
        // real chapters" with anything: MayFold treats a null order as a wildcard, and so does this fence. That is
        // deliberate — at the b5 gate ALL SEVEN surviving duplicate pairs on this book were a book-wide copy beside
        // an anchored one, so banning them would reject the group shape that is most often CORRECT.
        //
        // (1) A wildcard member merges fine with an anchored one. W9 (plot, NO anchor) + W8 (plot, chapter 3).
        var h = Harness.FromGold();
        var kept = h.Run(Resolve(h, enabled: true, Group("W8", "W8", "W9")));
        Assert.Single(kept, f => f.Rationale == PlotMap);
        Assert.DoesNotContain(kept, f => f.Rationale == PlotBreak);

        // (2) THE TRANSITIVITY TRAP — the reason the fence is PAIRWISE and not "every member shares with the
        // survivor". A book-wide SURVIVOR is compatible with ch1 AND with ch15, so a survivor-only check would let it
        // BRIDGE the science/poetics pair and merge the two anchored copies after all. Checking every pair closes it:
        // the bridge is fine, but W16 (ch1) and W18 (ch15) still have to face EACH OTHER.
        var h2 = Harness.FromGold();
        var bridged = h2.Run(Resolve(h2, enabled: true, Group("W9", "W9", "W16", "W18")));

        Assert.Contains(bridged, f => f.Rationale == ToneScience1);  // BOTH anchored copies survive...
        Assert.Contains(bridged, f => f.Rationale == ToneScience15);
        Assert.Contains(bridged, f => f.Rationale == PlotBreak);     // ...and so does the would-be bridge
    }

    [Fact]
    public void MergeMap_TheWholeGoldSet_EighteenRealRows_CollapseToFifteen_WithEveryFenceIntact()
    {
        // END TO END over all 18 real rows, with the merge groups the anchor fence ALLOWS.
        var h = Harness.FromGold();

        // WITHOUT the map (today): only b4's within-bucket pass fires, on the one theme pair that clears 0.45
        // (W10×W11 = 0.600). Everything else survives as a visible duplicate. 18 → 17.
        var withoutMap = h.Run(resolution: null);
        Assert.Equal(17, withoutMap.Count);

        var fresh = Harness.FromGold();
        var kept = fresh.Run(Resolve(fresh, enabled: true,
            Group("W10", "W10", "W11", "W12"),    // the memory triple — all three on chapter 9
            Group("W7", "W6", "W7")));            // the Daniel pair that shares primary chapter 13

        // 18 − 2 (memory) − 1 (Daniel) = 15. THE MERGE MAP STILL EARNS ITS KEEP: both of those merges are BELOW b4's
        // 0.45 threshold (0.375 and 0.444) and NO threshold can reach them — 0.44 would also catch the 0.462 DISTINCT
        // contradiction pair. Only something that reads MEANING can close them, which is the whole premise of b8.
        Assert.Equal(15, kept.Count);

        // EVERY FENCE: the sev3 contradiction, the 0.364 distinct tone pair, AND (be-c07) the two cross-chapter
        // duplicates the model may no longer touch — all survive, whole.
        Assert.Single(kept, f => f.Rationale == ContraDaniel && f.Severity == 3);
        Assert.Single(kept, f => f.Rationale == ToneCold15);
        Assert.Single(kept, f => f.Rationale == ToneScience1);   // ch1  ─┬─ 0.875, a REAL duplicate, and the fence
        Assert.Single(kept, f => f.Rationale == ToneScience15);  // ch15 ─┘  keeps them apart anyway. Accepted cost.
        Assert.Single(kept, f => f.Rationale == DanielSleep14_15); // ch14 — not swept into the ch13 group

        // And no distinct finding was lost: every remaining real finding is still there exactly once.
        foreach (var survivor in new[] { CharMarara, CharTamar, ContraDaniel, ContYahya, PlotMap, PlotBreak, ThemeWorry, ThemeMatter, ToneKtiv, ToneCold15 })
            Assert.Single(kept, f => f.Rationale == survivor);
    }

    [Fact]
    public void MergeMap_TheSurvivorTakesTheMaxSeverityOfTheGroup()
    {
        // b4b's rule, carried into PASS 0. The model routinely re-emits one finding at different severities across
        // windows; since the SURVIVOR here is whichever copy the model named, taking its severity verbatim would let
        // that choice silently DOWNGRADE a major finding. (The Daniel rows are all severity 1 on the real book, so
        // the severity split is the synthetic part of this case — the rationales and anchors are still the real ones,
        // and the two share primary chapter 13, so the be-c07 anchor fence allows the group.)
        var h = new Harness();
        foreach (var row in new[]
                 {
                     new Row("W1", "continuity", 1, new[] { 13, 14, 15 }, DanielSleep13_15),
                     new Row("W2", "continuity", 3, new[] { 13 }, DanielExhaust13), // the model called this one MAJOR
                 })
        {
            var (candidate, item) = Build(row);
            h.Candidates.Add(candidate);
            h.Items.Add(item);
            h.IdMap[row.Id] = item;
        }

        var kept = h.Run(Resolve(h, enabled: true, Group("W1", "W1", "W2")));

        var survivor = Assert.Single(kept);
        Assert.Equal(DanielSleep13_15, survivor.Rationale); // the model's chosen survivor, verbatim
        Assert.Equal(3, survivor.Severity);                 // but never at the lower severity
    }

    [Fact]
    public void MergeMap_NeverFabricatesProse_TheSurvivorRowIsOneOfTheOriginals_Untouched()
    {
        // The survivor keeps its OWN rationale, suggestedAction and evidence. Grafting one copy's quotes onto
        // another's prose would fabricate a finding that neither of them states (b4's rule, unchanged here).
        var h = Harness.FromGold();
        var survivorBefore = h.Candidates[6].Finding!; // W7
        var evidenceBefore = survivorBefore.EvidenceJson;
        var keyBefore = survivorBefore.DedupKey;

        var kept = h.Run(Resolve(h, enabled: true, Group("W7", "W6", "W7")));

        var survivor = Find(kept, DanielExhaust13);
        Assert.Same(survivorBefore, survivor);
        Assert.Equal(DanielExhaust13, survivor.Rationale); // NOT a merged sentence
        Assert.Equal(evidenceBefore, survivor.EvidenceJson);
        Assert.Equal(keyBefore, survivor.DedupKey);        // the key did not move
    }

    // ── 3. Fail-closed: a malformed group is REJECTED WHOLE, never half-honoured ──────────────────────

    [Theory]
    // an id the digest never printed (the model invented it) — the same failure class as a phantom chapter anchor
    [InlineData("W5", new[] { "W5", "W99" })]
    // the same finding named twice: not a merge, a model that has lost track of what it is looking at
    [InlineData("W5", new[] { "W5", "W5" })]
    // a group of one says nothing
    [InlineData("W5", new[] { "W5" })]
    // "keep" from OUTSIDE the group: picking a survivor for the model would be US deciding which finding is lost
    [InlineData("W9", new[] { "W5", "W6" })]
    // no "keep" at all
    [InlineData(null, new[] { "W5", "W6" })]
    // a blank id
    [InlineData("W5", new[] { "W5", "  " })]
    public void MergeMap_AMalformedGroup_IsIgnoredEntirely_AndNoFindingIsTouched(string? keep, string[] ids)
    {
        var h = Harness.FromGold();
        var resolution = SynthesisMergeMap.Resolve(
            enabled: true,
            new List<SynthesisMergeItem> { new() { Ids = ids.ToList(), Keep = keep } },
            h.IdMap,
            NullLogger.Instance);

        Assert.Empty(resolution.Groups);
        Assert.Equal(1, resolution.ProposedGroups);
        Assert.NotEmpty(resolution.Rejections); // the reason is COUNTED, never swallowed

        // The findings are exactly what they would be with no merge map at all — including both Daniel copies the
        // malformed group half-named. A group we cannot fully trust changes NOTHING.
        var kept = h.Run(resolution);
        Assert.Equal(17, kept.Count);
        Assert.Single(kept, f => f.Rationale == DanielSleep14_15);
        Assert.Single(kept, f => f.Rationale == DanielSleep13_15);
    }

    [Fact]
    public void MergeMap_AFindingNamedInTwoGroups_KeepsTheFirstGroupAndRejectsTheSecond()
    {
        // One finding, one group. A second group reaching for a finding the first already claimed is a model that
        // is confused about the set, so its whole group is dropped rather than partially applied. (Both groups here
        // sit on chapter 9, so the be-c07 anchor fence is not what decides this — the one-group-per-finding rule is.)
        var h = Harness.FromGold();
        var resolution = Resolve(h, enabled: true,
            Group("W10", "W10", "W11"),       // accepted
            Group("W12", "W11", "W12"));      // W11 is already spoken for → REJECTED WHOLE

        Assert.Single(resolution.Groups);
        Assert.Equal(1, resolution.Rejections["finding-already-in-another-group"]);

        var kept = h.Run(resolution);
        Assert.DoesNotContain(kept, f => f.Rationale == ThemeMemB); // absorbed by the first group
        Assert.Single(kept, f => f.Rationale == ThemeMemAck);        // the first group's survivor
        Assert.Single(kept, f => f.Rationale == ThemeMemC);          // untouched: its group was rejected
    }

    [Fact]
    public void MergeMap_NoMergesEmitted_LeavesTheCollapsePipelineExactlyAsItWasPreB8()
    {
        // The additive promise, stated PRECISELY (be-c06 — this test used to be named "...IsByteIdenticalToTheOld
        // Behaviour", which claimed more than it proves): given the SAME model output, a response with no "merges"
        // key drives the collapse pipeline to exactly the findings it produced pre-b8. PASS 0 is a pure no-op.
        //
        // It does NOT prove the OFF build is a pre-b8 build. The model's output is upstream of everything here, and
        // b8 changed the PROMPT unconditionally, so the response itself may differ. That is the one claim this file
        // must never make; the prompt-side truth is pinned in BookReviewServiceTests (be-c06).
        var withMap = Harness.FromGold();
        var resolution = SynthesisMergeMap.Resolve(enabled: true, proposed: null, withMap.IdMap, NullLogger.Instance);
        var withEmptyMap = withMap.Run(resolution);

        var withoutMap = Harness.FromGold().Run(resolution: null);

        Assert.Equal(0, resolution.ProposedGroups);
        Assert.Equal(
            withoutMap.Select(f => f.Dimension + "|" + f.Rationale + "|" + f.Severity).OrderBy(s => s, StringComparer.Ordinal),
            withEmptyMap.Select(f => f.Dimension + "|" + f.Rationale + "|" + f.Severity).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void MergeMap_AKeepThatTheExactKeyDedupAlreadyDropped_RejectsTheGroup()
    {
        // The exact-key dedup runs BEFORE this pass, so a byte-identical re-emission is already gone. If the model
        // chose THAT copy as the survivor, its group's premise has shifted under it. Promoting a different member
        // would be us choosing which finding the user loses — so the group is rejected and both copies stay.
        var h = new Harness();
        var rows = new[]
        {
            new Row("W1", "continuity", 1, new[] { 13 }, DanielExhaust13),
            new Row("W2", "continuity", 1, new[] { 14, 15 }, DanielSleep14_15),
        };
        foreach (var row in rows)
        {
            var (candidate, item) = Build(row);
            h.Items.Add(item);
            h.IdMap[row.Id] = item;
            if (row.Id == "W2") // W1's entity never reaches the candidate list (modelling the exact-key drop)
                h.Candidates.Add(candidate);
        }
        // Index-align: the candidate list holds only W2, so the item list handed to Apply must too.
        var items = new List<BookFindingItem> { h.IdMap["W2"] };

        var resolution = Resolve(h, enabled: true, Group("W1", "W1", "W2"));
        Assert.Single(resolution.Groups); // it VALIDATES (both ids were printed)...

        var merged = SynthesisMergeMap.Apply(h.Candidates, items, resolution, NullLogger.Instance);
        Assert.Single(merged); // ...but at APPLY time the survivor is not there, so nothing is merged.
        Assert.Equal(DanielSleep14_15, merged[0].Finding!.Rationale);
    }

    [Fact]
    public void MergeMap_AFaultInTheMap_FailsClosed_AndReturnsEveryFinding()
    {
        // Non-throwing on any shape of garbage. A null id list, a null group, an empty proposal.
        var h = Harness.FromGold();
        var resolution = SynthesisMergeMap.Resolve(
            enabled: true,
            new List<SynthesisMergeItem> { null!, new() { Ids = null, Keep = "W1" }, new() { Ids = new List<string>(), Keep = null } },
            h.IdMap,
            NullLogger.Instance);

        Assert.Empty(resolution.Groups);
        Assert.Equal(17, h.Run(resolution).Count);
    }

    // ── 4. The kill-switch, and the coverage log that keeps its OFF state honest ──────────────────────

    [Fact]
    public void MergeMap_WithTheKillSwitchOff_AppliesNothing_ButStillMEASURESWhatWouldHaveHappened()
    {
        // It ships OFF. The OFF state must not be a blind spot: the map is still parsed, validated and LOGGED, so
        // the coverage line says what the model proposed and what flipping the switch would do — which is the whole
        // point of a staged rollout. Nothing is APPLIED.
        //
        // be-c06: "nothing is applied" is the exact and only guarantee. The OFF build is NOT a pre-b8 build — the
        // synthesis PROMPT still carries the merge contract, so the model's own findings can differ (pinned end-to-end
        // by BookReviewServiceTests.BuildBookReviewAsync_WithTheKillSwitchOff_TheSynthesisPromptStillCarriesTheMergeContract).
        // This test sees only the collapse pipeline, which is downstream of the model, so what IT pins is that PASS 0
        // is a no-op and the collapser alone decides the outcome.
        var logger = new ListLogger();
        var h = Harness.FromGold();
        var kept = h.Run(Resolve(h, enabled: false, Group("W10", "W10", "W11", "W12")), logger);

        Assert.Equal(17, kept.Count); // exactly what the collapser alone keeps: PASS 0 changed nothing
        Assert.Single(kept, f => f.Rationale == ThemeMemC);

        var coverage = Assert.Single(logger.Messages, m => m.Contains("synthesis merge map") && m.Contains("switch"));
        Assert.Contains("switch OFF", coverage);
        Assert.Contains("proposed 1 merge group(s)", coverage);
        Assert.Contains("would have merged 2 finding(s) away", coverage);

        // The model's answer is NAMED, not just counted — a human can check "W10+W11+W12->W10" against the digest it
        // was shown, which is the decision procedure for flipping the switch. Its voice is heard; only its hand is
        // stayed. (be-c07: the OFF forecast now runs through the anchor fence too, so what it reports as "would have
        // merged" is what a flipped build would ACTUALLY merge — a group the fence refuses is not forecast as applied.)
        Assert.Contains("W10+W11+W12->W10", coverage);
        Assert.Contains("gates the APPLY step, not the prompt", coverage);
    }

    [Fact]
    public void MergeMap_WithTheKillSwitchOff_AlsoLogsTheDigestSummary_NotOnlyOnTheONPath()
    {
        // P2-5 / be-f01. DigestSummary answers "of the duplicates that SURVIVED, which were even in front of the
        // model" — a STRUCTURAL miss vs a MODEL-RECALL miss (see the property's own xmldoc). That question belongs
        // to the OFF state, which is the state that SHIPS. Before this fix the OFF branch `return`ed before the
        // line that logs it (only the ON path reached it), so the rollout's key diagnostic was never emitted in
        // the state anyone actually runs. (Premise-verified by revert: moving this assertion's target log call
        // back below the `if (!resolution.Enabled) { ...; return all; }` block fails this test.)
        var logger = new ListLogger();
        var h = Harness.FromGold();
        h.Run(Resolve(h, enabled: false, Group("W10", "W10", "W11", "W12")), logger);

        Assert.Contains(logger.Messages,
            m => m.Contains("the digest the model was shown") && m.Contains("W3=continuity/12"));
    }

    [Fact]
    public void MergeMap_WithTheKillSwitchOff_TheForecastIsFENCED_AGroupTheFenceWouldRefuseIsNotReportedAsApplicable()
    {
        // The OFF coverage line is the evidence a human reads to decide whether to flip a model-driven DELETE on, so
        // it must forecast what a FLIPPED build would really do — not what the model merely asked for. The anchor
        // fence runs in BOTH switch states (it is part of the plan, which is computed before the switch is consulted),
        // so a cross-chapter group is reported as REJECTED, not as "would have merged".
        var logger = new ListLogger();
        var h = Harness.FromGold();
        h.Run(Resolve(h, enabled: false, Group("W18", "W16", "W18")), logger); // ch1 + ch15

        var coverage = Assert.Single(logger.Messages, m => m.Contains("synthesis proposed"));
        Assert.Contains("switch OFF", coverage);
        Assert.Contains("proposed 1 merge group(s)", coverage);
        Assert.Contains("would have merged 0 finding(s) away", coverage);
        Assert.Contains("rejected 1 (anchors-span-different-chapters=1)", coverage);
    }

    [Fact]
    public void MergeMap_LogsCoverage_EvenWhenNothingWasMerged()
    {
        // THE b4c TRAP, GUARDED. b4c shipped with 136 green tests while its fold fired ZERO times on real data, and
        // nothing said so: a guard that reports only its POSITIVE count is indistinguishable from a guard that was
        // never wired. So the merge map reports its coverage on EVERY build — proposed, applied, rejected — including
        // the all-zero build. If this line ever stops being emitted, "0 merges" stops being evidence of anything.
        var logger = new ListLogger();
        var h = Harness.FromGold();
        h.Run(SynthesisMergeMap.Resolve(enabled: true, proposed: null, h.IdMap, logger), logger);

        var coverage = Assert.Single(logger.Messages, m => m.Contains("synthesis merge map") && m.Contains("switch"));
        Assert.Contains("switch ON", coverage);
        Assert.Contains("proposed 0 merge group(s)", coverage);
        Assert.Contains("applied 0", coverage);
        Assert.Contains("over 18 digest id(s)", coverage);
    }

    [Fact]
    public void MergeMap_LogsTheAppliedCountAndTheRejectionReasons()
    {
        var logger = new ListLogger();
        var h = Harness.FromGold();
        h.Run(Resolve(h, enabled: true,
            Group("W10", "W10", "W11", "W12"),
            Group("W1", "W1", "W99")), logger); // an invented id

        var coverage = Assert.Single(logger.Messages, m => m.Contains("synthesis merge map") && m.Contains("switch"));
        Assert.Contains("switch ON", coverage);
        Assert.Contains("proposed 2 merge group(s)", coverage);
        Assert.Contains("applied 1", coverage);
        Assert.Contains("merged 2 finding(s) away", coverage);
        Assert.Contains("rejected 1 (unknown-id=1)", coverage);

        // The log names the group the model actually made, not just how many. During a staged rollout "1 group
        // applied" is unreadable; "W10+W11+W12->W10" can be checked by a human against the digest it was shown.
        Assert.Contains("Applied: [W10+W11+W12->W10]", coverage);

        // And the digest itself is logged (at Debug), because a duplicate that SURVIVES is only a model-recall miss
        // if both its copies were in the id space at all. Without this, a structural miss and a recall miss look
        // identical from the outside.
        Assert.Contains(logger.Messages, m => m.Contains("the digest the model was shown") && m.Contains("W3=continuity/12"));
    }

    // ═══ be-c08 — THE THREE APPLY-TIME DEFECTS (P3-5, P3-7, P3-8) ════════════════════════════════════
    //
    // All three live in the same few lines and all three have the same shape: the pass acted on a state it had not
    // finished reasoning about. It MUTATED survivors as it went (so a fault left a half-merged set behind a comment
    // claiming otherwise), it GUESSED when it could not read a survivor's anchors (adopting another finding's
    // chapters), and it let a merge CREATE anchors[0] where there had been none (moving the dedup key's own input).
    // ═════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MergeMap_AFaultPartWayThroughTheApply_LeavesEVERYFindingExactlyAsItArrived()
    {
        // P3-5. The catch has always said it returns "the UN-merged set … what the pipeline had BEFORE this pass".
        // It returned the un-merged LIST — but the FINDINGS in it had already been rewritten: the pre-fix loop
        // validated a group and immediately unioned its anchors and lifted its severity, so a fault on group 2 shipped
        // group 1's mutations anyway. Not a lost finding, but a state nobody had reasoned about, sitting behind a
        // comment asserting the opposite. (The FOURTH time this subsystem has shipped a false invariant in a comment;
        // hence the fix is in the CODE — plan/stage everything, then commit in a loop that cannot fault.)
        //
        // THE FAULT IS REAL DATA, not a mock: an anchors payload of "[null]" PARSES (System.Text.Json yields a
        // one-element list holding null) and then throws NullReferenceException the moment the union reads
        // anchor.ChapterId. It is the same corrupt-payload class as P3-7's unparseable JSON, one notch further along.
        var h = new Harness();
        var rows = new[]
        {
            // Group 1 is FULLY VALID and would apply: its survivor would gain chapters 14+15 AND be lifted to
            // severity 3. Both mutations are the assertion below.
            new Row("W1", "continuity", 3, new[] { 13, 14, 15 }, DanielSleep13_15), // group 1: absorbed  (sev 3)
            new Row("W2", "continuity", 1, new[] { 13 }, DanielExhaust13),          // group 1: SURVIVOR  (sev 1)
            new Row("W3", "theme", 1, new[] { 9 }, ThemeMemAck),                    // group 2: SURVIVOR — poisoned
            new Row("W4", "theme", 2, new[] { 9 }, ThemeMemB),                      // group 2: absorbed
        };
        foreach (var row in rows)
        {
            var (candidate, item) = Build(row);
            h.Candidates.Add(candidate);
            h.Items.Add(item);
            h.IdMap[row.Id] = item;
        }

        // Group 2's survivor carries a payload that parses to [null]. Its PrimaryChapterOrder (9) is unaffected, so
        // the group still passes every id rule AND the be-c07 anchor fence — the fault surfaces inside the union.
        h.Candidates[2].Finding!.ChapterAnchorsJson = "[null]";

        var survivorOfGroup1 = h.Candidates[1].Finding!;
        var anchorsBefore = survivorOfGroup1.ChapterAnchorsJson;
        var severityBefore = survivorOfGroup1.Severity;
        Assert.Equal(new[] { 13 }, AnchorOrders(survivorOfGroup1)); // premise: group 1's union WOULD add 14 and 15

        var logger = new ListLogger();
        var kept = SynthesisMergeMap.Apply(
            h.Candidates, h.Items,
            Resolve(h, enabled: true, Group("W2", "W1", "W2"), Group("W3", "W3", "W4")),
            logger);

        // THE FAIL-SAFE: every candidate comes back, un-merged.
        Assert.Equal(4, kept.Count);

        // AND EVERY FINDING IS EXACTLY WHAT ARRIVED — this is the assertion the pre-fix code failed. Group 1 was
        // validated, fenced and (pre-fix) APPLIED before group 2 blew up: its survivor came back carrying chapters
        // 14 and 15 it never claimed, inside a set the caller was told had not been merged.
        Assert.Equal(anchorsBefore, survivorOfGroup1.ChapterAnchorsJson);
        Assert.Equal(new[] { 13 }, AnchorOrders(survivorOfGroup1));
        Assert.Equal(severityBefore, survivorOfGroup1.Severity);

        // ...and the copy group 1 would have deleted is still here, because the merge never happened at all.
        Assert.Contains(kept, c => c.Finding!.Rationale == DanielSleep13_15);

        // The fault is SURFACED, never swallowed.
        Assert.Contains(logger.Messages, m => m.Contains("apply faulted") && m.Contains("UN-merged"));
    }

    [Fact]
    public void MergeMap_AFaultInTheAUDITLOG_LeavesEVERYFindingExactlyAsItArrived()
    {
        // final-r01 — THE HALF OF P3-5 THAT be-c08 DID NOT FINISH, and the test above cannot see.
        //
        // be-c08 STAGED the mutations, which is necessary but NOT sufficient: what makes the catch's promise true is
        // that no statement which CAN FAULT runs after the first entity write. The commit loop it left behind wrote
        // the survivor's anchors and severity and THEN emitted the per-group audit line — and a logger can throw. So
        // the fail-safe still handed back a set it called "UN-merged" with survivors already merged inside it.
        //
        // The test above (MergeMap_AFaultPartWayThroughTheApply_...) CANNOT catch this: its fault (a "[null]" anchors
        // payload) fires inside TryStageMerge, i.e. during PLANNING, before the commit loop is ever entered. It went
        // green against the broken commit loop. THIS test injects the fault at the LOGGER — the same surface be-c08
        // itself chose for the collapser, on the grounds that it is "the strongest form of the property" — which is
        // the ONE fault surface that lives INSIDE the commit loop.
        var h = new Harness();
        var rows = new[]
        {
            new Row("W1", "continuity", 3, new[] { 13, 14, 15 }, DanielSleep13_15), // absorbed (sev 3)
            new Row("W2", "continuity", 1, new[] { 13 }, DanielExhaust13),          // SURVIVOR (sev 1)
        };
        foreach (var row in rows)
        {
            var (candidate, item) = Build(row);
            h.Candidates.Add(candidate);
            h.Items.Add(item);
            h.IdMap[row.Id] = item;
        }

        var survivor = h.Candidates[1].Finding!;
        var anchorsBefore = survivor.ChapterAnchorsJson;
        var severityBefore = survivor.Severity;

        // PREMISES — this group is FULLY VALID and BOTH mutations really would fire, so a green result cannot be the
        // merge quietly not applying for some unrelated reason.
        Assert.Equal(new[] { 13 }, AnchorOrders(survivor)); // the union WOULD add 14 and 15
        Assert.Equal(1, severityBefore);                    // the MAX would lift it to 3
        Assert.True(NearDuplicateCollapser.MayFold(13, 13), "premise: the be-c07 anchor fence lets this group through");

        var logger = new ThrowingLogger();
        var kept = SynthesisMergeMap.Apply(
            h.Candidates, h.Items, Resolve(h, enabled: true, Group("W2", "W1", "W2")), logger);

        // THE FAIL-SAFE: both candidates come back, un-merged.
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, c => c.Finding!.Rationale == DanielSleep13_15);

        // ...AND THE SURVIVOR IS EXACTLY WHAT ARRIVED. Pre-fix it came back carrying chapters 14 and 15 it never
        // claimed, at a severity the model never gave it, inside a set the caller was told had NOT been merged.
        Assert.Equal(anchorsBefore, survivor.ChapterAnchorsJson);
        Assert.Equal(new[] { 13 }, AnchorOrders(survivor));
        Assert.Equal(severityBefore, survivor.Severity);
    }

    [Fact]
    public void MergeMap_AnUnreadableAnchorPayloadOnTheSURVIVOR_RejectsTheGroup_AndNeverAdoptsTheOtherCopysChapters()
    {
        // P3-7 — THE FAIL-SAFE THAT POINTED THE WRONG WAY. The pre-fix reader answered a JsonException with an EMPTY
        // anchor list, on either side of the merge, "because this JSON was written by the same build a moment ago and
        // always parses". On the ABSORBED side that is merely lossy. On the SURVIVOR's side it is an identity swap:
        // the union appends the absorbed copy's anchors to nothing, and the survivor is rewritten carrying ONLY THE
        // OTHER FINDING'S CHAPTERS — its own (unreadable) anchors gone, its card now pointing the user at chapters it
        // never claimed, and its anchors[0] no longer the one its dedup key was hashed on.
        //
        // Unknown scope is not an empty scope. Everywhere else in this subsystem an unreadable anchor payload means
        // "do not act" (BookFindingReconciler.ChapterOrdersOf → null → never deleted, never fuzzy-matched). Here too, now:
        // the GROUP is rejected, both findings survive, and the payload is left exactly as it was found.
        var h = new Harness();
        foreach (var row in new[]
                 {
                     new Row("W1", "continuity", 1, new[] { 13 }, DanielExhaust13),          // SURVIVOR — unreadable
                     new Row("W2", "continuity", 1, new[] { 13, 14, 15 }, DanielSleep13_15), // absorbed
                 })
        {
            var (candidate, item) = Build(row);
            h.Candidates.Add(candidate);
            h.Items.Add(item);
            h.IdMap[row.Id] = item;
        }

        const string corrupt = "{ this is not an anchor array";
        h.Candidates[0].Finding!.ChapterAnchorsJson = corrupt;

        var logger = new ListLogger();
        var kept = SynthesisMergeMap.Apply(
            h.Candidates, h.Items, Resolve(h, enabled: true, Group("W1", "W1", "W2")), logger);

        // REJECTED: nothing merged, nothing deleted, nothing adopted.
        Assert.Equal(2, kept.Count);
        Assert.Equal(corrupt, h.Candidates[0].Finding!.ChapterAnchorsJson); // NOT rewritten with W2's [13,14,15]
        Assert.Contains(kept, c => c.Finding!.Rationale == DanielSleep13_15);

        // And the refusal is COUNTED and NAMED — the model's map was fine; OUR data was not, and that has to be visible.
        var coverage = Assert.Single(logger.Messages, m => m.Contains("synthesis proposed"));
        Assert.Contains("applied 0", coverage);
        Assert.Contains("rejected 1 (anchors-unreadable=1)", coverage);
    }

    [Fact]
    public void MergeMap_ABookWideKeep_MayNotAbsorbAnAnchoredMember_BecauseTheUnionWouldCREATEItsAnchorsZero()
    {
        // P3-8 — THE ONE WAY AN APPEND-ONLY UNION CAN STILL MOVE anchors[0]: when there is no anchors[0] to append
        // after. W9 (plot) anchors NOTHING, so its dedup key is hashed on "none" and its collapse bucket is null. Let
        // it absorb W8 (plot, chapter 3) and the append CREATES anchors[0] = chapter 3, at which point the row carries:
        //     ChapterAnchorsJson[0].Order = 3   ·   DedupKey hashed at "none"   ·   PrimaryChapterOrder = null
        // — three values that are supposed to agree BY CONSTRUCTION (BookFindingReconciler.ComparisonOrderOf says so in
        // its xmldoc) and now do not. The card claims chapter 3 while every rule that decides whether it is the SAME
        // finding as some other one still treats it as book-wide. Worst of all, its bucket is still the no-anchor one,
        // so the VERY NEXT pass (b4b's FoldNoAnchorIntoAnchored) may fold the model's chosen survivor away — taking
        // the chapters just unioned onto it with it.
        //
        // The group is now REJECTED. The cost is a visible duplicate, and it is the right trade twice over: b4b's own
        // rule is that the ANCHORED copy must survive a fold with a book-wide one (a navigable finding is never traded
        // for a book-wide one), so a model that keeps the book-wide copy is asking for the outcome the deterministic
        // pass exists to prevent — and that pass will close the pair itself if they really are one finding.
        var h = Harness.FromGold();
        var w9 = h.Candidates[8].Finding!; // plot, NO anchors
        var w8 = h.Candidates[7].Finding!; // plot, chapter 3
        Assert.Empty(AnchorOrders(w9));                                                    // premise: book-wide…
        Assert.Null(h.Candidates[8].PrimaryChapterOrder);                                  // …in every representation
        Assert.Equal(BookFinding.ComputeDedupKey("plot", null, PlotBreak), w9.DedupKey);   // …including its key

        var logger = new ListLogger();
        var kept = SynthesisMergeMap.Apply(
            h.Candidates, h.Items, Resolve(h, enabled: true, Group("W9", "W8", "W9")), logger);

        // REJECTED — and the be-c07 anchor fence is NOT what refused it (MayFold(null, 3) is a wildcard: it says YES).
        // This is its own rule, on its own reason, and the log names it.
        var coverage = Assert.Single(logger.Messages, m => m.Contains("synthesis proposed"));
        Assert.Contains("applied 0", coverage);
        Assert.Contains("rejected 1 (keep-is-book-wide-but-a-member-is-anchored=1)", coverage);

        // BOTH findings survive, and the book-wide one is still book-wide — key, anchors and bucket order still agree.
        Assert.Equal(h.Candidates.Count, kept.Count);
        Assert.Empty(AnchorOrders(w9));
        Assert.Equal(BookFinding.ComputeDedupKey("plot", null, PlotBreak), w9.DedupKey);
        Assert.Equal(new[] { 3 }, AnchorOrders(w8)); // the anchored copy is untouched and still navigable

        // THE OTHER DIRECTION IS UNCHANGED, and this is what keeps the fix from being a blanket ban: an ANCHORED keep
        // absorbing a BOOK-WIDE member still merges. That is where the real duplicates are (all seven surviving pairs
        // at the b5 gate were exactly this shape), the append lands after a real anchors[0], and nothing moves.
        var h2 = Harness.FromGold();
        var kept2 = h2.Run(Resolve(h2, enabled: true, Group("W8", "W8", "W9")));
        Assert.Single(kept2, f => f.Rationale == PlotMap);
        Assert.DoesNotContain(kept2, f => f.Rationale == PlotBreak);
    }

    [Fact]
    public void MergeMap_WhenTheSynthesisPassNeverRan_NothingIsLoggedAndNothingChanges()
    {
        // NULL resolution = the pass did not run (a book with no BookBrief, or the legacy per-dimension path). That
        // is a different state from "it ran and proposed nothing", and it must not produce a coverage line claiming
        // a channel that was never opened.
        var logger = new ListLogger();
        var h = Harness.FromGold();
        var kept = h.Run(resolution: null, logger);

        Assert.Equal(17, kept.Count);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("synthesis merge map"));
    }

    [Fact]
    public void SynthesisMergeMap_DoesNotDeclareItsOwnAnchorJsonOptions_ItReusesBookReviewServices()
    {
        // NIT-5. This class used to re-declare AnchorSerializeOpts / AnchorDeserializeOpts, byte-identical to
        // BookReviewService.SerializeOpts / DeserializeOpts — two independently-maintained JsonSerializerOptions
        // for the SAME wire shape (List<FindingChapterAnchor>, the same BookFinding.ChapterAnchorsJson field), a
        // THIRD copy of an already-duplicated pattern and drift waiting to happen (a tweak to one but not the
        // other would silently change how a merge-map anchor union round-trips). Fixed by single-sourcing:
        // BookReviewService's options are now internal and SynthesisMergeMap references them directly. Pin the
        // absence of a private JsonSerializerOptions field here so the duplicate cannot silently creep back.
        var privateJsonOptionsFields = typeof(SynthesisMergeMap)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(JsonSerializerOptions))
            .ToList();

        Assert.Empty(privateJsonOptionsFields);
    }

    private sealed class ListLogger : ILogger
    {
        public readonly List<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Messages.Add(fmt(state, ex));
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    /// <summary>An ILogger that FAULTS on every level except Warning (so the fail-safe's OWN log still gets out and
    /// the catch can complete). Mirrors the collapser's P3-5 ThrowingLogger: the logger is the only fault surface
    /// that lives inside <c>Apply</c>'s commit block, so it is what proves the entity writes really are the last
    /// thing the method does.</summary>
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (level != LogLevel.Warning)
                throw new InvalidOperationException("logger faulted");
        }

        private sealed class Scope : IDisposable { public void Dispose() { } }
    }
}
