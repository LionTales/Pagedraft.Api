namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// LiteraryTermGlossary — curated English -> Hebrew glossary for the analysis-
// output repair layer's deterministic pass.
//
// Plan: src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md
//       (todo p2-glossary; seeded from the "Phase 0 baseline" diagnostic's real
//       CONTENT-value leaks — "(Action)", "High Stakes", "Magic vs. Nature",
//       "Tension" — plus common, unambiguous literary/linguistic terms).
//
// THIS IS A CLOSED, CONSERVATIVE, REVIEWED LIST. Only unambiguous 1:1 English ->
// Hebrew literary/linguistic terms belong here. A term that is NOT in this map
// is deliberately left untouched by the repair layer downstream (p2-glossary-
// apply) — that is the FAIL-SAFE default, not a bug. When in doubt about
// whether a Hebrew rendering is the single accepted equivalent, LEAVE THE TERM
// OUT rather than guess; ambiguous/context-dependent terms are handled (if at
// all) by the value-scoped LLM repair pass (p3-repair-service), never here.
//
// VALIDATION STATUS: these Hebrew equivalents are the standard academic/
// literary-criticism renderings, but — like the KtivMaleWordList
// (Services/Analysis/Hebrew/KtivMaleWordList.cs) — they still need NATIVE-
// SPEAKER validation before being trusted as ground truth at scale. Mirror the
// proofread `c04` deferral: do not fabricate confidence in an entry that
// hasn't been checked against a real leak; when unsure, leave it out.
//
// GROWTH HABIT (see p6-docs): every real production leak found by
// OutputQualityDiagnostic / live usage that is NOT covered here should be
// promoted into this glossary (and into repair-gold.json) as a new reviewed
// entry, then the deterministic + gold gates re-run. This mirrors the
// proofread-gold-growth habit (see memory: proofread-gold-growth-habit).
// ---------------------------------------------------------------------------

/// <summary>
/// Closed, conservative English -> Hebrew glossary for literary/linguistic
/// terms observed leaking (untranslated) into Hebrew analysis prose. Lookups
/// are case-insensitive. See the header comment for scope and the fail-safe
/// contract: an absent key means "leave untouched", never "guess".
/// </summary>
public static class LiteraryTermGlossary
{
    /// <summary>
    /// English term -> accepted Hebrew equivalent. Case-insensitive
    /// (<see cref="StringComparer.OrdinalIgnoreCase"/>). Keys are the bare
    /// English term as it tends to leak (lowercase; the repair-apply pass is
    /// responsible for word-boundary matching and preserving surrounding
    /// punctuation/casing in the source text, not this map).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Terms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // ── Literary / narrative craft ──────────────────────────────────
        ["narrator"] = "מספר",
        ["tone"] = "טון",
        ["mood"] = "מצב רוח",
        ["foreshadowing"] = "רמיזה מקדימה",
        ["imagery"] = "דימויים",
        ["tension"] = "מתח",
        ["suspense"] = "מתח",
        ["climax"] = "שיא",
        ["anticlimax"] = "אנטי-שיא",
        ["metaphor"] = "מטפורה",
        ["simile"] = "דימוי",
        ["irony"] = "אירוניה",
        ["pacing"] = "קצב",
        ["plot"] = "עלילה",
        ["theme"] = "תמה",
        ["protagonist"] = "גיבור",
        ["antagonist"] = "אנטגוניסט",
        ["dialogue"] = "דיאלוג",

        // ── Linguistic ───────────────────────────────────────────────────
        ["register"] = "משלב",

        // ── Real diagnostic leaks (Phase 0 baseline; narrative/social terms
        //    that leaked in parenthetical/descriptive form) ────────────────
        ["action"] = "פעולה",
        ["face-saving"] = "שמירת כבוד",
        ["high stakes"] = "סיכונים גבוהים",
        ["nature"] = "טבע",
    };

    /// <summary>
    /// Looks up the accepted Hebrew equivalent for an English literary/
    /// linguistic term. Case-insensitive. Returns false (and leaves
    /// <paramref name="hebrew"/> empty) if the term is not in the closed list
    /// — callers must treat that as "leave the source text untouched", never
    /// synthesize a translation.
    /// </summary>
    public static bool TryGet(string english, out string hebrew)
    {
        if (string.IsNullOrWhiteSpace(english))
        {
            hebrew = string.Empty;
            return false;
        }

        return Terms.TryGetValue(english.Trim(), out hebrew!);
    }
}
