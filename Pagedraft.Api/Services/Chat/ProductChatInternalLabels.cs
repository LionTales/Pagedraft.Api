using System.Text.RegularExpressions;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE removal of the prompt's own INTERNAL tokens from the answer prose the author reads
/// (chatbot phase B, review finding final-r03).
///
/// <para>WHAT LEAKS, AND WHAT THE MEASUREMENT SAYS ABOUT WHY. The BOOK section labels each chapter's raw
/// text for the model - <c>[CHAPTER 7, whole chapter]</c> vs <c>[CHAPTER 7 EXCERPT, not the whole
/// chapter]</c> - and heads every block with a wire ref (<c>chapter-text:7</c>, <c>finding:&lt;guid&gt;</c>,
/// <c>status:review</c>). None of that is author-facing. The model quotes it anyway: <c>g4</c> measured a
/// bracketed label in the prose in 3 of 38 book-scoped runs and a ref in 5 of 38, and <c>final-r03</c>'s
/// own 32-call attribution run measured the surviving shapes at 4 of 16 and 1 of 16.</para>
///
/// <para>THIS IS A RENDERING FIX BECAUSE THE PROMPT FIX WAS TRIED AND ATTRIBUTED. The grounding clause has
/// been edited three times over this class. <c>final-r03</c>'s attribution run put the clause's
/// <c>be-c02</c> sentence on one arm and HEAD on the other, 16 runs each, same instrument and same loaded
/// model: the arm WITHOUT the sentence leaked internal labels at 4 of 16 against HEAD's 1 of 16
/// (Fisher p=0.333). The sentence did not create the class. What it did create was one EXEMPLAR - a
/// literal <c>[CHAPTER 0]</c> quoted inside it - and deleting that exemplar took the bracketed shape from
/// 3 of 38 to 0 of 146 across THREE subsequent live runs (<c>final-r04</c>'s 45, <c>final-r06</c>'s 69
/// and <c>final-r03</c>'s own 32; counted 2026-08-14). So the exemplar is gone and stays gone (see
/// <c>ProductChatPrompt</c>: no literal bracketed label may be re-introduced into either grounding
/// string), and what is left is a residue that a fourth prohibition would not close and could only
/// re-teach, since a prohibition has to name the token it forbids.</para>
///
/// <para>EVERY MEASURED LEAK IS A PARENTHETICAL GLOSS, and that is the shape this strips. In all five the
/// internal token is appended in brackets or backticks as a gloss on the Hebrew clause it hangs off -
/// redundant with it in three of them, subordinate to it in the other two (see DECISION 2's HONEST LIMIT,
/// where leak 1's parenthetical is the only place the answer names an identifier at all): <c>הטקסט שמופיע כאן הוא רק חלק ממנו (EXCERPT).</c>,
/// <c>אלא רק חלקים מהם (EXCERPT), ולכן...</c>, <c>...שמעכב את ההתקדמות בעלילה (מצא פתיחה:
/// finding:4c8dd0c9-...)</c>. Removing the gloss leaves the sentence intact and grammatical, which is what
/// separates this from stripping the model's reasoning: the words <c>whole chapter</c> and <c>excerpt</c>
/// are ones the grounding clause deliberately tells the model to reason WITH, so a bare use of them in
/// running prose is untouched here and only the redundant parenthetical copy goes (see below).</para>
///
/// <para>THREE SHAPES AND NO MORE, because the first version of this class generalized past its evidence
/// (review finding final-r03, findings A1/A2/A4/A5/A6/A19). What it removes is (1) a bracketed internal
/// label, (2) a bracket group whose content is internal tokens plus at most <see cref="MaxResidueWords"/>
/// connective words, removed whole with its brackets, and (3) a bare slug. Every character deleted from
/// every one of the five measured leaks is deleted by shape 2 alone; shapes 1 and 3 carry <c>g4</c>'s older
/// measurement and the emitter's own literal output. THREE PASSES WERE DELETED rather than guarded, each
/// for want of any measurement behind it, and each is recorded in the plan's Investigation findings so it
/// is not re-derived as a missing feature: a pass that deleted an emphasis- or backtick-wrapped word out of
/// running prose (it ate <c>**excerpt**</c>, <c>`register`</c> and <c>**history**</c>, which are the
/// product's OWN feature names); the individual removal of a label fragment out of a bracket that still
/// held a readable clause (<c>(an excerpt of chapter 8)</c> became <c>(an of chapter 8)</c>); and a
/// LINE-WIDE unmatched-bracket drop, which reached brackets the strip had not orphaned and could not
/// orphan. EVERY RULE HERE IS SCOPED TO A REMOVAL'S OWN NEIGHBOURHOOD, never to the line: line scope was
/// the structural cause of the THIRD of those, and of the seam repair that A6 rewrote rather than deleted.
/// The second had a different cause, token scope inside a bracket the strip had decided to keep, and a
/// different fix (the <c>GroupOnly</c> flag in <see cref="Tokens"/>).</para>
///
/// <para>DECISION 1 - A LABEL THE MODEL QUOTED WRONG. <c>g4</c> observed <c>בפרק 7 (כפי שמצויין בכותרת
/// [CHAPTER 6])</c>, where order 7's label is <c>[CHAPTER 7]</c>, so the parenthetical was FALSE. The
/// parenthetical is always redundant with, or subordinate to, the clause it hangs off - true of all five
/// measured leaks - so removing it deletes a WRONG statement and leaves the sentence's own, correct claim
/// ("in chapter 7") standing. Shipping both was strictly worse. This is the one case where the strip
/// improves accuracy rather than only tidiness, and it is why the strip does NOT try to correct a
/// mis-quoted label: correcting it would mean asserting a number on the model's behalf, and the number
/// the sentence already carries is the one the author asked about.</para>
///
/// <para>DECISION 2 - WHAT GOES WITH THE TOKEN. If a parenthetical's content is nothing but internal
/// tokens plus at most two residual words, the WHOLE parenthetical goes, brackets included, and the
/// doubled space or stranded comma it leaves behind is tidied. Two words is the bound and not zero
/// because the measured glosses carry a connective that introduced the token and means nothing without it
/// - <c>(למשל `chapter-text:X` או `EXCERPT`)</c> ("for example ... or ...") and <c>(מצא פתיחה:
/// finding:...)</c> ("found opening: ...") - while three or more words is a clause the author can read,
/// so <c>(כפי שמצויין בכותרת [CHAPTER 6])</c> keeps its words and loses only the label. A three-word
/// residue that reads oddly without its object is accepted: keeping a clause is the safe direction, and
/// it is the same asymmetry <c>ProductChatCitations</c> holds ("A leaked label mid-answer is cosmetic; a
/// deleted sentence is not").</para>
///
/// <para>DECISION 2, THE HONEST LIMIT. The first measured leak, <c>אנא ציין את המזהה שלו (למשל
/// `chapter-text:X` או `EXCERPT`) ואשמח לעדכן אותך</c>, becomes <c>אנא ציין את המזהה שלו ואשמח לעדכן
/// אותך</c> - "please state its identifier and I will be glad to update you". That is HARMLESS BUT ODD,
/// not correct: the sentence still asks the author for an identifier they have no way to give, because
/// the identifier was never theirs. Curing it means stopping the model from offering the transaction at
/// all, which is a prompt change, and this class exists precisely because the prompt has been the wrong
/// lever here three times. Recorded rather than hidden.</para>
///
/// <para>DECISION 3 - RTL, AND THE BRACKET RULE THAT REPLACED A WORSE ONE. Every measured leak is an LTR
/// slug inside Hebrew prose, and this program has twice recorded such a fragment dragging its closing
/// punctuation to the wrong end (review finding #3's <c>chapter-text:0 ),</c> and <c>g5</c>'s Latin chapter
/// frame). BRACKETS ARE THEREFORE ONLY EVER REMOVED IN PAIRS - a whole group, or a <c>[...]</c> label, or
/// nothing - which means this strip CANNOT ORPHAN A BRACKET, and needs no rule for cleaning up after
/// itself. It used to have one, dropping every unmatched bracket on any line it touched, and that rule was
/// deleted: re-reading its own cited evidence dissolved it, because <c>chapter-text:0 ),</c> is the RTL
/// RENDERING of a BALANCED <c>(chapter-text:0),</c>, which shape 2 removes whole. What the line-wide rule
/// actually reached was brackets the MODEL wrote and this strip never touched - the far half of a pair that
/// spans a newline, and the eye of a <c>:-(</c> at the other end of the sentence - so it manufactured the
/// very malformation it was written to prevent. Pinned by tests that assert on the real Hebrew strings from
/// the run artifacts, not on transliterations.</para>
///
/// <para>DECISION 5 - WHAT A REMOVAL TAKES WITH IT, which is delimiters and whitespace and never a word. A
/// slug inside a wrapper whose whole content is that slug takes the wrapper too (<c>*chapter-text:0*</c>
/// goes entirely), and a slug inside a wrapper that still holds words takes the whitespace it stranded
/// against the delimiter (<c>**see chapter-text:0**</c> becomes <c>**see**</c>). Not tidiness: in CommonMark
/// a space-preceded closer never closes, so <c>**see **</c> renders the REST OF THE PARAGRAPH bold in
/// <c>app-markdown-text</c>. And a MARKDOWN LINK whose target is an internal ref is UNLINKED, never
/// deleted: <c>[chapter 1](chapter-text:0)</c> becomes <c>chapter 1</c>. The text half of a link is
/// author-facing by construction - it is the only half a reader sees, and the target is not rendered as
/// prose at all - so taking both halves takes words the author was reading. A square pair that is a link's
/// text is exempt from the bracketed-label rule for the same reason.</para>
///
/// <para>DECISION 6 - THIS LAYER NEVER RETURNS NOTHING, which is the one failure it could cause that is
/// worse than every leak it prevents. An answer whose whole content is a gloss (<c>(EXCERPT)</c> alone on
/// the line, and the model does produce one-line answers) strips to the empty string, and an empty answer
/// leaves the reader a card that claims to be grounded and says nothing. So when a strip would leave the
/// answer with no WORD IN IT AT ALL - not merely blank, since <c>(EXCERPT).</c> strips to a lone full stop
/// - the ORIGINAL text comes back untouched and the tokens stay in it: LEAVING JARGON IN BEATS RETURNING
/// NOTHING, exactly the asymmetry <c>ProductChatCitations</c> holds at three separate sites ("A leaked
/// label mid-answer is cosmetic; a deleted sentence is not"). A BULLETED gloss never reaches this guard:
/// A13's <see cref="IsBareListMarker"/> refuses it a line earlier, for the same reason at a smaller scope,
/// and reports it the same way. The refusal is reported separately from the
/// removal count so it cannot pass for a quiet success - see <see cref="Strip(string?, out int)"/> - and
/// the count it reports keeps the MODEL's emission rate honest, because those tokens were emitted whether
/// or not this layer dared delete them. All-or-nothing is deliberate: a partial strip would have to choose
/// which words of a sentence the author gets, and this layer has no basis for that choice.</para>
///
/// <para>DECISION 4 - THE BOUNDARY, i.e. what is deliberately NOT stripped.</para>
/// <list type="bullet">
///   <item>A <c>whole chapter</c> / <c>excerpt</c> ANYWHERE EXCEPT INSIDE A BRACKET GROUP THAT GOES WHOLE.
///   The grounding clause names both words in both languages as the distinction the model must carry to
///   the author in its own sentence, so "I only have part of this chapter" and even "I was given an
///   excerpt" are the instruction WORKING. Bare, emphasised (<c>**excerpt**</c>) and backticked uses are
///   all left alone; so is one inside a bracket that ALSO holds a readable clause, because the bracket
///   then keeps every word it had. Only a parenthetical whose whole content is the gloss goes, and it goes
///   as one thing.</item>
///   <item>THE CITATION LINE, which <c>ProductChatCitations</c> owns end to end. A line whose cleaned form
///   OPENS with a citation label is skipped whole: that parser's position rule already decided what
///   happens to it, including the deliberate choice to leave a refused line in place. An INLINE trailing
///   label is not skipped, because there the label sits at the end of a sentence the author is reading and
///   a fabricated ref in it is the very thing <c>LooksFabricated</c> exists to stop publishing.</item>
///   <item>The ordinary English words <c>register</c> and <c>history</c>, which are keyless refs in
///   <see cref="BookArtifactRefs"/> and ordinary nouns everywhere else. THIS STRIP NEVER TOUCHES THEM, in
///   any wrapper. It used to remove them inside backticks or bold, on the theory that a sentence does not
///   put them there; no measurement supported that, they are the product's own feature names, and the rule
///   turned "This is only an <c>**excerpt**</c>, not the whole thing." into "This is only an, not the whole
///   thing." <c>book-brief</c> and every <c>&lt;type&gt;:&lt;key&gt;</c> ref DO carry a shape no sentence in
///   either language produces, so those go wherever they appear - the same test
///   <c>ProductChatCitations.LooksFabricated</c> already makes.</item>
/// </list>
///
/// <para>THE SHAPE TEST IS <see cref="BookArtifactRefs.LooksLikeArtifactRef"/>, NOT A SECOND GRAMMAR. A
/// candidate slug is found by shape and then handed to the vocabulary's own owner, so a new PREFIXED
/// artifact type (<c>&lt;prefix&gt;:&lt;key&gt;</c>) starts being stripped the day it starts being rendered.
/// A new KEYLESS one does not: <see cref="SlugCandidate"/> requires a colon, so <c>book-brief</c> needed
/// its own <see cref="BareBookBrief"/> regex here and any future keyless ref would need the same. Restating the prefix list here is how an
/// emitter and its parser drift apart, which is the argument <c>BookArtifactRefs</c> was written with.</para>
///
/// <para>FOUR EDGE CASES OF THE NARROWED SURFACE (review findings A11-A14), fixed once the shapes above
/// stopped moving. A11: a bidi control mark sits directly against a slug in RTL prose to fix its rendering
/// direction, and a bare removal orphans it - not whitespace, so <see cref="JoinAtSeam"/> cannot reach it,
/// and an un-consumed embedding initiator still opens a directional run; <see cref="ExpandOverWrapper"/>
/// now consumes it, the same way it already consumes an emptied markdown wrapper. A12: a plain
/// <c>TrimEnd()</c> in <see cref="Tidy"/> could not tell a removal's own trailing space from a markdown
/// HARD LINE BREAK (two trailing spaces) the model wrote elsewhere on the same line, merging two lines into
/// one paragraph only on lines a token happened to leak on; the break is now read before the trim and
/// reattached after. A13: a list item whose entire content was internal tokens collapsed to a bare marker
/// mid-list (<c>- chapter-text:0</c> to <c>-</c>); such a line is now left entirely alone, the same
/// under-strip-over-over-strip choice DECISION 6 makes at the whole-answer scope, applied at the line. A14:
/// this layer's code-span policy now SPLITS DELIBERATELY rather than matching its sibling everywhere. A
/// FENCED block agrees with <c>ProductChatPunctuation.Repair</c> (its SHAPE GUARDS bullet beginning
/// "Text inside backticks is copied verbatim: a code span is content, not prose" - cited by its opening
/// words rather than by line number, because that bullet moved when this cross-reference was added to
/// it) and is skipped whole, a line at a time, before it ever reaches this class's line
/// logic - see <see cref="IsFenceDelimiter"/>. A BARE INLINE span deliberately does NOT agree, and this
/// half is not new: DECISION 5 already ships and tests it. A model does not put a real code example around
/// an internal wire ref, it puts the ref there as styling for something that already reads as technical, so
/// leaving <c>`chapter-text:0`</c> untouched would not cure the leak and would risk a stray unmatched
/// backtick flipping <c>ProductChatPunctuation</c>'s own parity for the rest of the ANSWER (its
/// <c>inCodeSpan</c> state is never reset at a newline). See the fuller
/// note on <see cref="FenceDelimiter"/>.</para>
///
/// <para>WHAT THIS CANNOT DO, said plainly because the next gate has to know it: it changes what the
/// AUTHOR SEES, not what the MODEL EMITS. A zero leak rate measured after this ships is this layer
/// working, NOT the class closing.</para>
///
/// <para>AND THERE IS NO PRE-STRIP CHANNEL, which this paragraph used to assert there was. It said "the
/// API log records the answer as returned"; it does not. No statement in <c>ProductChatService</c> logs
/// the answer prose or the question - the counts are logged and the text never is, deliberately - and a
/// harness driving <c>/api/product-chat</c> reads the POST-pipeline body. So the instruction this
/// paragraph used to carry, "a gate must keep scoring the PRE-strip text", named an evidence channel
/// that does not exist and could not be followed by anyone who tried. What a future gate must do
/// instead:</para>
/// <list type="number">
///   <item>SCORE THE RETURNED ANSWER, and read the number as what the AUTHOR SEES. That is the number
///   this layer is accountable for, and the only one the returned body can support.</item>
///   <item>RECOVER THE MODEL'S OWN RATE FROM THE COUNT this class returns and the service logs
///   per-answer beside the provider and model. That count is the whole pre-strip signal that exists, and
///   it is complete in the one way that matters: it counts the tokens a strip removed AND, separately,
///   every token a strip REFUSED to remove - because removing them would have emptied the whole answer
///   (DECISION 6) or would have left a list item as a bare marker (A13). Neither refusal can silently
///   deflate the rate; the list-item one used to, and was reported by neither number. A gate quoting a
///   rate must read both; a gate that reads only removals under-reports.</item>
///   <item>NOT ASK THIS SERVICE FOR THE PROSE. A gate that genuinely needs pre-strip TEXT has to obtain
///   it on its own path - composing and calling the router without applying this layer, which is what an
///   offline harness already does - and not by logging the answer. This service logs no question and no
///   answer text; that is a privacy posture and not an oversight, and a debug-level switch would not
///   preserve it, because whoever turns it on gets every author's prose.</item>
/// </list>
/// </summary>
public static class ProductChatInternalLabels
{
    /// <summary>
    /// The most a whole-parenthetical removal may ever delete. A gloss is short by construction (the
    /// longest of the five measured ones is 57 characters, the finding-guid parenthetical of leak 2, and
    /// the bound is about three and a half times that); anything longer keeps its brackets and loses only its
    /// tokens, so a mis-classified parenthetical can never cost a paragraph. Same "bound the SHAPE, not
    /// just the content" rule as <c>ProductChatCitations.MaxInlineCitationChars</c>.
    /// </summary>
    internal const int MaxGroupChars = 200;

    /// <summary>
    /// The most residual words a parenthetical may hold and still be removed whole. See the type's
    /// DECISION 2: at two, the residue is the connective that introduced the token; at three it is a
    /// clause.
    /// </summary>
    internal const int MaxResidueWords = 2;

    /// <summary>
    /// The whole/excerpt vocabulary <see cref="BookArtifactBlocks.WholeChapterLabelFormat"/> and
    /// <see cref="BookArtifactBlocks.ExcerptLabelFormat"/> put in front of the model, longest first so a
    /// match takes the longest form. These are stripped ONLY as a gloss (DECISION 4); the same words in
    /// running prose are the instruction working.
    /// </summary>
    private static readonly string[] LabelFragments =
    {
        "not the whole chapter", "whole chapter", "excerpt"
    };

    /// <summary>
    /// The bracketed chapter label, in the English form the blocks actually render and in the Hebrew form
    /// a model translating it would write. The tail is bounded and may not cross a bracket or a newline,
    /// so this can only ever consume a label-sized fragment.
    /// </summary>
    private static readonly Regex BracketedLabel = new(
        @"\[\s*(?:CHAPTER|פרק)\s*\d+[^\]\r\n]{0,40}\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A <c>&lt;word&gt;:&lt;key&gt;</c> candidate. SHAPE ONLY - whether it is a ref is decided by
    /// <see cref="BookArtifactRefs.LooksLikeArtifactRef"/>, which owns the vocabulary.
    /// </summary>
    private static readonly Regex SlugCandidate = new(
        @"(?<![A-Za-z0-9\-:/])[A-Za-z][A-Za-z\-]*:[A-Za-z0-9][A-Za-z0-9\-]*(?![A-Za-z0-9\-])",
        RegexOptions.CultureInvariant);

    /// <summary>The one keyless ref whose shape no sentence produces, so it needs no wrapper to be safe.</summary>
    private static readonly Regex BareBookBrief = new(
        @"(?<![A-Za-z0-9\-])book-brief(?![A-Za-z0-9\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A markdown link. DECISION 5: the text half is the only half a reader sees, so a link whose target
    /// is an internal ref is unlinked and never deleted, and its text brackets are not a label. Both halves
    /// must be NON-EMPTY: <c>[](x)</c> has no author-facing half to protect, and treating it as a link
    /// would make the removal of its <c>[</c> overlap the seam repair that follows it.
    /// </summary>
    private static readonly Regex MarkdownLink = new(
        @"\[(?<text>[^\[\]\r\n]+)\]\((?<target>[^()\r\n]+)\)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The emphasis delimiters a removal may consume (DECISION 5). Backtick and asterisk only: underscore
    /// is not excluded by <see cref="SlugCandidate"/>'s own lookahead, so treating it as a delimiter would
    /// let a removal reach into a word.
    /// </summary>
    private const string WrapperChars = "`*";

    private static readonly Regex ResidueWord = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// A fenced code block's opening or closing line (A14): three or more backticks or tildes, with up to
    /// three leading spaces, the same shape CommonMark itself uses.
    ///
    /// <para>A14 - THE POLICY, AND WHY IT SPLITS IN TWO. <c>ProductChatPunctuation.Repair</c> (its SHAPE
    /// GUARDS bullet beginning "Text inside backticks is copied verbatim: a code span is content, not
    /// prose" - cited by its opening words, because that bullet MOVED when this cross-reference was added
    /// to it and a line number would already be stale) skips EVERY code span, inline and fenced alike,
    /// because an em-dash inside one is plausibly the model quoting real content - a literal string, an
    /// example - and rewriting it would corrupt that content for no measured gain. This layer does not
    /// have that plausibility for a FENCED block either, and skips it the same way: see
    /// <see cref="IsFenceDelimiter"/>, checked per line in <see cref="Strip(string?, out int)"/> because a
    /// fence can span many lines and this method only ever sees one.</para>
    ///
    /// <para>A BARE INLINE code span is the one place the two layers deliberately DISAGREE, and this is
    /// already shipped, already tested behaviour (DECISION 5's <c>AWrapperEmptiedByASlugRemoval_GoesWithTheSlug</c>),
    /// not an oversight this todo reopens: a model does not put a real code example around an internal
    /// wire ref, it puts the ref there as a STYLING CHOICE for something that already reads as technical,
    /// so <c>`chapter-text:0`</c> is exactly as much a leak as a bare `chapter-text:0` - backtick styling
    /// does not cure it, and arguably makes it read MORE like something the author should recognise.
    /// Protecting it would also leave the wrapper's own backticks stray whenever the token inside them was
    /// the whole span, and an unmatched backtick then flips <c>ProductChatPunctuation</c>'s own code-span
    /// parity for every character AFTER it in the ANSWER - that layer runs one pass over the whole text and
    /// never resets the state at a newline. So the inline case is removed, wrapper and all
    /// (<see cref="ExpandOverWrapper"/>), the same as any other wrapped slug.</para>
    /// </summary>
    private static readonly Regex FenceDelimiter = new(
        @"^[ \t]{0,3}(`{3,}|~{3,})", RegexOptions.CultureInvariant);

    private static bool IsFenceDelimiter(string line) => FenceDelimiter.IsMatch(line);

    /// <summary>
    /// The bidi control characters (Unicode category Cf) a removal must not orphan (A11). They mark a
    /// directional boundary against the token they sit beside, so once the token is gone they are inert
    /// noise: not whitespace, so <see cref="JoinAtSeam"/>'s space collapse cannot reach them, and an
    /// un-consumed embedding initiator (LRE/RLE/LRO/RLO) still opens a directional run the rest of the
    /// answer never closes. The bracketed-label half of this class needs no equivalent -
    /// <see cref="BracketedLabel"/>'s own <c>\s</c> does not match a Cf character either, so a mark between
    /// the bracket and its digits just defeats the match and the label is left alone, which fails safe. The
    /// parenthetical half (shape 2) needs no equivalent either: <see cref="ResidueWord"/> already ignores
    /// Cf, so a mark inside a gloss that goes whole goes with it.
    /// </summary>
    private static bool IsBidiControl(char c) =>
        c == '\u200E' || c == '\u200F'                    // LRM, RLM
        || (c >= '\u202A' && c <= '\u202E')                // LRE, RLE, PDF, LRO, RLO
        || (c >= '\u2066' && c <= '\u2069');               // LRI, RLI, FSI, PDI

    /// <summary>
    /// Removes the internal tokens that reached the prose, returning the cleaned answer and HOW MANY were
    /// removed. The count is the observability half of this layer, for the same reason
    /// <see cref="ProductChatPunctuation.Repair"/> returns one: a silent rewrite of model output that says
    /// nothing ships its own failures invisibly. It is a count, never the text.
    ///
    /// <para>Callers that log take the <see cref="Strip(string?, out int)"/> overload, because a refusal to
    /// empty the answer (DECISION 6) is invisible in this pair: it looks exactly like a clean answer.</para>
    /// </summary>
    public static (string Text, int Removed) Strip(string? answer) => Strip(answer, out _);

    /// <summary>
    /// As <see cref="Strip(string?)"/>, and reports the refusal DECISION 6 makes. When removing this
    /// answer's internal tokens would have left the reader nothing, the original text comes back verbatim,
    /// <c>Removed</c> is 0 because nothing was removed, and <paramref name="keptToAvoidEmptying"/> carries
    /// how many tokens were LEFT IN the prose for that reason.
    ///
    /// <para>Two numbers rather than one, because they mean different things and only one caller needs
    /// both: <c>Removed</c> is what changed under the author's eyes, and the two summed are what the MODEL
    /// emitted. A refusal folded into <c>Removed</c> would claim a rewrite that did not happen; a refusal
    /// reported as 0 everywhere would deflate the only pre-strip rate this program has (see the type's
    /// "no pre-strip channel" note).</para>
    /// </summary>
    public static (string Text, int Removed) Strip(string? answer, out int keptToAvoidEmptying)
    {
        keptToAvoidEmptying = 0;
        if (string.IsNullOrEmpty(answer)) return (answer ?? string.Empty, 0);
        var lines = answer.Split('\n');
        var removed = 0;
        var keptByALine = 0;
        var inFencedBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Keep the physical line ending untouched: this may run on text ProductChatCitations returned
            // verbatim (it only normalises the answer it actually modified).
            var carriageReturn = line.EndsWith('\r');
            if (carriageReturn) line = line[..^1];

            // A14: A FENCED CODE BLOCK IS CONTENT, NOT PROSE - the multi-line twin of the inline policy
            // below, and the reason ProductChatPunctuation.Repair's code-span bullet (the one beginning
            // "Text inside backticks is copied verbatim") is cited rather than restated: both layers must
            // agree, or a fenced example showing the wire vocabulary gets silently edited by one layer and
            // left alone by the other. The delimiter line only flips the state; nothing between a pair of
            // them reaches StripLine.
            if (IsFenceDelimiter(line))
            {
                inFencedBlock = !inFencedBlock;
            }
            // THE CITATION LINE IS NOT OURS (DECISION 4). Position already decided its fate one layer up.
            else if (!inFencedBlock && !ProductChatCitations.OpensWithCitationLabel(line))
            {
                var (cleaned, count, kept) = StripLine(line);
                line = cleaned;
                removed += count;
                keptByALine += kept;
            }

            lines[i] = carriageReturn ? line + '\r' : line;
        }

        // A LINE-LEVEL REFUSAL IS STILL A REFUSAL, and it used to be reported by neither number. A13 leaves
        // a list item whose whole content was internal tokens entirely alone, so an answer leaking
        // "- chapter-text:0" mid-list came back with the leak in it, `Removed` 0 and `keptToAvoidEmptying` 0
        // - indistinguishable from a clean answer to the only pre-strip signal this program has. It is the
        // same event as DECISION 6's whole-answer refusal at a smaller scope, so it is reported the same way.
        if (removed == 0)
        {
            keptToAvoidEmptying = keptByALine;
            return (answer, 0);
        }

        var stripped = string.Join("\n", lines);

        // DECISION 6, AND THIS IS THE BELT - the guard that sits on the defect itself. An answer whose whole
        // content was a gloss ("(EXCERPT)" alone, which the model does write) strips to nothing, and nothing
        // is the one output this layer must never produce: a leaked token is cosmetic, an empty answer is
        // the reader's whole card. So the ORIGINAL comes back with its jargon still in it, and the tokens
        // are reported as KEPT rather than removed so the refusal cannot read as a clean answer. The braces
        // is ProductChatService's own re-check after every rewrite, which is what would catch this if a
        // future edit to the code above ever got past here.
        if (!HasSomethingToRead(stripped))
        {
            keptToAvoidEmptying = removed + keptByALine;
            return (answer, 0);
        }

        keptToAvoidEmptying = keptByALine;
        return (stripped, removed);
    }

    /// <summary>
    /// Whether an answer still holds a WORD. Not <c>IsNullOrWhiteSpace</c>: the one-line answers this guard
    /// exists for do not all strip to blank. <c>"(EXCERPT)."</c> strips to <c>"."</c> and <c>"*   (EXCERPT)"</c>
    /// to its own bullet, and a card holding a full stop is as empty to a reader as a card holding nothing.
    /// A letter or a digit is the floor, and no answer written in either language can fail it, so this can
    /// only ever fire on text that was entirely internal.
    /// </summary>
    private static bool HasSomethingToRead(string text)
    {
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) return true;

        return false;
    }

    private static (string Text, int Removed, int Kept) StripLine(string line)
    {
        var links = MarkdownLink.Matches(line);
        var groups = Groups(line, links);
        var tokens = Tokens(line, groups, links);
        if (tokens.Count == 0) return (line, 0, 0);

        // Each removal is a half-open span. Whole-group removals subsume the tokens inside them, which is
        // why the token spans they cover are dropped rather than applied twice.
        var removals = new List<(int Start, int End)>();
        var covered = new bool[tokens.Count];
        var removed = 0;

        foreach (var group in groups)
        {
            var inside = new List<int>();
            for (var i = 0; i < tokens.Count; i++)
                if (tokens[i].Start >= group.ContentStart && tokens[i].End <= group.ContentEnd)
                    inside.Add(i);

            if (inside.Count == 0) continue;

            var residue = ResidueOf(line, group, inside.Select(i => (tokens[i].Start, tokens[i].End)));
            var wholeGroup = residue <= MaxResidueWords
                             && group.End - group.Start <= MaxGroupChars;

            if (!wholeGroup) continue;

            // DECISION 5 APPLIES TO THIS PATH TOO. A whole-group removal used to splice out the brackets
            // and leave the emphasis or backtick wrapper that held them, so `**(EXCERPT)**` came back as
            // `****` and `` `(EXCERPT)` `` as a bare pair of backticks. That is the SAME residue the token
            // path consumes the wrapper to avoid, and the same two harms: `*(EXCERPT)*` leaves `**`, which
            // opens a CommonMark emphasis run that never closes, and a stray backtick pair flips
            // ProductChatPunctuation's own code-span parity, which was MEASURED to silently disable its
            // em-dash repair for the rest of the line. Only the BALANCED case is taken (both sides moved):
            // a one-sided delimiter is either the model's own or, at the head of a line, this line's list
            // marker, and eating a bullet would defeat the bare-marker guard below.
            var (wrapStart, wrapEnd) = ExpandOverWrapperDelimiters(line, group.Start, group.End);
            removals.Add(wrapStart < group.Start && wrapEnd > group.End
                ? (wrapStart, wrapEnd)
                : (group.Start, group.End));
            foreach (var i in inside) covered[i] = true;
            removed += inside.Count;

            // DECISION 5. This group is a markdown link's TARGET, so the brackets around the link's text
            // go with it and the text itself stays: `[chapter 1](chapter-text:0)` leaves `chapter 1`.
            var link = LinkWhoseTargetIs(links, group);
            if (link != null)
            {
                removals.Add((link.Index, link.Index + 1));
                removals.Add((group.Start - 1, group.Start));
            }
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (covered[i]) continue;

            // A LABEL FRAGMENT IS ONLY EVER REMOVED AS PART OF ITS WHOLE GROUP. Its group kept its
            // brackets, which means the group holds a clause the author can read, and deleting one word
            // out of a clause is the over-strip this class was narrowed to stop: `(an excerpt of chapter
            // 8)` used to come back as `(an of chapter 8)`. Leaving the word is the safe direction.
            if (tokens[i].GroupOnly) continue;

            removals.Add(ExpandOverWrapper(line, tokens[i].Start, tokens[i].End));
            removed++;
        }

        if (removals.Count == 0) return (line, 0, 0);

        // The list marker is read off the line BEFORE anything is removed, so a strip at the head of the
        // line cannot leave the indentation it created behind (and cannot eat a bullet's own spacing).
        var prefix = ListPrefix.Match(line).Value;

        var text = Apply(line, removals, prefix.Length);
        var result = Tidy(text, prefix);

        // A13: a list item whose ENTIRE content was internal tokens must not collapse to a bare marker
        // mid-list ("- chapter-text:0" -> "-", "1. chapter-text:0" -> "1."), which reads as an empty
        // bullet to a reader and a broken item to a markdown renderer. This is DECISION 6's choice
        // ("leaving jargon in beats returning nothing") applied at the LINE rather than the whole answer:
        // the line is left entirely alone rather than shipped as a marker with nothing after it. The tokens
        // it declined to remove are reported as KEPT, for DECISION 6's reason: a refusal that reports
        // nothing is indistinguishable from a clean answer to the only pre-strip signal this program has.
        if (IsBareListMarker(result, prefix)) return (line, 0, removed);

        return (result, removed, 0);
    }

    /// <summary>
    /// Whether stripping this line left nothing but its own list marker. Requires the <see cref="ListPrefix"/>
    /// match to have actually captured a bullet or ordinal glyph - a prefix of pure indentation is not a
    /// marker and an indented, now-empty line is not this case.
    /// </summary>
    private static bool IsBareListMarker(string result, string prefix)
    {
        var marker = prefix.TrimEnd(' ', '\t');
        if (marker.Length == 0) return false;

        var glyph = marker[^1];
        if (glyph != '-' && glyph != '*' && glyph != '+' && glyph != '.' && glyph != ')') return false;

        return result.TrimEnd(' ', '\t') == marker;
    }

    /// <summary>
    /// The markdown link whose TARGET is this group, or null. Matched on the group's own span so a
    /// parenthetical that merely follows a <c>]</c> is not mistaken for a link target.
    /// </summary>
    private static Match? LinkWhoseTargetIs(MatchCollection links, Group group)
        => links.FirstOrDefault(m => m.Index + m.Length == group.End
                                     && m.Groups["target"].Index == group.ContentStart);

    /// <summary>
    /// DECISION 5, plus A11. A removal takes the delimiters it emptied, or the whitespace it stranded
    /// against one, and now also the bidi control characters ITS OWN REMOVAL orphaned - scoped, like every
    /// other rule here, to the characters TOUCHING the removal, never to the line. Wrapper consumption is
    /// computed first (unchanged from before A11), then the result is expanded outward over any run of
    /// <see cref="IsBidiControl"/> characters touching it, on both sides, in one final pass: a mark can sit
    /// directly against the token (a Hebrew RLM immediately before and after <c>chapter-text:0</c>, no
    /// space) or against a wrapper the first pass already consumed, and either way it is noise once what it
    /// bounded is gone.
    /// </summary>
    private static (int Start, int End) ExpandOverWrapper(string line, int start, int end)
    {
        var (s, e) = ExpandOverWrapperDelimiters(line, start, end);

        while (s > 0 && IsBidiControl(line[s - 1])) s--;
        while (e < line.Length && IsBidiControl(line[e])) e++;

        return (s, e);
    }

    private static (int Start, int End) ExpandOverWrapperDelimiters(string line, int start, int end)
    {
        var left = start;
        while (left > 0 && WrapperChars.IndexOf(line[left - 1]) >= 0) left--;

        var right = end;
        while (right < line.Length && WrapperChars.IndexOf(line[right]) >= 0) right++;

        // The wrapper held nothing but this token, so it goes too: `*chapter-text:0*` would otherwise
        // leave `**`, which opens an emphasis run that never closes.
        if (left < start && right > end && start - left == right - end && line[left] == line[right - 1])
            return (left, right);

        // The wrapper still holds words. Take the whitespace the removal stranded against the delimiter,
        // because in CommonMark a space-preceded closer never closes and `**see **` would render the rest
        // of the paragraph bold.
        if (right > end && left == start)
        {
            var s = start;
            while (s > 0 && (line[s - 1] == ' ' || line[s - 1] == '\t')) s--;
            return (s, end);
        }

        if (left < start && right == end)
        {
            var e = end;
            while (e < line.Length && (line[e] == ' ' || line[e] == '\t')) e++;
            return (start, e);
        }

        return (start, end);
    }

    /// <summary>Words left in a parenthetical once its internal tokens are taken out of it.</summary>
    private static int ResidueOf(string line, Group group, IEnumerable<(int Start, int End)> inside)
    {
        var content = line[group.ContentStart..group.ContentEnd].ToCharArray();

        foreach (var (start, end) in inside)
            for (var i = start; i < end; i++)
                content[i - group.ContentStart] = ' ';

        return ResidueWord.Matches(new string(content)).Count;
    }

    private static string Apply(string line, List<(int Start, int End)> removals, int prefixLength)
    {
        // Expansion can make two removals touch or overlap, and splicing those independently would eat
        // text between them, so they are merged first.
        var merged = new List<(int Start, int End)>();

        foreach (var span in removals.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            else
                merged.Add(span);
        }

        // Right to left, so every span still indexes the text it was measured against.
        for (var i = merged.Count - 1; i >= 0; i--)
            line = JoinAtSeam(line[..merged[i].Start], line[merged[i].End..], prefixLength);

        return line;
    }

    /// <summary>
    /// The punctuation a removal strands, repaired AT THE SEAM THE REMOVAL MADE and nowhere else. This
    /// used to be five regexes run over the whole of any line the strip touched, which is how a removal at
    /// the head of a sentence came to glue two words together around a smiley twelve characters away
    /// (review finding A6). Whitespace and punctuation the model wrote somewhere else on the line is none
    /// of this layer's business, and it is now unreachable rather than merely unintended.
    /// </summary>
    private static string JoinAtSeam(string left, string right, int prefixLength)
    {
        // A list marker's own spacing is not whitespace a removal created, so the scan stops at it.
        var l = left.Length;
        while (l > prefixLength && (left[l - 1] == ' ' || left[l - 1] == '\t')) l--;

        var r = 0;
        while (r < right.Length && (right[r] == ' ' || right[r] == '\t')) r++;

        var head = left[..l];
        var tail = right[r..];
        var spaced = left.Length > l || r > 0;
        var before = l > 0 ? left[l - 1] : '\0';

        if (before == '(' || before == '[')
        {
            // The opener the removal left holding nothing, with or without the separator that introduced
            // what went: "(מצא פתיחה: finding:x)" would otherwise close as "(מצא פתיחה:)" or "()".
            var rest = tail;
            if (rest.Length > 0 && (rest[0] == ',' || rest[0] == ';' || rest[0] == ':'))
                rest = rest[1..].TrimStart(' ', '\t');

            if (rest.Length > 0 && rest[0] == (before == '(' ? ')' : ']'))
                return head[..^1] + rest[1..];

            return head + tail;
        }

        // A separator now leaning on a closer, and a closer or a sentence ending now leaning on the space
        // the removal left in front of it.
        if ((before == ',' || before == ';' || before == ':') && tail.Length > 0
            && (tail[0] == ')' || tail[0] == ']'))
            return head[..^1] + tail;

        if (tail.Length > 0 && SeamPunctuation.Contains(tail[0]))
            return head + tail;

        if (!spaced) return head + tail;

        // Two runs of whitespace that met when the text between them went.
        var gap = head.Length > 0 && (head[^1] == ' ' || head[^1] == '\t') ? string.Empty : " ";
        return head + gap + tail;
    }

    // ─── Token discovery ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An internal token, and whether it is one that only counts INSIDE a bracket group. A label fragment
    /// is <c>GroupOnly</c>: <c>excerpt</c> and <c>whole chapter</c> are words the grounding clause tells
    /// the model to reason with, so they are only ever a leak as the whole content of a gloss, and they go
    /// only when that whole gloss goes.
    /// </summary>
    private readonly record struct Token(int Start, int End, bool GroupOnly);

    /// <summary>
    /// Every internal token on the line, de-overlapped and in document order. A LABEL FRAGMENT counts only
    /// inside a group (DECISION 4); everything else carries a shape no sentence produces and counts
    /// wherever it stands, INCLUDING inside a bare inline code span - see the A14 policy note on
    /// <see cref="FenceDelimiter"/> for why that is a deliberate divergence from
    /// <see cref="ProductChatPunctuation.Repair"/> rather than an oversight this method still owes it. A
    /// FENCED block is a different matter and is never handed to this method at all: <see cref="Strip(string?, out int)"/>
    /// skips it a whole line at a time, before <c>StripLine</c> - and therefore this method - ever run.
    /// </summary>
    private static List<Token> Tokens(string line, List<Group> groups, MatchCollection links)
    {
        var spans = new List<Token>();

        foreach (Match m in BracketedLabel.Matches(line))
        {
            // DECISION 5: a square pair that is a markdown link's TEXT is the half a reader sees, not a
            // label. `[chapter 1](chapter-text:0)` must not lose the words "chapter 1".
            if (links.Any(l => l.Index == m.Index)) continue;
            spans.Add(new Token(m.Index, m.Index + m.Length, false));
        }

        foreach (Match m in SlugCandidate.Matches(line))
            if (BookArtifactRefs.LooksLikeArtifactRef(m.Value))
                spans.Add(new Token(m.Index, m.Index + m.Length, false));

        foreach (Match m in BareBookBrief.Matches(line))
            spans.Add(new Token(m.Index, m.Index + m.Length, false));

        foreach (var group in groups)
        {
            // A GLOSS IS A PARENTHETICAL, which is what DECISION 2 and DECISION 4 both say and what all
            // five measured leaks are: every one puts its token inside a ROUND pair. A square pair is not
            // a gloss - in prose it is the author's own aside - so `He gave me [an excerpt] of it.` kept
            // its words back when the fragment scan was round-only, and lost them when this loop was
            // widened to every group. A slug or a `[CHAPTER n]` label inside a square pair is unaffected:
            // those carry a shape no sentence produces and are minted below, outside this loop.
            if (line[group.Start] != '(') continue;

            foreach (var fragment in LabelFragments)
                foreach (var at in Occurrences(line, fragment, group.ContentStart, group.ContentEnd))
                    spans.Add(new Token(at, at + fragment.Length, true));
        }

        return Deoverlap(spans);
    }

    private static IEnumerable<int> Occurrences(string line, string needle, int from, int to)
    {
        var at = from;

        while (at < to)
        {
            var found = line.IndexOf(needle, at, to - at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) yield break;

            var beforeOk = found == 0 || !char.IsLetterOrDigit(line[found - 1]);
            var after = found + needle.Length;
            var afterOk = after >= line.Length || !char.IsLetterOrDigit(line[after]);
            if (beforeOk && afterOk) yield return found;

            at = found + needle.Length;
        }
    }

    /// <summary>Longest-first, so <c>[CHAPTER 0 EXCERPT]</c> is one token and not a label plus a fragment.</summary>
    private static List<Token> Deoverlap(List<Token> spans)
    {
        var kept = new List<Token>();

        foreach (var span in spans.OrderBy(s => s.Start).ThenByDescending(s => s.End - s.Start))
            if (kept.All(k => span.Start >= k.End || span.End <= k.Start))
                kept.Add(span);

        return kept.OrderBy(s => s.Start).ToList();
    }

    // ─── Bracket groups ─────────────────────────────────────────────────────────────────────────

    private readonly record struct Group(int Start, int End, int ContentStart, int ContentEnd);

    /// <summary>
    /// The round and square bracket pairs on the line, non-nested (first closer wins) and never crossing a
    /// newline. A square pair that is itself a bracketed label is NOT a group - it is a token - so a
    /// <c>[CHAPTER 7]</c> is removed as one thing rather than analysed for residue.
    ///
    /// <para>DECISION 5's protection of a link's TEXT is NOT enforced here, deliberately. A link's text is
    /// a square pair like any other, and the thing that used to eat it - <c>[an excerpt](chapter-text:0)</c>
    /// losing BOTH halves - was the FRAGMENT scan reaching into square pairs, which is fixed where it
    /// belongs, in <see cref="Tokens"/>. An exemption here as well was tried and reverted: it killed no
    /// mutant the round-only scan does not already kill, and it made one case worse, because a link whose
    /// text IS a slug (<c>[chapter-text:0](https://x)</c>) then loses the slug and keeps the empty brackets
    /// instead of losing the pair.</para>
    /// </summary>
    private static List<Group> Groups(string line, MatchCollection links)
    {
        var groups = new List<Group>();
        var labels = BracketedLabel.Matches(line)
            .Where(m => !links.Any(l => l.Index == m.Index))
            .Select(m => m.Index)
            .ToHashSet();

        for (var i = 0; i < line.Length; i++)
        {
            var open = line[i];
            if (open != '(' && open != '[') continue;
            if (open == '[' && labels.Contains(i)) continue;

            var close = line.IndexOf(open == '(' ? ')' : ']', i + 1);
            if (close < 0) continue;

            groups.Add(new Group(i, close + 1, i + 1, close));
            i = close;
        }

        return groups;
    }

    // ─── Tidying ────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a removal must not leave a space in front of. Used only at a seam, never on a line.</summary>
    private static readonly char[] SeamPunctuation = { '.', ',', ';', ':', '!', '?', ')', ']' };

    /// <summary>
    /// A list marker and its indentation, kept out of the tidying so a bullet's own <c>"*   "</c> spacing
    /// survives a strip somewhere later on the same line.
    /// </summary>
    private static readonly Regex ListPrefix = new(
        @"^[ \t]*(?:[-*+]|\d+[.)])?[ \t]*", RegexOptions.CultureInvariant);

    /// <summary>
    /// What is left over once every seam has been joined: a removal that took the head of the line, and a
    /// removal that took its tail. The punctuation repair itself lives at the seam (see
    /// <see cref="JoinAtSeam"/>), so nothing here reaches text the strip did not touch.
    ///
    /// <para>DECISION 3: nothing here has to put a bracket back, because nothing here can take half of
    /// one. There used to be a <c>DropUnmatchedBrackets</c> pass at this seam; it was line-wide, so the
    /// only brackets it ever reached were the model's own.</para>
    ///
    /// <para>A12: a plain <c>TrimEnd()</c> cannot tell a removal's own trailing whitespace from a markdown
    /// HARD LINE BREAK (two or more trailing spaces, which render as <c>&lt;br&gt;</c>) that the model wrote
    /// and this strip never touched - it only fires because the line held a removal SOMEWHERE, not because
    /// the removal reached the end of it. Blindly trimming it merges two lines into one paragraph, and only
    /// on lines the strip happened to fire on, so the same document would render two ways depending on
    /// whether a token elsewhere on the line leaked. The break is read off <c>text</c> BEFORE the trim and
    /// reattached after, which preserves it exactly when nothing between the removal and the end of the line
    /// consumed it.</para>
    /// </summary>
    private static string Tidy(string text, string prefix)
    {
        // Anything white BEYOND the line's original marker is whitespace a removal created.
        var line = (text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text)
            .TrimStart(' ', '\t');

        var hardBreak = line.Length >= 2 && line[^1] == ' ' && line[^2] == ' ';
        var trimmed = (prefix + line).TrimEnd();

        return hardBreak && trimmed.Length > 0 ? trimmed + "  " : trimmed;
    }
}
