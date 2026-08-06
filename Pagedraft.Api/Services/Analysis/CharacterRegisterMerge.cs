using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// CharacterRegisterMerge — THE ONE PLACE d1's merge rule lives.
//
// Plan: src/.cursor/plans/_todo/character-register-editing-2026-08-02.plan.md (§ d1 decision, §3).
//
// A pure, deterministic function: given the register currently persisted for a book (LOCAL, which
// may carry author edits) and a freshly extracted register (INCOMING), produce the register to
// persist. No I/O, no clock of its own (the caller supplies `now`), no logging — so it is fully
// unit-testable and every caller gets identical semantics.
//
// The whole point of provenance is that this function exists. If a second implementation of "merge
// two registers" ever appears, the author-confirmed values it does not know about get clobbered
// silently and undetectably. Route every re-extraction through here.
// ---------------------------------------------------------------------------

/// <summary>
/// d1 §3's merge rule, case for case. See <see cref="Merge"/>.
/// </summary>
public static class CharacterRegisterMerge
{
    /// <summary>
    /// Merge a freshly extracted register over the persisted one, preserving author intent.
    ///
    /// <para><b>Matching key</b> (d1 §3): two entries are the SAME character when their
    /// <see cref="CharacterRegisterEntry.Name"/> values match case-insensitively after trim, OR when
    /// either side's Name appears (case-insensitively) in the other side's
    /// <see cref="CharacterRegisterEntry.Aliases"/>. Alias fallback matters because the extractor is
    /// free to surface a character under a different surface form each run (Danny vs Daniel) and a
    /// name-only match would wrongly treat that as a new character.</para>
    ///
    /// <para><b>Rules</b>:</para>
    /// <list type="bullet">
    /// <item>Matched + field author-confirmed → the LOCAL value wins; the extracted value for that
    /// field is discarded even if it disagrees.</item>
    /// <item>Matched + field extracted-only (flag false, or Role/Description which carry no flag) →
    /// REPLACED with the new extraction's value.</item>
    /// <item>Matched + <see cref="CharacterRegisterEntry.IsAuthorAdded"/> → the entry is left
    /// entirely untouched; none of its fields originated from extraction.</item>
    /// <item>Matched + LOCAL suppressed (<c>IsCharacter=false &amp;&amp; IsCharacterConfirmed=true</c>)
    /// → the incoming entry is DROPPED. A suppressed character is never resurrected.</item>
    /// <item>Incoming only → ADDED with no confirmed flags set (fresh extraction, not yet reviewed).</item>
    /// <item>Local only → KEPT AS-IS, never deleted. The extraction pre-pass reads only the first
    /// ~2000 words of the book, so "the extractor didn't mention them" says nothing about whether the
    /// character exists; deleting here would drop real characters purely for first appearing on page 50.</item>
    /// </list>
    ///
    /// <para><b>Name is identity, not an extracted-only field.</b> A matched entry keeps its LOCAL
    /// Name even though Name carries no provenance flag. Name is the matching key: overwriting it
    /// with the extraction's surface form can orphan the entry from every future match — precisely
    /// the failure d1 §3's last row warns about for renames. Role/Description/Gender/Aliases are the
    /// extracted-only fields that actually get replaced.</para>
    /// </summary>
    /// <param name="local">The persisted register (may be null / empty on a first extraction).</param>
    /// <param name="incoming">The freshly extracted register (may be null / empty).</param>
    /// <param name="now">
    /// The stamp to write to <see cref="CharacterRegister.UpdatedAt"/> IF the merge actually changed
    /// the entry set. When nothing changed the local stamp is preserved, so a no-op re-extraction
    /// cannot make every already-computed AnalysisResult on the book read as stale (d1 §4).
    /// </param>
    public static CharacterRegister Merge(
        CharacterRegister? local,
        CharacterRegister? incoming,
        DateTimeOffset now)
    {
        // Normalize also COLLAPSES duplicate entries (fix-plan c02), on both sides:
        //   local    - a legacy register that already holds a character twice is repaired here, and
        //              the union of both copies' author state is what the merge then preserves. The
        //              collapsed list is also what `merged` starts from, so the write-back is clean.
        //   incoming - one extraction naming a character twice arrives as one entry, so the two
        //              proposals cannot land as two rows.
        // A pure collapse is invisible to the `changed` check below (it compares the NORMALIZED local
        // with the merged result), so repairing a duplicate does not bump UpdatedAt. See Normalize.
        var localEntries = Normalize(local);
        var incomingEntries = Normalize(incoming);

        // Start from the local entries in their existing order: unmatched locals are KEPT, and a
        // matched local is replaced IN PLACE so the register's order stays stable across runs.
        var merged = new List<CharacterRegisterEntry>(localEntries);
        var consumed = new bool[localEntries.Count];

        foreach (var inc in incomingEntries)
        {
            var index = IndexOfMatch(localEntries, consumed, inc);
            if (index >= 0)
            {
                consumed[index] = true;
                var loc = localEntries[index];

                if (IsSuppressed(loc))
                {
                    // Permanent suppression: drop the incoming proposal, keep the suppressed entry.
                    continue;
                }

                merged[index] = MergeMatched(loc, inc);
                continue;
            }

            // No local match. Guard against the SAME character arriving twice in one extraction (or
            // matching an entry this loop already appended): merging it in again would duplicate the
            // character rather than describe it twice.
            //
            // PARTLY (not wholly) redundant since Normalize collapses entries: two incoming entries
            // that match EACH OTHER are already one by the time this loop runs, so the plain
            // same-character-twice case no longer reaches here. What still does is the case Matches is
            // NOT TRANSITIVE for. Incoming {Name:"Dana"} and {Name:"Dani"} do not match each other
            // (different names, no aliases), so Normalize leaves both; a local {Name:"Dana",
            // Aliases:["Dani"]} matches BOTH. The first consumes the local, and the second arrives
            // here with no unconsumed local left. Only this line stops it being appended as a second
            // entry for a character `merged` already holds. Both guards stay.
            if (IndexOfMatch(merged, null, inc) >= 0)
                continue;

            merged.Add(AsFreshlyExtracted(inc));
        }

        var changed = !EntriesEqual(localEntries, merged);
        return new CharacterRegister
        {
            Characters = merged,
            UpdatedAt = changed ? now : local?.UpdatedAt
        };
    }

    /// <summary>
    /// True when the author has explicitly marked this entry as not-a-character. Both halves are
    /// required: <c>IsCharacter=false</c> alone (without the confirmation) is not an author decision
    /// and must not permanently suppress anything.
    /// </summary>
    public static bool IsSuppressed(CharacterRegisterEntry e)
        => !e.IsCharacter && e.IsCharacterConfirmed;

    /// <summary>
    /// d1 §3's matching key. Case-insensitive, trim-insensitive Name equality, OR either side's Name
    /// appearing in the other side's aliases.
    /// </summary>
    public static bool Matches(CharacterRegisterEntry a, CharacterRegisterEntry b)
    {
        if (NameEquals(a.Name, b.Name)) return true;
        if (a.Aliases.Any(alias => NameEquals(alias, b.Name))) return true;
        if (b.Aliases.Any(alias => NameEquals(alias, a.Name))) return true;
        return false;
    }

    /// <summary>Trim + case-insensitive comparison used for every name/alias match.</summary>
    public static bool NameEquals(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Defensive normalization applied to BOTH sides before merging: drops null elements and entries
    /// with a blank name, trims names, de-duplicates aliases (trim + case-insensitive, order
    /// preserved), drops blank aliases, and COLLAPSES entries that are the same character. A persisted
    /// register is arbitrary JSON that a model wrote, so none of that is hypothetical.
    ///
    /// <para><b>Entry collapse</b> (fix-plan c02). Until this shipped, only ALIASES were de-duplicated
    /// and two entries for one character survived side by side. Every register written before
    /// provenance came straight from <c>JsonSerializer.Serialize(extracted)</c> with no de-duplication
    /// at all, so a repeat is reachable on real books. A duplicate broke the surface at both ends: the
    /// client keyed its rows on the name (an Angular NG0955 duplicate-track-key warning on every
    /// change-detection pass, and one Edit click opening the form on both rows), and server-side every
    /// edit resolved through <see cref="Matches"/> landed on the FIRST occurrence, so the second row
    /// could never be corrected while the UI reported the save succeeded.</para>
    ///
    /// <para>The collapse uses <see cref="Matches"/> - the merge's OWN matching key, never a second
    /// "are these the same character" rule. A divergent second rule is exactly how author-confirmed
    /// values get clobbered silently, which is what this file's header forbids.</para>
    ///
    /// <para><b>Per-field collapse rule</b> (survivor = the FIRST occurrence in document order; see
    /// <see cref="CollapseDuplicate"/> for the argument behind each line):</para>
    /// <list type="bullet">
    /// <item><b>Name</b> - the survivor's. Name is identity and the matching key; rewriting it can
    /// orphan the entry from every future match (same reason <see cref="Merge"/> keeps the local name).</item>
    /// <item><b>Aliases</b> - UNION of every collapsed entry's aliases PLUS each dropped entry's own
    /// Name, first-seen order.</item>
    /// <item><b>Gender</b> - the first CONFIRMED value wins; if none is confirmed, the first non-blank.</item>
    /// <item><b>Role / Description</b> - the survivor's, or the first non-blank one. No provenance flag,
    /// no author edit path, so any value beats a null.</item>
    /// <item><b>GenderConfirmed / AliasesConfirmed / IsCharacterConfirmed / IsAuthorAdded</b> - OR.
    /// These are AUTHOR STATE: a flag set on ANY duplicate survives onto the kept entry. Dropping one
    /// would let a later re-extraction overwrite something a human blessed.</item>
    /// <item><b>IsCharacter</b> - SUPPRESSION WINS. If any duplicate is suppressed the collapsed entry
    /// is suppressed; otherwise it is a character if any duplicate was.</item>
    /// </list>
    ///
    /// <para><b>The collapse does NOT bump <see cref="CharacterRegister.UpdatedAt"/>.</b>
    /// <see cref="Merge"/> measures "changed" between the NORMALIZED local and the merged result, so a
    /// pure collapse is invisible to it - deliberately, and consistent with the trimming and alias
    /// de-duplication that already ran here without bumping the stamp. Normalization is repair of a
    /// malformed register, not new content, and treating it as content would mark every prior
    /// AnalysisResult on every legacy book stale the first time anything touched its register.</para>
    /// </summary>
    public static IReadOnlyList<CharacterRegisterEntry> Normalize(CharacterRegister? register)
    {
        if (register is null) return Array.Empty<CharacterRegisterEntry>();

        var result = new List<CharacterRegisterEntry>();
        foreach (var entry in register.Characters)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            var cleaned = entry with
            {
                Name = entry.Name.Trim(),
                Aliases = NormalizeAliases(entry.Aliases, entry.Name)
            };

            // Match against the ACCUMULATED survivor, not the raw earlier entry: an earlier collapse
            // may have widened its alias set, and a third occurrence that only matches through that
            // widened set is still the same character. Matching against the accumulator also makes
            // Normalize idempotent - a second pass over its own output collapses nothing further.
            var survivor = IndexOfMatch(result, null, cleaned);
            if (survivor >= 0)
            {
                result[survivor] = CollapseDuplicate(result[survivor], cleaned);
                continue;
            }

            result.Add(cleaned);
        }

        return result;
    }

    /// <summary>
    /// Trim, drop blanks, drop an alias identical to the entry's own name, and de-duplicate
    /// case-insensitively while preserving first-seen order.
    /// </summary>
    public static IReadOnlyList<string> NormalizeAliases(IEnumerable<string>? aliases, string? ownName)
    {
        if (aliases is null) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in aliases)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var alias = raw.Trim();
            if (NameEquals(alias, ownName)) continue;
            if (!seen.Add(alias)) continue;
            result.Add(alias);
        }

        return result;
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fold a duplicate entry into the survivor that already matched it. NO AUTHOR STATE MAY BE LOST:
    /// every confirmation flag and the author-added marker are OR-ed, so a flag set on any duplicate
    /// survives.
    ///
    /// <para><b>Why the survivor is the FIRST occurrence.</b> Every existing consumer already resolved
    /// a duplicate name to the first one - <c>CharacterRegisterService.ApplyOne</c>'s
    /// <c>FindIndex</c> and this file's <see cref="IndexOfMatch"/> both take the first match. So the
    /// first entry is the one the author has been editing and watching the system act on all along;
    /// collapsing onto it makes the fix a no-op for the row whose behavior was already correct.</para>
    ///
    /// <para><b>CONFLICTING CONFIRMED VALUES: the first occurrence wins</b> (Gender is the only
    /// scalar this can happen to - <c>GenderConfirmed=true</c> on both duplicates with different
    /// values). There is no free right answer here, so the argument for this one:</para>
    /// <list type="number">
    /// <item>"Most recent wins" is not available. Provenance is per FIELD but the only timestamp is
    /// per REGISTER (<see cref="CharacterRegister.UpdatedAt"/>), so nothing in the data says which
    /// confirmation the author made later. Any recency-flavoured rule would be inventing that fact.</item>
    /// <item>First-wins is what the system already behaved as if it held, per the paragraph above, so
    /// it is the choice that changes the fewest observable answers.</item>
    /// <item>It is deterministic and order-stable, which keeps <see cref="Normalize"/> idempotent. A
    /// rule like "male beats unknown" would encode a preference the domain does not have.</item>
    /// </list>
    /// <para>The LOSING value is dropped but the CONFIRMATION is not: <c>GenderConfirmed</c> stays
    /// true, and the author can now re-edit the single surviving row - which is precisely what the
    /// duplicate made impossible. That is the whole reason this is the acceptable loss.</para>
    ///
    /// <para><b>Aliases union rather than first-wins</b>, even against two confirmed lists, because
    /// aliases are a SET and two confirmed lists are not contradictory claims about one value the way
    /// "male" and "female" are: each says "these names refer to this character", and both can be true
    /// at once. Union is therefore the resolution that discards no author statement. The dropped
    /// entry's own NAME joins the union too: it is a surface form the register demonstrably held for
    /// this character, and dropping it would NARROW the matching key for every consumer that resolves a
    /// name through <see cref="Matches"/> - the author's own edits (<c>ApplyOne</c>), and the merge's
    /// matching of the incoming extraction against this entry, both of which run against the collapsed
    /// list. (In the dominant case, two entries with the same name, this is a no-op, since
    /// <see cref="NormalizeAliases"/> drops an alias equal to the entry's own name.)</para>
    ///
    /// <para><b>How far the widened alias set actually survives.</b> Only as far as
    /// <c>AliasesConfirmed</c> carries it. Aliases are an extracted-only field unless the author
    /// confirmed them, so <see cref="MergeMatched"/> REPLACES an unconfirmed alias list with the new
    /// extraction's on the next re-extraction, and a name that only entered through this collapse is
    /// gone again. That is the feature's existing rule, not something the collapse should override:
    /// silently promoting a collapsed name to permanent would be this file inventing author state. So
    /// the union is a guarantee WITHIN the pass that collapses (nothing is lost while the register is
    /// being repaired and edited), not a permanent widening of the key.</para>
    ///
    /// <para><b>SUPPRESSION WINS on IsCharacter.</b> If either duplicate is suppressed the collapsed
    /// entry is suppressed. Suppression is the one decision this feature treats as permanent ("a
    /// suppressed character is never resurrected", see <see cref="Merge"/>): collapsing a suppressed
    /// duplicate into a visible one would RESURRECT a name the author banished, the exact outcome the
    /// merge rule forbids. Collapsing the other way merely hides a name, and the suppressed list stays
    /// rendered with a one-click Restore. Prefer the reversible error over the irreversible one.</para>
    /// </summary>
    private static CharacterRegisterEntry CollapseDuplicate(
        CharacterRegisterEntry keep,
        CharacterRegisterEntry drop)
    {
        var suppressed = IsSuppressed(keep) || IsSuppressed(drop);

        return keep with
        {
            // Name: the survivor's, untouched. It is the matching key.
            Aliases = NormalizeAliases(keep.Aliases.Append(drop.Name).Concat(drop.Aliases), keep.Name),
            Gender = keep.GenderConfirmed ? keep.Gender
                : drop.GenderConfirmed ? drop.Gender
                : string.IsNullOrWhiteSpace(keep.Gender) ? drop.Gender
                : keep.Gender,
            Role = string.IsNullOrWhiteSpace(keep.Role) ? drop.Role : keep.Role,
            Description = string.IsNullOrWhiteSpace(keep.Description) ? drop.Description : keep.Description,
            IsCharacter = !suppressed && (keep.IsCharacter || drop.IsCharacter),
            IsAuthorAdded = keep.IsAuthorAdded || drop.IsAuthorAdded,
            GenderConfirmed = keep.GenderConfirmed || drop.GenderConfirmed,
            AliasesConfirmed = keep.AliasesConfirmed || drop.AliasesConfirmed,
            IsCharacterConfirmed = keep.IsCharacterConfirmed || drop.IsCharacterConfirmed
        };
    }

    /// <summary>
    /// One matched pair. Author-confirmed fields keep the local value; extracted-only fields take the
    /// new extraction's value; the provenance flags and IsAuthorAdded are author state and always
    /// carry forward. An author-added entry short-circuits: nothing on it came from extraction.
    /// </summary>
    private static CharacterRegisterEntry MergeMatched(CharacterRegisterEntry local, CharacterRegisterEntry incoming)
    {
        if (local.IsAuthorAdded) return local;

        return local with
        {
            // Name stays local — it is the matching key, see the Merge doc-comment.
            Gender = local.GenderConfirmed ? local.Gender : incoming.Gender,
            Aliases = local.AliasesConfirmed ? local.Aliases : incoming.Aliases,
            Role = incoming.Role,
            Description = incoming.Description,
            // The extraction proposed this entry, so it considers it a character. An author decision
            // to the contrary is already handled upstream (a suppressed local never reaches here);
            // an author decision that it IS a character is preserved along with its flag.
            IsCharacter = local.IsCharacterConfirmed ? local.IsCharacter : true
        };
    }

    /// <summary>A brand-new character from the extraction: present, unreviewed, no author state.</summary>
    private static CharacterRegisterEntry AsFreshlyExtracted(CharacterRegisterEntry incoming)
        => incoming with
        {
            IsCharacter = true,
            IsAuthorAdded = false,
            GenderConfirmed = false,
            AliasesConfirmed = false,
            IsCharacterConfirmed = false
        };

    /// <summary>
    /// First index in <paramref name="candidates"/> matching <paramref name="incoming"/> and not
    /// already consumed. <paramref name="consumed"/> may be null to search without consumption.
    /// </summary>
    private static int IndexOfMatch(
        IReadOnlyList<CharacterRegisterEntry> candidates,
        bool[]? consumed,
        CharacterRegisterEntry incoming)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (consumed != null && consumed[i]) continue;
            if (Matches(candidates[i], incoming)) return i;
        }

        return -1;
    }

    /// <summary>
    /// Element-wise equality used ONLY to decide whether the merge changed anything (and therefore
    /// whether to bump <see cref="CharacterRegister.UpdatedAt"/>). Record equality alone is not
    /// enough: <c>Aliases</c> is an <c>IReadOnlyList&lt;string&gt;</c> whose default Equals is
    /// reference equality, so two structurally identical entries would compare unequal and every
    /// no-op re-extraction would bump the stamp — making every prior AnalysisResult read as stale.
    /// </summary>
    private static bool EntriesEqual(
        IReadOnlyList<CharacterRegisterEntry> a,
        IReadOnlyList<CharacterRegisterEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(x.Gender, y.Gender, StringComparison.Ordinal)) return false;
            if (!string.Equals(x.Role, y.Role, StringComparison.Ordinal)) return false;
            if (!string.Equals(x.Description, y.Description, StringComparison.Ordinal)) return false;
            if (x.IsCharacter != y.IsCharacter) return false;
            if (x.IsAuthorAdded != y.IsAuthorAdded) return false;
            if (x.GenderConfirmed != y.GenderConfirmed) return false;
            if (x.AliasesConfirmed != y.AliasesConfirmed) return false;
            if (x.IsCharacterConfirmed != y.IsCharacterConfirmed) return false;
            if (!x.Aliases.SequenceEqual(y.Aliases, StringComparer.Ordinal)) return false;
        }

        return true;
    }
}
