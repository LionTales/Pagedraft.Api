using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The one workspace rule that authored strings can honour and MODEL OUTPUT cannot (chatbot phase A,
/// review finding 17): no em-dash in user-facing text.
///
/// <para>WHAT THESE TESTS DEFEND. <c>ProductChatServiceTests.NoUserFacingStringContainsAnEmDash</c>
/// sweeps the AUTHORED strings, which is the half of the surface a code review can hold. The other
/// half is the answer itself, written by a model that echoes punctuation from its own frame: g1
/// measured one leak in 72 answers, g2 one in 102. The repair is a silent rewrite of user-visible text
/// with no human in the loop, so what is pinned here is not only "the dash is gone" but the SHAPE of
/// the rewrite - what it refuses to touch, how far it can move the text, and that it says out loud
/// when it fires.</para>
///
/// <para>EVERY "no em-dash in the output" assertion below is paired with a floor asserting the INPUT
/// had one. Without it the assertion passes on a string that never contained a dash, which is a
/// vacuous shape this codebase has shipped before.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK: the repair is pure, and the service-level tests stub the
/// router.</para>
/// </summary>
public class ProductChatPunctuationTests
{
    /// <summary>U+2014, as an escape: a literal here would be invisible in exactly the file that
    /// governs it, and would trip a grep for em-dashes in the sources.</summary>
    private const string Dash = "\u2014";

    /// <summary>U+2013. Deliberately NOT repaired; see <c>AnEnDashIsLeftAlone</c>.</summary>
    private const string EnDash = "\u2013";

    // ─── The two leaks that were actually measured ──────────────────────────────────────────────

    /// <summary>
    /// THE REAL g1 AND g2 STRINGS, verbatim. Both were GLUED between two words, which is why the
    /// replacement is a comma FOLLOWED BY A SPACE: a bare comma would render "fails,but" and trade a
    /// style defect for a worse one.
    /// </summary>
    [Theory]
    [InlineData("The check can be out of date or fails" + Dash + "but the pass still runs.",
                "The check can be out of date or fails, but the pass still runs.")]
    [InlineData("There is no separate scope control" + Dash + "the pass runs on the selected scene.",
                "There is no separate scope control, the pass runs on the selected scene.")]
    public void TheMeasuredLeaks_BecomeACommaAndASpace(string modelOutput, string expected)
    {
        Assert.Contains(Dash, modelOutput, StringComparison.Ordinal);   // floor: the input really had one

        var (repaired, count) = ProductChatPunctuation.Repair(modelOutput);

        Assert.Equal(expected, repaired);
        Assert.Equal(1, count);
        Assert.DoesNotContain(Dash, repaired, StringComparison.Ordinal);
    }

    /// <summary>
    /// END TO END THROUGH THE SERVICE, because the repair is only worth anything at the seam the user
    /// reads from: the citation on the last line is still parsed and still stripped, and the prose the
    /// user is left with carries no dash. (The ORDER of the two steps is pinned separately, by
    /// <c>TheRepairRunsAfterTheCitationParse_SoStrippingTheLabelLeavesNoStrandedComma</c>.)
    /// </summary>
    [Fact]
    public async Task TheAnswerTheUserSees_HasNoEmDash_AndStillCarriesItsCitation()
    {
        var modelOutput = "Export writes a DOCX" + Dash + "the file lands in your downloads folder.\n\nGuides: export";
        Assert.Contains(Dash, modelOutput, StringComparison.Ordinal);   // floor

        var svc = Service(AnsweringRouter(new List<AiRequest>(), modelOutput), out _);

        var result = await svc.AnswerAsync(new ProductChatRequest("How do I export my book to Word?"), CancellationToken.None);

        Assert.DoesNotContain(Dash, result.Answer, StringComparison.Ordinal);
        Assert.Equal("Export writes a DOCX, the file lands in your downloads folder.", result.Answer);
        Assert.Equal(new[] { "export" }, result.GuideIds);
        Assert.DoesNotContain("Guides:", result.Answer, StringComparison.Ordinal);
        Assert.True(result.IsGrounded);
    }

    /// <summary>
    /// THE ORDER IS LOAD-BEARING, and this is the case that shows it. When the model glues the citation
    /// label straight onto an em-dash, the parser strips the label and hands back prose ending in the
    /// dash, which the repair then drops outright because a dash ending a line joins nothing. Run the
    /// other way round the dash has already become a comma, the parser strips the label from behind it,
    /// and the user is left reading a sentence that ends in a stranded comma. Same citation either way,
    /// so only the prose catches it.
    /// </summary>
    [Fact]
    public async Task TheRepairRunsAfterTheCitationParse_SoStrippingTheLabelLeavesNoStrandedComma()
    {
        var modelOutput = "Export writes a DOCX file" + Dash + "Guides: export";
        Assert.Contains(Dash, modelOutput, StringComparison.Ordinal);   // floor

        var svc = Service(AnsweringRouter(new List<AiRequest>(), modelOutput), out _);

        var result = await svc.AnswerAsync(new ProductChatRequest("How do I export my book to Word?"), CancellationToken.None);

        Assert.Equal(new[] { "export" }, result.GuideIds);
        Assert.True(result.Answer == "Export writes a DOCX file",
            "The punctuation repair has moved AHEAD of ProductChatCitations.Extract. The parser reads the " +
            "character immediately before the citation label (its Guard A) and strips from there, so a dash " +
            "already turned into a comma leaves that comma stranded at the end of the answer the user reads. " +
            $"Got: {result.Answer}");
    }

    /// <summary>The rule is not English-only: the answer language follows the question, and a Hebrew
    /// answer is read by the same authors under the same convention.</summary>
    [Fact]
    public async Task AHebrewAnswer_IsRepairedToo()
    {
        var modelOutput = "הייצוא מפיק קובץ DOCX" + Dash + "הקובץ נשמר בתיקיית ההורדות.";
        Assert.Contains(Dash, modelOutput, StringComparison.Ordinal);   // floor

        var svc = Service(AnsweringRouter(new List<AiRequest>(), modelOutput), out _);

        var result = await svc.AnswerAsync(new ProductChatRequest("איך מייצאים את הספר?"), CancellationToken.None);

        Assert.Equal("he", result.Language);
        Assert.DoesNotContain(Dash, result.Answer, StringComparison.Ordinal);
        Assert.Contains("DOCX, הקובץ נשמר", result.Answer, StringComparison.Ordinal);
    }

    // ─── What the repair refuses to touch ───────────────────────────────────────────────────────

    /// <summary>
    /// A CODE SPAN IS CONTENT, NOT PROSE. Rewriting punctuation inside backticks would corrupt the one
    /// thing a reader is meant to copy verbatim, so the span is passed through untouched while the
    /// prose on either side of it is repaired. Asserted on an input that trips BOTH sides at once, so
    /// a repair that simply gave up on any answer containing a backtick would not pass.
    /// </summary>
    [Fact]
    public void ACodeSpanKeepsItsEmDash_WhileTheProseAroundItIsStillRepaired()
    {
        var text = "Type `a" + Dash + "b` in the field, and the run fails" + Dash + "but the pass continues.";
        Assert.Equal(2, text.Count(c => c == Dash[0]));   // floor: two dashes in, one protected

        var (repaired, count) = ProductChatPunctuation.Repair(text);

        Assert.True(repaired.Contains("`a" + Dash + "b`", StringComparison.Ordinal),
            "The repair rewrote punctuation INSIDE a code span. A span is content the reader is meant to " +
            $"copy verbatim, not prose. Got: {repaired}");
        Assert.Contains("the run fails, but the pass continues.", repaired, StringComparison.Ordinal);
        Assert.Equal(1, count);
        Assert.Equal(1, repaired.Count(c => c == Dash[0]));
    }

    /// <summary>A fenced block is protected by the same rule, so a multi-line example survives whole.</summary>
    [Fact]
    public void AFencedBlockIsLeftAlone()
    {
        var text = "Run it:\n```\nexport" + Dash + "docx\n```\nand it saves" + Dash + "then exports.";
        Assert.Equal(2, text.Count(c => c == Dash[0]));   // floor

        var (repaired, count) = ProductChatPunctuation.Repair(text);

        Assert.True(repaired.Contains("export" + Dash + "docx", StringComparison.Ordinal),
            "The repair rewrote punctuation inside a fenced block, so a multi-line example no longer says " +
            $"what the model wrote. Got: {repaired}");
        Assert.Contains("and it saves, then exports.", repaired, StringComparison.Ordinal);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// A GUIDE ID IS NEVER REWRITTEN. Ids are ASCII slugs written with HYPHEN-MINUS
    /// (<c>chapter-editing-passes</c>) and this repair consumes only U+2014, so the protection is by
    /// construction - which is worth pinning, because an id reaches the prose whenever the citation
    /// parser refuses a line and leaves it in place. The input glues a dash straight onto the id so a
    /// repair that widened to "any dash" would show up here.
    /// </summary>
    [Fact]
    public void AGuideIdKeepsItsHyphens_EvenWithAnEmDashGluedToIt()
    {
        var text = "See the chapter-editing-passes guide" + Dash + "it covers the whole sequence.";
        Assert.Contains(Dash, text, StringComparison.Ordinal);   // floor

        var (repaired, count) = ProductChatPunctuation.Repair(text);

        Assert.Equal(1, count);
        Assert.Contains("chapter-editing-passes", repaired, StringComparison.Ordinal);
        Assert.Equal("See the chapter-editing-passes guide, it covers the whole sequence.", repaired);
    }

    /// <summary>
    /// THE EN-DASH IS DELIBERATELY LEFT ALONE, and this test is the record of that decision. The
    /// convention names the em-dash, both live runs measured zero en-dashes, and the en-dash's ordinary
    /// job is a RANGE: turning "chapters 3-8" into a comma would convert a span into a list, which is
    /// content damage rather than a style fix. If a future measurement finds en-dash leaks, widen the
    /// rule deliberately and delete this test; do not let it happen as a side effect.
    /// </summary>
    [Fact]
    public void AnEnDashIsLeftAlone_BecauseItsOrdinaryJobIsARange()
    {
        var text = "Review chapters 3" + EnDash + "8 first.";
        Assert.Contains(EnDash, text, StringComparison.Ordinal);   // floor

        var (repaired, count) = ProductChatPunctuation.Repair(text);

        Assert.Equal(text, repaired);
        Assert.Equal(0, count);
    }

    /// <summary>An answer with no em-dash is returned byte for byte, and reports nothing.</summary>
    [Fact]
    public void AnAnswerWithNoEmDash_IsUntouched_AndReportsZero()
    {
        const string text = "Export writes a DOCX. Save first, then export.";

        var (repaired, count) = ProductChatPunctuation.Repair(text);

        Assert.Same(text, repaired);   // not merely equal: nothing was rebuilt
        Assert.Equal(0, count);
    }

    // ─── The shape of the rewrite ───────────────────────────────────────────────────────────────

    /// <summary>
    /// BOUNDED, AND SHRINKING EVERYWHERE BUT THE GLUED CASE. A silent rewrite that can grow text is
    /// how a repair turns a cosmetic defect into a structural one, so the movement is pinned per
    /// shape: a spaced dash gives a character back, a dash opening or ending a line is dropped
    /// outright, and only the glued case costs one character.
    /// </summary>
    [Theory]
    [InlineData("it works " + Dash + " most of the time", "it works, most of the time")]      // spaced: shrinks
    [InlineData("it works" + Dash + " most of the time", "it works, most of the time")]       // right space: level
    [InlineData("it works " + Dash + "most of the time", "it works, most of the time")]       // left space: level
    [InlineData("it works" + Dash + "most of the time", "it works, most of the time")]        // glued: +1
    [InlineData(Dash + " a bullet point", "a bullet point")]                                  // opens the line
    [InlineData("a trailing flourish" + Dash, "a trailing flourish")]                         // ends the text
    [InlineData("first line" + Dash + "\nsecond line", "first line\nsecond line")]            // ends the line
    [InlineData("wait for it, " + Dash + " then run", "wait for it, then run")]               // no doubled comma
    [InlineData("stop." + Dash + "then go", "stop. then go")]                                 // no comma after a stop
    [InlineData("two dashes " + Dash + Dash + " here", "two dashes, here")]                   // a run is one break
    public void TheRewriteMovesTheTextByAtMostOneCharacterPerDash(string input, string expected)
    {
        Assert.Contains(Dash, input, StringComparison.Ordinal);   // floor

        var (repaired, count) = ProductChatPunctuation.Repair(input);

        Assert.Equal(expected, repaired);
        Assert.DoesNotContain(Dash, repaired, StringComparison.Ordinal);
        Assert.True(repaired.Length <= input.Length + count,
            $"The repair grew the answer by more than one character per em-dash: {input.Length} -> " +
            $"{repaired.Length} for {count} dash(es). A rewrite that can expand user-visible text without " +
            "bound is the failure shape this guard exists to refuse.");
    }

    // ─── Observability ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IT SAYS WHEN IT FIRES. A layer that silently edits model output is exactly the fail-safe shape
    /// this codebase has shipped failures through before: if a prompt or corpus change starts producing
    /// em-dashes at scale, this line is the only thing that would say so. The COUNT is logged and the
    /// answer text is not, matching the surrounding rule that the user's own words never reach a log.
    /// </summary>
    [Fact]
    public async Task WhenTheRepairFires_ItLogsTheCount_AndNotTheAnswerText()
    {
        const string secret = "zxqvfluffernutter";
        var modelOutput = "Export writes a " + secret + Dash + "and then stops.";
        Assert.Contains(Dash, modelOutput, StringComparison.Ordinal);   // floor

        var svc = Service(AnsweringRouter(new List<AiRequest>(), modelOutput), out var logger);

        var result = await svc.AnswerAsync(new ProductChatRequest("How do I export my book to Word?"), CancellationToken.None);

        Assert.DoesNotContain(Dash, result.Answer, StringComparison.Ordinal);

        var warnings = logger.AtLeast(LogLevel.Warning);
        Assert.Contains(warnings, m => m.Contains("REPAIRED the answer punctuation", StringComparison.Ordinal)
                                       && m.Contains("replaced 1 em-dash", StringComparison.Ordinal));

        var entries = logger.AtLeast(LogLevel.Trace);
        Assert.NotEmpty(entries);   // the population, so the sweep below cannot be vacuous
        Assert.All(entries, e => Assert.DoesNotContain(secret, e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AND IT STAYS QUIET OTHERWISE. A repair that logged on every answer would be noise, and noise is
    /// how the one line that matters gets filtered out. Paired with the test above so "it logs" and "it
    /// only logs when it fired" are both held.
    /// </summary>
    [Fact]
    public async Task WhenThereIsNothingToRepair_NothingIsLoggedAboutIt()
    {
        var svc = Service(AnsweringRouter(new List<AiRequest>(), "Export writes a DOCX file."), out var logger);

        var result = await svc.AnswerAsync(new ProductChatRequest("How do I export my book to Word?"), CancellationToken.None);

        Assert.True(result.IsGrounded);
        Assert.All(logger.AtLeast(LogLevel.Trace),
            e => Assert.DoesNotContain("REPAIRED the answer punctuation", e, StringComparison.Ordinal));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static ProductChatService Service(Mock<IAiRouter> router, out CapturingLogger<ProductChatService> logger)
        => ProductChatBudgetTests.Service(router, out logger, guidesDirectory: null, aiOptions: null);

    private static Mock<IAiRouter> AnsweringRouter(List<AiRequest> captured, string content)
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .Callback<AiRequest, CancellationToken>((req, _) => captured.Add(req))
              .ReturnsAsync(new AiResponse { Content = content, Provider = "test", Model = "test-model" });
        return router;
    }
}
