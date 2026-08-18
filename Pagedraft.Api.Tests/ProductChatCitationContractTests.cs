using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE ARTIFACT-CITATION CONTRACT (chatbot phase B, f2; g1 findings F-3, F-6, F-10).
///
/// <para>WHAT g1 MEASURED AND WHY NO TEST SAW IT. Of 93 book-scoped runs, <c>artifactRefs</c> came back
/// EMPTY on 74 then 79 - 80-85% - and only ~13 then ~5 named a specific artifact, several of those
/// wrongly. The suite was green throughout, because every citation test asserted that a citation the
/// model DID write parses correctly, and none asserted anything about the instruction that decides
/// whether it writes one. The diagnosis that fell out of the counts: the parser cannot return an empty
/// ref list at all (an unparsed line falls back to the FULL carried set, which is the "everything
/// carried" outcome that fired 6 then 9 times), so 74 empties are 74 lines that PARSED and named only
/// guide ids. The parser worked. The instruction did not: B's "also name the book artifacts" sat in the
/// middle of the message while phase A's tail still closed it with "naming the guide ids you used, and
/// nothing else on that line" - later, narrower, unconditional - and the model resolved the collision
/// toward the tail. That is F-1's shape one clause down, and the fix is the same one: exactly one
/// sentence about the citation line reaches the model.</para>
///
/// <para>THE PARSER STILL GOT TWO NARROW FIXES, for the two shapes that reached the READER. A whole-line
/// citation naming a ref that was never carried used to be left in the prose (the fallback deliberately
/// does not strip on a miss), which published <c>chapter-brief:5</c> for a brief the trim had withheld
/// and a guide anchor that does not exist. Both are shapes no sentence produces, and only those shapes
/// are stripped - an ordinary word after the label is still left exactly where it was, because deleting
/// a line a user might be reading is the worse failure.</para>
///
/// <para>Pure: no model, no GPU, no network, no database.</para>
/// </summary>
public class ProductChatCitationContractTests
{
    private static readonly string[] Carried =
    {
        "export", "faq", "chapter-text:3", "chapter-brief:3", "status:review", "register"
    };

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. THE COLLISION: exactly one sentence about the citation line ─────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FIXTURE THAT WOULD HAVE CAUGHT F-3. Phase A's citation sentence and B's must never both be in
    /// the message: they name different labels, different vocabularies and different scopes, and the
    /// narrower one arrives last. Asserted as a property over the whole composed message rather than as a
    /// string comparison, so it survives a rewording of either sentence.
    /// </summary>
    [Theory]
    [InlineData("en", "naming the guide ids you used", "naming what you actually used")]
    [InlineData("he", "שמציינת את מזהי המדריכים שהשתמשת בהם", "שמציינת את מה שבאמת השתמשת בו")]
    public void TheBookAwareMessage_CarriesExactlyOneCitationSentence(
        string language, string phaseASentence, string bookAwareSentence)
    {
        var bookAware = ProductChatPrompt.SystemMessage(language, bookAware: true);

        Assert.DoesNotContain(phaseASentence, bookAware, StringComparison.Ordinal);
        Assert.Contains(bookAwareSentence, bookAware, StringComparison.Ordinal);

        // VACUITY GUARD: phase A's sentence IS reachable, and is the ONLY one, when no book is in scope.
        var bookless = ProductChatPrompt.SystemMessage(language, bookAware: false);
        Assert.Contains(phaseASentence, bookless, StringComparison.Ordinal);
        Assert.DoesNotContain(bookAwareSentence, bookless, StringComparison.Ordinal);
    }

    /// <summary>
    /// And there is exactly ONE occurrence of the label-shape instruction, counted rather than sampled.
    /// The pre-f2 message contained the phrase "citation line" once and the "End your reply with a line
    /// of the form" template once, in two sentences that disagreed; counting is what makes "one rule
    /// reaches the model" checkable rather than eyeballed.
    /// </summary>
    [Theory]
    [InlineData("en", "line of the form")]
    [InlineData("he", "בשורה בצורה")]
    public void OnlyOneSentence_DescribesTheShapeOfTheCitationLine(string language, string template)
    {
        foreach (var bookAware in new[] { false, true })
        {
            var message = ProductChatPrompt.SystemMessage(language, bookAware);
            var occurrences = Occurrences(message, template);

            Assert.True(occurrences == 1,
                $"expected exactly one citation-line template in the {language} " +
                $"{(bookAware ? "book-aware" : "book-less")} message, found {occurrences}");
        }
    }

    /// <summary>
    /// The book-aware sentence says the three things F-3's wrong citations each got wrong: WHERE the ref
    /// comes from (the artifact's own header, not a guess at a nearby number - g1 cited
    /// <c>chapter-brief:2</c> for an answer taken verbatim from <c>chapter-text:3</c>), that a guide is
    /// named by its ID ALONE (g1 emitted <c>guide-id#a-heading-that-does-not-exist</c>, and guide headings
    /// are this codebase's retrieval index), and that refs live on that line and not in the prose (g1
    /// printed raw finding guids into Hebrew sentences).
    /// </summary>
    [Theory]
    [InlineData("en", "by the ref in its own header", "by its id alone", "not in your sentences")]
    [InlineData("he", "לפי המזהה שבכותרת שלו", "לפי המזהה שלו בלבד", "ולא למשפטים שלך")]
    public void TheBookAwareCitationSentence_ScopesWhereEachRefComesFrom(
        string language, string fromTheHeader, string idAlone, string notInProse)
    {
        var message = ProductChatPrompt.SystemMessage(language, bookAware: true);

        Assert.Contains(fromTheHeader, message, StringComparison.Ordinal);
        Assert.Contains(idAlone, message, StringComparison.Ordinal);
        Assert.Contains(notInProse, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE MESSAGE SAYS WHAT A REF IS, not only where it goes (be-c03, review finding #3).
    ///
    /// <para>OBSERVED live, 1 of 1 book-scoped Hebrew turn: '...כפי שמסומן בקובץ chapter-text:0)' - "as
    /// marked in the FILE chapter-text:0". The place-rule asserted above was already in that same message
    /// and was not obeyed, and nothing downstream removes a mid-sentence ref (an explicit
    /// <c>ProductChatCitations</c> decision: a leaked label is cosmetic, a deleted sentence is not). What
    /// was missing was the token's IDENTITY - that a ref is an internal key the author never sees - which
    /// is why the fix WIDENED the clause that already said exactly that about the bracketed labels instead
    /// of stating the place-rule a second time. A fourth prohibition is the move this prompt has recorded
    /// failing twice.</para>
    ///
    /// <para>Both halves are asserted TOGETHER on purpose: the scoping is an addition to the place-rule
    /// and not a replacement of it, and this file's own F-3 finding is what one rule silently displacing
    /// another looks like from the outside.</para>
    ///
    /// <para>WHAT IT CANNOT ASSERT: whether the model stops quoting refs at the author. That is a rate,
    /// and it is g4's to measure.</para>
    /// </summary>
    [Theory]
    [InlineData("en", "The refs are internal too and the author never sees them either",
                      "not in your sentences")]
    [InlineData("he", "גם המזהים פנימיים והמחבר אינו רואה גם אותם",
                      "ולא למשפטים שלך")]
    public void TheBookAwareMessage_SaysWhatARefIs_AsWellAsWhereItBelongs(
        string language, string whatARefIs, string whereItBelongs)
    {
        var bookAware = ProductChatPrompt.SystemMessage(language, bookAware: true);

        Assert.Contains(whatARefIs, bookAware, StringComparison.Ordinal);
        Assert.Contains(whereItBelongs, bookAware, StringComparison.Ordinal);

        // VACUITY GUARD: phase A carries neither, so this is the book-aware swap and not a fragment that
        // is present in every message regardless.
        var bookless = ProductChatPrompt.SystemMessage(language, bookAware: false);
        Assert.DoesNotContain(whatARefIs, bookless, StringComparison.Ordinal);
        Assert.DoesNotContain(whereItBelongs, bookless, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. EVERY REF THE MODEL MAY CITE IS ONE IT CAN SEE ──────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ROUND TRIP, IN BOTH DIRECTIONS, over a composition that actually trims. "Cite the artifact you
    /// used" is only a thing a model can do without guessing while the two sets agree: every acceptable
    /// ref must be VISIBLE in the composed instruction, and every ref visible in the instruction must be
    /// acceptable. g1's f3 emitted a citation for <c>chapter-brief:5</c> that the trimmer had withheld,
    /// which is the second direction failing, and this is the cheapest permanent statement of both.
    /// </summary>
    [Fact]
    public void TheAcceptableRefs_AndTheRefsVisibleInThePrompt_AreTheSameSet()
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.Status, "status:review"),
            Block(BookArtifactKind.BookBrief, "book-brief"),
            Block(BookArtifactKind.ChapterText, "chapter-text:5"),
            Block(BookArtifactKind.ChapterBrief, "chapter-brief:1", rank: 9),
            Block(BookArtifactKind.ChapterBrief, "chapter-brief:2", rank: 1),
            Block(BookArtifactKind.Finding, "finding:" + Guid.Empty.ToString("D"))
        };

        // A budget tight enough that the cascade really runs, so this is asserted on SURVIVORS.
        var composed = ProductChatBudget.Compose(
            "en", new[] { Guide("export"), Guide("faq") }, Array.Empty<ProductChatTurn>(),
            "What happens in chapter 5?", budgetTokens: 900, blocks, "Salt and Rope");

        Assert.True(composed.Trimmed, "the fixture must exercise the trim, or it pins nothing about drops");

        var artifactRefs = composed.AcceptableReferences
            .Where(BookArtifactRefs.LooksLikeArtifactRef)
            .ToList();

        Assert.NotEmpty(artifactRefs);   // VACUITY GUARD

        foreach (var reference in artifactRefs)
            Assert.Contains($"ref={reference}", composed.Instruction, StringComparison.Ordinal);

        foreach (var dropped in composed.DroppedBookRefs)
        {
            Assert.DoesNotContain($"ref={dropped}", composed.Instruction, StringComparison.Ordinal);
            Assert.DoesNotContain(dropped, composed.AcceptableReferences);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 3. THE "Sources" LABEL, and the phase-A label beside it ────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The label the book-aware prompt now asks for parses, in both languages and both shapes. A book
    /// artifact under a label reading "Guides" is a contradiction the model resolved by listing guides;
    /// "Sources" is what the line is once it can name a chapter or a status.
    /// </summary>
    [Theory]
    [InlineData("Miriam lights the lamp.\nSources: chapter-text:3, export")]
    [InlineData("Miriam lights the lamp.\n**Sources:** chapter-text:3, export")]
    [InlineData("Miriam lights the lamp. (Sources: chapter-text:3, export)")]
    [InlineData("מרים מדליקה את המנורה.\nמקורות: chapter-text:3, export")]
    [InlineData("מרים מדליקה את המנורה. מקורות: chapter-text:3, export")]
    public void TheSourcesLabel_Parses_InBothLanguagesAndBothShapes(string answer)
    {
        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(new[] { "export", "chapter-text:3" }, refs);
        Assert.DoesNotContain("ources:", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("מקורות:", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE PHASE-A LABEL IS STILL ACCEPTED, which is the point of adding a label rather than swapping
    /// one. "Guides:" is the only part of this mechanism g1 measured working (91.7% in phase A), so a
    /// model falling back to it out of habit must not lose its citation.
    /// </summary>
    [Theory]
    [InlineData("Prose.\nGuides: chapter-text:3, export")]
    [InlineData("פרוזה.\nמדריכים: chapter-text:3, export")]
    public void ThePhaseALabel_StillParses_WithArtifactRefs(string answer)
    {
        var (_, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(new[] { "export", "chapter-text:3" }, refs);
    }

    /// <summary>
    /// The Hebrew prefixed forms of the NEW label are prose, exactly as the phase-A pair's are.
    /// "במקורות:" ("in the sources") contains the label as a substring, and only the preceding-character
    /// guard tells them apart. Added with the label, not after a live run finds it.
    /// </summary>
    [Theory]
    [InlineData("הכול מוסבר במקורות: export")]
    [InlineData("כפי שכתוב המקורות: export")]
    [InlineData("That is described in the sources: export")]
    public void APrefixedFormOfTheNewLabel_IsNotACitation(string answer)
    {
        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(answer, prose);
        Assert.Equal(Carried.Length, refs.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 4. FABRICATED CITATIONS STOP REACHING THE READER ───────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// g1's f3, VERBATIM IN SHAPE: a visible Hebrew citation line naming <c>chapter-brief:5</c> for a
    /// brief that was never carried (chapter 5 escalated, so its brief was deliberately withheld). The
    /// citation was refused - correctly, the safety property held - and then LEFT IN THE PROSE, so what
    /// the author read was a source the answer was never given.
    ///
    /// <para>The refs still degrade to the honest full set. Only the line stops being published.</para>
    /// </summary>
    [Theory]
    [InlineData("התקצירים אינם מזכירים צוללת.\nמדריכים: chapter-brief:5", "התקצירים אינם מזכירים צוללת.")]
    [InlineData("The briefs do not mention it.\nSources: chapter-brief:5", "The briefs do not mention it.")]
    [InlineData("The briefs do not mention it.\nSources: status:summary", "The briefs do not mention it.")]
    public void AWholeLineCitation_NamingAnUncarriedArtifactRef_IsNotPublished(
        string answer, string expectedProse)
    {
        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(expectedProse, prose);
        Assert.Equal(Carried, refs);   // the honest fallback, unchanged
    }

    /// <summary>
    /// g1's F-6 second half: an INVENTED GUIDE ANCHOR in the visible citation line
    /// (<c>whole-book-review#a-heading-that-does-not-exist</c>). Not merely cosmetic in this codebase -
    /// <c>GuideSelector</c> scores H1/H2 headings at weight 3.0 and reads no body prose, so a fabricated
    /// anchor points at a retrieval key. The prompt now says a guide is named by its id alone; this is the
    /// belt, for when it is not obeyed.
    /// </summary>
    [Fact]
    public void AWholeLineCitation_NamingAnInventedGuideAnchor_IsNotPublished()
    {
        const string answer =
            "הסקירה אינה מעודכנת.\nמדריכים: whole-book-review#למה_עריכה_התפתחותית_הופכת_ללא_עדכנית";

        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal("הסקירה אינה מעודכנת.", prose);
        Assert.Equal(Carried, refs);
    }

    /// <summary>
    /// AND THE NARROWNESS IS THE POINT. A whole-line label followed by an ordinary WORD is still left
    /// exactly where it was, because the only rule strong enough to find scaffolding by meaning is also
    /// strong enough to delete a sentence someone is reading. The two tests above fire on SHAPES that no
    /// sentence in either language produces; these do not have one.
    /// </summary>
    [Theory]
    [InlineData("Some prose.\nGuides: nonexistent-guide")]
    [InlineData("Some prose.\nGuides: none of them cover this")]
    [InlineData("טקסט.\nמדריכים: אין מדריך שמכסה את זה")]
    public void AWholeLineCitation_NamingSomethingThatIsNotARefShape_IsStillLeftAlone(string answer)
    {
        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(answer, prose);
        Assert.Equal(Carried, refs);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 5. THE CITATION LINE THE MODEL KEPT WRITING PAST ───────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// g1 F-10: the parser reads the LAST line, so a model that emits its citation line and then adds
    /// another paragraph strands the line mid-answer, where the reader has to skip it and the response
    /// falls back to the full set. Position proves nothing there, so the SHAPE has to, and the bar is the
    /// strictest in the class: the line must be the label and NOTHING but refs this turn actually carried.
    /// </summary>
    [Fact]
    public void AStrandedCitationLine_IsRemoved_AndItsRefsAreUsed()
    {
        const string answer =
            "The briefs cover chapters 0 to 2.\n" +
            "Sources: chapter-brief:3, export\n" +
            "Let me know if you want the full text of one of them.";

        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(new[] { "export", "chapter-brief:3" }, refs);
        Assert.Equal(
            "The briefs cover chapters 0 to 2.\n" +
            "Let me know if you want the full text of one of them.",
            prose);
    }

    /// <summary>
    /// A stranded line carrying ANY token that is not a carried ref is left alone, whole. This is the
    /// guard that keeps the scan above from reaching into prose: it is the same Guard C the inline shape
    /// uses, reused rather than re-tuned, so the tolerance cannot drift between the two.
    /// </summary>
    [Theory]
    [InlineData("Prose.\nGuides: export is the one that covers this.\nMore prose.")]
    [InlineData("Prose.\nSources: export, epub-export\nMore prose.")]
    public void AStrandedLineThatIsNotPurelyRefs_IsLeftAlone(string answer)
    {
        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(answer, prose);
        Assert.Equal(Carried, refs);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 6. THE SAFETY PROPERTY, SWEPT ACROSS EVERYTHING ABOVE ──────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NOTHING f2 ADDED CAN WIDEN A CITATION. Every shape in this file, plus the fabricated ones, run
    /// through the one property that must survive all of it: the returned refs are always a subset of
    /// what this turn actually carried. g1 confirmed this held before; the two new strip paths and the
    /// two new labels are exactly the kind of change that could quietly break it.
    /// </summary>
    [Fact]
    public void NoAcceptedCitation_EverNamesSomethingThatWasNotCarried()
    {
        var shapes = new[]
        {
            "Prose.\nSources: chapter-text:3, export",
            "Prose. (Sources: chapter-text:3)",
            "Prose.\nמקורות: register",
            "Prose.\nSources: chapter-brief:5",
            "Prose.\nGuides: whole-book-review#invented",
            "Prose.\nSources: chapter-text:3, export\nStill talking.",
            "Prose.\nGuides: nonexistent-guide",
            "Prose.\nSources: ,",
            "Prose with no citation at all."
        };

        Assert.NotEmpty(shapes);   // VACUITY GUARD

        foreach (var shape in shapes)
        {
            var (_, refs) = ProductChatCitations.Extract(shape, Carried);

            Assert.All(refs, reference => Assert.Contains(reference, Carried));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 7. AN ANSWER WITH NO CITATION LINE AT ALL (g2, the General route) ──────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN ANSWER THAT NEVER CITES ANYTHING SURVIVES INTACT. g2's General route asks for NO citation line -
    /// an answer out of Show's own knowledge has no guide to name - so what was a rare parse miss becomes
    /// the normal shape of a whole route's traffic, and the parser's behaviour on it stops being an edge
    /// case. Two properties, and they are different facts: the PROSE comes back untouched (the parser must
    /// not decide that the last sentence of an uncited answer was scaffolding), and the refs fall back to
    /// the full carried set, which is the deliberate fail-safe direction - "here is what this answer was
    /// grounded in" rather than a wrong citation.
    ///
    /// <para>Both languages, and the multi-line case, because the parser reads the LAST non-blank line and
    /// a one-line fixture would not exercise that walk.</para>
    /// </summary>
    [Theory]
    [InlineData("Export runs from the book menu and produces a DOCX.")]
    [InlineData("Export runs from the book menu.\nIt produces a DOCX.\n")]
    [InlineData("הייצוא מופעל מתפריט הספר ומפיק קובץ DOCX.")]
    [InlineData("הייצוא מופעל מתפריט הספר.\nהוא מפיק קובץ DOCX.\n")]
    public void AnAnswerWithNoCitationLine_KeepsItsProse_AndFallsBackToTheCarriedSet(string answer)
    {
        var (prose, refs) = ProductChatCitations.Extract(answer, Carried);

        Assert.Equal(answer, prose);
        Assert.Equal(Carried, refs);
    }

    /// <summary>
    /// AND WITH NOTHING LICENSED, IT CITES NOTHING - which is the state <c>ProductChatService</c> puts the
    /// General route in on purpose. The fallback returns the acceptable set, so handing the parser the
    /// surviving guides there would decorate every general answer with chips for guides it never used;
    /// handing it an empty set makes "this answer cites nothing" the outcome rather than a hope about what
    /// the model wrote. The prose still has to survive, which is the half that could silently break.
    /// </summary>
    [Theory]
    [InlineData("Dialogue reads faster when the beats carry the blocking.")]
    [InlineData("דיאלוג נקרא מהר יותר כשהפעולות נושאות את התנועה.")]
    public void WithNoAcceptableReferences_TheAnswerCitesNothing_AndKeepsItsProse(string answer)
    {
        var (prose, refs) = ProductChatCitations.Extract(answer, Array.Empty<string>());

        Assert.Equal(answer, prose);
        Assert.Empty(refs);

        // VACUITY GUARD: the same call WITH a carried set does return something, so the emptiness above is
        // the licensing and not a parser that returns nothing for every input.
        var (_, carriedRefs) = ProductChatCitations.Extract(answer, Carried);
        Assert.NotEmpty(carriedRefs);
    }

    /// <summary>
    /// A MODEL THAT WRITES A CITATION LINE ANYWAY, ON A TURN THAT LICENSES NONE, CANNOT MANUFACTURE ONE.
    /// This is the safety property of the file's section 6 applied to g2's empty-set case, and it is the
    /// direction that matters: the General route's whole point is that Show stops attributing an answer to
    /// a source, so a habitual "Guides: faq" must not become a chip.
    /// </summary>
    [Fact]
    public void WithNoAcceptableReferences_AHabitualCitationLine_NamesNothing()
    {
        var (_, refs) = ProductChatCitations.Extract(
            "Dialogue reads faster when the beats carry the blocking.\nGuides: faq",
            Array.Empty<string>());

        Assert.Empty(refs);
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static GuideDocument Guide(string id = "export", string lang = "en")
        => new(id, "stage", "author", "2026-01-01", lang, $"50-{id}.{lang}.md", 50,
               new[] { "# Export" }, $"Body of {id}.");

    private static BookArtifactBlock Block(
        BookArtifactKind kind, string reference, string text = "artifact text", double rank = 0)
        => new(kind, new[] { reference }, $"=== ARTIFACT ref={reference} ===\n{text}", rank);
}
