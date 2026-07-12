using System.Collections;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// BookEntitySet — the TWO-TIER proper-noun set produced by BookEntityProvider and
// consumed by ForeignRunClassifier's bookEntities LEAVE lever (be-c04).
//
// The two tiers differ ONLY in how membership is MATCHED:
//
//   DECLARED names (tier 1) — from a stored CharacterAnalysis / BookProfile.
//       Matched CASE-INSENSITIVELY. These are AUTHORITATIVE proper nouns: a human/model
//       declared them as the book's characters, so every casing of them is the name
//       ("Dolores", "dolores", "DOLORES") and every casing must be spared.
//
//   MANUSCRIPT-harvested tokens (tier 2) — from the prose scan.
//       Matched CASE-SENSITIVELY (Ordinal). These are only INFERRED proper nouns: the
//       evidence for them is a CAPITALIZED surface form, so they may be spared only in
//       the capitalized surface form that was actually observed.
//
// WHY THE ASYMMETRY (be-c04, measured — see the plan's "Investigation findings"):
// membership used to be case-insensitive for BOTH tiers, which meant ONE capitalized Latin
// token anywhere in a Hebrew manuscript spared EVERY LOWERCASE occurrence of that word in
// the analysis output. Measured against the real 80-chapter Hebrew manuscript fixture: a
// SINGLE English epigraph line ("A story of Confusion and Nostalgia, of Tension without
// Catharsis.") harvested 4 tokens — all of them on one mid-sentence occurrence — and flipped
// 3 of the 10 d5 leak seeds (confusion, nostalgia, catharsis) from REPAIR to LEAVE. That is a
// 30% RECALL regression on the exact leak class the dynamic repair exists to clean, bought
// with one sentence. The leak class is LOWERCASE by construction (a leaked common word leaks
// lowercase); the name evidence is UPPERCASE by construction. Matching the manuscript tier
// case-sensitively severs that link exactly: harvested "Confusion" still spares "Confusion",
// and no longer spares "confusion".
//
// It is deliberately the ONLY thing that changed. The harvest condition itself
// (recurrence OR mid-sentence) is untouched, so a name seen mid-sentence in a single chapter
// ("Berlin") is still harvested and still spared — the bias-to-LEAVE that protects the book's
// own names is intact for every casing the manuscript actually shows.
//
// NOTE for the caseless (Hebrew) harvest direction: Hebrew has no letter case, so Ordinal and
// OrdinalIgnoreCase agree on every Hebrew token. The tier-2 tightening is therefore a NO-OP for
// the Latin-native-book direction that be-c03 added, and cannot weaken it.
//
// CONTRACT WITH THE CLASSIFIER: this is an IReadOnlySet<string>, so ForeignRunClassifier's
// signature is unchanged. Its Contains IS the membership test — ForeignRunClassifier.
// ContainsIgnoreCase recognises this type and treats Contains as AUTHORITATIVE, so it must not
// widen a miss back into a case-insensitive scan. A plain HashSet passed by a caller keeps the
// classifier's original "case-insensitive member is LEAVE" behaviour.
// ---------------------------------------------------------------------------

/// <summary>
/// A book's proper-noun set with TWO matching tiers: DECLARED names (from stored analysis) match
/// case-insensitively; MANUSCRIPT-harvested tokens match case-SENSITIVELY. See the header for why.
/// The tiers are kept disjoint — a token in both is held as DECLARED (the looser, safer tier).
/// </summary>
public sealed class BookEntitySet : IReadOnlySet<string>
{
    private readonly HashSet<string> _declared;
    private readonly HashSet<string> _manuscript;

    /// <param name="declaredNames">Authoritative proper nouns from stored analysis — matched case-insensitively.</param>
    /// <param name="manuscriptTokens">Tokens inferred from the prose scan — matched case-sensitively.</param>
    public BookEntitySet(IEnumerable<string> declaredNames, IEnumerable<string> manuscriptTokens)
    {
        _declared = new HashSet<string>(declaredNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _manuscript = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in manuscriptTokens ?? Enumerable.Empty<string>())
        {
            // A token that is ALSO a declared name is already covered by the looser tier; keeping the tiers
            // disjoint makes Count and enumeration duplicate-free.
            if (!string.IsNullOrEmpty(token) && !_declared.Contains(token))
            {
                _manuscript.Add(token);
            }
        }
    }

    /// <summary>The declared (case-insensitive) tier. Exposed for diagnostics/tests.</summary>
    public IReadOnlySet<string> DeclaredNames => _declared;

    /// <summary>The manuscript-harvested (case-sensitive) tier. Exposed for diagnostics/tests.</summary>
    public IReadOnlySet<string> ManuscriptTokens => _manuscript;

    /// <summary>THE membership test: case-insensitive against the declared tier, case-SENSITIVE against the
    /// manuscript tier. A miss here is a real miss — callers must not widen it (see the header).</summary>
    public bool Contains(string item)
        => !string.IsNullOrEmpty(item) && (_declared.Contains(item) || _manuscript.Contains(item));

    public int Count => _declared.Count + _manuscript.Count;

    public IEnumerator<string> GetEnumerator()
    {
        foreach (var name in _declared)
        {
            yield return name;
        }

        foreach (var token in _manuscript)
        {
            yield return token;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Set algebra: nothing in the codebase uses these on an entity set (the classifier only calls Contains).
    // They are implemented over an ORDINAL snapshot of both tiers so the interface is honoured; use
    // Contains for membership — it is the only operation that carries the two-tier semantics.
    public bool IsProperSubsetOf(IEnumerable<string> other) => Snapshot().IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => Snapshot().IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<string> other) => Snapshot().IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => Snapshot().IsSupersetOf(other);
    public bool Overlaps(IEnumerable<string> other) => Snapshot().Overlaps(other);
    public bool SetEquals(IEnumerable<string> other) => Snapshot().SetEquals(other);

    private HashSet<string> Snapshot() => new(this, StringComparer.Ordinal);
}
