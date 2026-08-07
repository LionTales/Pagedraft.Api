using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE INLINE CITATION SHAPE THAT LEAKED (chatbot phase A, g1 finding F1).
///
/// <para>WHAT WENT WRONG LIVE. The prompt asks the model to end with a <c>Guides: &lt;id&gt;</c> line
/// and nothing else on it. In 3 of g1's 72 measured answers the model put that label at the END OF A
/// PROSE LINE instead, and the parser only looked at the START of the last line. Two things broke at
/// once: the raw label stayed in the text the user reads, and because nothing parsed, the response
/// fell back to the FULL four-guide selection. The rendered citation chips then contradicted the
/// sentence directly above them, and overstated what the answer was actually built from.</para>
///
/// <para>WHY THIS FILE IS MOSTLY REJECTIONS. Accepting an inline citation is easy; accepting it
/// without eating prose that merely mentions a guide is the actual problem, and a fix that stripped
/// too much would delete a sentence the user was reading - strictly worse than the leak it replaced.
/// So the accepted and rejected shapes are pinned side by side, and the last test sweeps EVERY shape
/// in this file through the one property that must survive all of it: a citation can only NARROW the
/// selection, never widen it.</para>
///
/// <para>Pure: no model, no corpus, no GPU.</para>
/// </summary>
public class ProductChatInlineCitationTests
{
    private static readonly IReadOnlyList<GuideDocument> Selection = new[]
    {
        Guide("export"), Guide("faq"), Guide("import"), Guide("chapter-editing-passes")
    };

    /// <summary>
    /// The real selector shape behind g2's leaked answer: the last guide was retrieved as an en/he TWIN
    /// PAIR, two documents sharing ONE id, which is exactly why the model reached for a language tag.
    /// </summary>
    private static readonly IReadOnlyList<GuideDocument> TwinSelection = new[]
    {
        Guide("export"), Guide("faq"), Guide("book-setup-and-intelligence"),
        Guide("chapter-editing-passes", "en"), Guide("chapter-editing-passes", "he")
    };

    // ─── ACCEPTED: a citation that trails prose on the same line ────────────────────────────────

    /// <summary>
    /// THE EXACT SHAPE FROM g1 d6 run3, Hebrew, label and prose on one line. The label must leave the
    /// answer and the citation must narrow to the one guide the model named.
    /// </summary>
    [Fact]
    public void TheHebrewInlineCitationThatLeaked_IsParsed_AndStrippedFromTheProse()
    {
        const string answer =
            "מענה על שאלות לגבי ספר מסוים עדיין אינו זמין. אפשר לשאול שאלות כלליות על המוצר. " +
            "מדריכים: chapter-editing-passes";

        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(new[] { "chapter-editing-passes" }, ids);
        Assert.DoesNotContain("מדריכים:", prose, StringComparison.Ordinal);
        Assert.EndsWith("אפשר לשאול שאלות כלליות על המוצר.", prose, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnglishInlineCitation_AtTheEndOfAProseLine_IsParsed()
    {
        var (prose, ids) = ProductChatCitations.Extract(
            "Export produces a DOCX file from what is saved in your chapters. Guides: export", Selection);

        Assert.Equal(new[] { "export" }, ids);
        Assert.Equal("Export produces a DOCX file from what is saved in your chapters.", prose);
    }

    /// <summary>The parenthesised, comma-separated variant g1 also saw. The opening bracket goes with
    /// the citation it opened; the sentence's own full stop stays.</summary>
    [Fact]
    public void AParenthesisedInlineCitation_IsParsed_AndItsOpeningBracketGoesWithIt()
    {
        var (prose, ids) = ProductChatCitations.Extract(
            "Proofread and Linguistic can use the thinking tier. (Guides: faq, chapter-editing-passes)", Selection);

        Assert.Equal(new[] { "faq", "chapter-editing-passes" }, ids);   // selection order
        Assert.Equal("Proofread and Linguistic can use the thinking tier.", prose);
    }

    [Fact]
    public void AnInlineCitationAfterAMultiLineAnswer_LeavesTheEarlierLinesIntact()
    {
        var (prose, ids) = ProductChatCitations.Extract(
            "First paragraph.\n\nSecond paragraph. **Guides:** import", Selection);

        Assert.Equal(new[] { "import" }, ids);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", prose);
    }

    // ─── ACCEPTED: an id disambiguated by a parenthesised language tag (g2 finding G3 item 1) ────

    /// <summary>
    /// THE EXACT ENDING g2's 102-run measurement recorded THREE TIMES VERBATIM. An en/he twin pair
    /// shares one guide id, so a model citing both members has no id-level way to say so and appends a
    /// language tag. The id-only Guard C tokenised "(en)" into "en", found no such guide, and abandoned
    /// the WHOLE match - leaking the raw label into the prose AND falling back to the full selection, so
    /// the rendered chips contradicted the sentence directly above them. 5 of g2's 102 answers carried a
    /// raw label this way.
    ///
    /// <para>The tag is scaffolding, not provenance: it must never itself become a cited id.</para>
    ///
    /// <para>Revert-verified: with the id-only Guard C restored (// TEMP-REVERT), this failed on
    /// "Assert.Equal() Failure: Collections differ ... Actual: ["export", "faq",
    /// "book-setup-and-intelligence", "chapter-editing-passes"]" - the widened fallback naming a guide
    /// the answer never cited - and, with the assertions reordered, on "Assert.DoesNotContain() Failure:
    /// Sub-string found ... Found: "Guides:"", the raw label left in the user's prose.</para>
    /// </summary>
    [Fact]
    public void AnInlineCitationTaggingAnEnHeTwinPair_IsParsed_AndTheTagIsNotCited()
    {
        const string answer =
            "You can start a run from the chapter toolbar and pressing Run analysis. " +
            "Guides: faq, book-setup-and-intelligence, chapter-editing-passes (en), chapter-editing-passes (he)";

        var (prose, ids) = ProductChatCitations.Extract(answer, TwinSelection);

        // Narrowed to the ids actually named, in SELECTION order, with the twin pair listed once.
        Assert.Equal(new[] { "faq", "book-setup-and-intelligence", "chapter-editing-passes" }, ids);

        // The language tags are scaffolding and never provenance.
        Assert.DoesNotContain("en", ids, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("he", ids, StringComparer.OrdinalIgnoreCase);

        // And the label is gone from the text the user reads.
        Assert.DoesNotContain("Guides:", prose, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("You can start a run from the chapter toolbar and pressing Run analysis.", prose);
    }

    /// <summary>
    /// THE TOLERANCE IS A SHAPE, NOT A CLASS. "(xx)" is accepted because two letters in brackets cannot
    /// be a sentence; anything else in brackets can be, and accepting it would hand Guard C back the
    /// greediness that lets a citation eat the prose the user came for. Both of these must still be
    /// refused WHOLE: text untouched, full selection returned.
    ///
    /// <para>Revert-verified: with IsLanguageTag widened to "anything that touches a bracket"
    /// (// TEMP-REVERT), all three cases failed on "Assert.Equal() Failure: Strings differ / Expected:
    /// "Here is the answer. Guides: faq (guide)" / Actual: "Here is the answer."" - the loosened
    /// tolerance deleting the fragment the user was reading.</para>
    /// </summary>
    [Theory]
    [InlineData("Here is the answer. Guides: faq (section 3)")]
    [InlineData("Here is the answer. Guides: faq (guide)")]
    [InlineData("Here is the answer. Guides: faq (english)")]
    public void AParenthesisedTokenThatIsNotATwoLetterLanguageTag_IsStillRefusedWhole(string answer)
    {
        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(new[] { "export", "faq", "import", "chapter-editing-passes" }, ids);
    }

    /// <summary>
    /// A tag names no guide, so a citation made of nothing but tags cites nothing. It must not strip the
    /// label on the strength of scaffolding alone, and must not return an empty citation.
    ///
    /// <para>DEFENCE IN DEPTH, and honestly labelled as such: removing the cited-is-empty guard
    /// (// TEMP-REVERT) left this GREEN, because Extract's empty-intersection fallback catches it a
    /// layer later. No single-line mutation turns it red, so it pins the layering rather than one
    /// guard - both layers would have to go for a tag to reach the user as provenance.</para>
    /// </summary>
    [Fact]
    public void AnInlineCitationOfNothingButLanguageTags_IsRefused()
    {
        const string answer = "Here is the answer. Guides: (en), (he)";

        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(4, ids.Count);
    }

    /// <summary>
    /// The tolerance must not have opened a hole in the mixed-citation refusal: a tagged HALLUCINATED id
    /// is still a hallucinated id, and the whole match is still abandoned. (The untagged form of this is
    /// pinned separately below; this is the same rule reached through the new branch.)
    ///
    /// <para>Revert-verified: with Guard C's refusal turned into a half-trusting "skip the token I do
    /// not recognise" (// TEMP-REVERT), this failed on "Assert.Equal() Failure: Strings differ /
    /// Expected: ···"annot do that today. Guides: export (en)," / Actual: "You cannot do that today.""
    /// - the citation accepted, and narrowed, on the strength of a fabricated guide.</para>
    /// </summary>
    [Fact]
    public void ATaggedCitationNamingAGuideThatWasNotSelected_IsStillRefusedEntirely()
    {
        const string answer = "You cannot do that today. Guides: export (en), epub-export (he)";

        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(4, ids.Count);
    }

    // ─── REJECTED: prose that merely mentions a guide ───────────────────────────────────────────

    /// <summary>
    /// THE ANTI-GREEDY CASE. "Guides:" followed by a SENTENCE is prose, not a citation list, and a
    /// parser loose enough to accept it would delete the sentence the user came for. Rejecting costs
    /// only the pre-F1 behaviour: the text is left alone and the full selection is returned.
    /// </summary>
    [Fact]
    public void ALabelFollowedByASentence_IsNotACitation_AndNothingIsStripped()
    {
        const string answer = "Here is what I found. Guides: export is the only one that covers this.";

        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(new[] { "export", "faq", "import", "chapter-editing-passes" }, ids);
    }

    /// <summary>
    /// THE ENGLISH FALSE POSITIVE THIS RULE EXISTS TO REFUSE: "in the guides:" reads exactly like the
    /// label unless you look at what precedes it. A label glued to a word is prose.
    /// </summary>
    [Fact]
    public void TheWordGuidesInsideASentence_IsNotACitation()
    {
        const string answer = "That is described in the guides: import";

        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(4, ids.Count);
    }

    /// <summary>
    /// THE HEBREW FALSE POSITIVE, which is the same trap and is easier to fall into: Hebrew prefixes
    /// attach directly to the noun, so "במדריכים:" ("in the guides:") CONTAINS the label
    /// "מדריכים:" as a substring. Only the preceding-character rule tells them apart.
    /// </summary>
    [Theory]
    [InlineData("הכול מוסבר במדריכים: export")]
    [InlineData("כפי שכתוב המדריכים: export")]
    public void AHebrewPrefixedFormOfTheLabel_IsNotACitation(string answer)
    {
        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(4, ids.Count);
    }

    /// <summary>
    /// A citation mixing a real id with one the model invented is refused WHOLE rather than
    /// half-trusted. Half-accepting would report a narrowed provenance for an answer whose own
    /// citation was partly fiction.
    /// </summary>
    [Fact]
    public void AnInlineCitationNamingAGuideThatWasNotSelected_IsRefusedEntirely()
    {
        const string answer = "You cannot do that today. Guides: export, epub-export";

        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(4, ids.Count);
    }

    /// <summary>
    /// THE SHAPE BOUND. Even when every token would parse, an inline tail longer than
    /// <see cref="ProductChatCitations.MaxInlineCitationChars"/> is refused, so a mis-parse can never
    /// swallow a paragraph of the answer.
    /// </summary>
    [Fact]
    public void AnInlineTailLongerThanTheCap_IsRefused()
    {
        var padding = string.Join(" ", Enumerable.Repeat("export", 40));   // all valid ids, far too long
        Assert.True(padding.Length > ProductChatCitations.MaxInlineCitationChars);

        var answer = "Prose. Guides: " + padding;

        var (prose, ids) = ProductChatCitations.Extract(answer, Selection);

        Assert.Equal(answer, prose);
        Assert.Equal(4, ids.Count);
    }

    // ─── The whole-line shape keeps its old, LENIENT behaviour ──────────────────────────────────

    /// <summary>
    /// A label on a line of ITS OWN is what the prompt asked for, and its position is the evidence, so
    /// the tail is still parsed leniently: stray words around the ids do not void it. Pinned because
    /// tightening the inline rule must not tighten this one by accident - that would turn g1's 91.7%
    /// parse rate into a regression.
    /// </summary>
    [Fact]
    public void AWholeLineCitation_StillToleratesStrayWordsAroundTheIds()
    {
        var (prose, ids) = ProductChatCitations.Extract("Some prose.\nGuides: export and import", Selection);

        Assert.Equal(new[] { "export", "import" }, ids);
        Assert.Equal("Some prose.", prose);
    }

    // ─── The safety property, swept over every shape above ──────────────────────────────────────

    /// <summary>
    /// A CITATION CAN ONLY NARROW THE SELECTION, NEVER WIDEN IT - the property F1's fix must not have
    /// weakened. Swept over every shape this file exercises plus the widening attempts, ACCEPTED or
    /// REFUSED, because the guarantee has to hold on both branches of the new parse.
    ///
    /// <para>The population is asserted non-empty first: a sweep over an empty list of shapes reads
    /// exactly like a thorough check and proves nothing.</para>
    /// </summary>
    [Fact]
    public void NoAnswerShape_CanWidenTheCitationBeyondTheSelection()
    {
        var shapes = new[]
        {
            "Prose. Guides: export",
            "Prose. Guides: export, whole-book-review",
            "Prose. Guides: whole-book-review",
            "Prose.\nGuides: whole-book-review, workflow-overview",
            "Prose.\nGuides: export, whole-book-review",
            "מענה. מדריכים: chapter-editing-passes",
            "מענה. מדריכים: book-setup-and-intelligence",
            "Prose with no citation at all.",
            "Guides:",
            "Prose. Guides:",
            "Prose. (Guides: faq, chapter-editing-passes)",
            "That is described in the guides: import",
            // The language-tag branch, accepted and refused alike.
            "Prose. Guides: chapter-editing-passes (en), chapter-editing-passes (he)",
            "Prose. Guides: export (en), whole-book-review (he)",
            "Prose. Guides: (en), (he)",
            "Prose. Guides: faq (section 3)"
        };

        Assert.Equal(16, shapes.Length);          // the population, before the sweep
        var selectedIds = Selection.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var shape in shapes)
        {
            var (_, ids) = ProductChatCitations.Extract(shape, Selection);

            Assert.NotEmpty(ids);                 // a turn always reports what it was grounded in
            Assert.All(ids, id => Assert.Contains(id, selectedIds));
            Assert.Equal(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), ids.Count);
        }

        // The same property on the TWIN selection, where a shared id is what makes the tag appear:
        // a tolerated token must never arrive as an id, and a twin pair must never be cited twice.
        var twinIds = TwinSelection.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var twinShapes = new[]
        {
            "Prose. Guides: chapter-editing-passes (en), chapter-editing-passes (he)",
            "Prose. Guides: faq, book-setup-and-intelligence, chapter-editing-passes (en), chapter-editing-passes (he)",
            "Prose. Guides: chapter-editing-passes (en), whole-book-review (he)"
        };

        Assert.Equal(3, twinShapes.Length);
        foreach (var shape in twinShapes)
        {
            var (_, ids) = ProductChatCitations.Extract(shape, TwinSelection);

            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.Contains(id, twinIds));
            Assert.Equal(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), ids.Count);
        }
    }

    /// <summary>
    /// An answer that is NOTHING BUT a citation still returns its prose rather than an empty bubble.
    /// The user must never be handed a blank answer because the parser was successful.
    /// </summary>
    [Fact]
    public void AnAnswerThatIsOnlyACitation_IsNotStrippedToNothing()
    {
        var (prose, ids) = ProductChatCitations.Extract("Guides: export", Selection);

        Assert.NotEmpty(prose);
        Assert.Equal(new[] { "export" }, ids);
    }

    private static GuideDocument Guide(string id, string lang = "en")
        => new(id, id, "author", "2026-08-02", lang, $"{id}.md", 10, Array.Empty<string>(), "body");
}
