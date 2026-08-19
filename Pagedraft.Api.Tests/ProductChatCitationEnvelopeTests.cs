using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE NEUTRAL DATA ENVELOPE, AND THE PROOF THAT IT DID NOT COST THE READER THEIR CITATION CHIPS (g3b).
///
/// <para>WHAT CHANGED AND WHY. The user message used to open with a <c>[GUIDES]</c> marker and give each
/// document a <c>=== GUIDE id=... ===</c> header. g3 had already deleted the source noun from the PRODUCT
/// route's INSTRUCTIONS on an explicit tested claim - that the instructions, not the data, are what make
/// the answer narrate about its sources - and g3b measured that claim false: narration went 33/102 to
/// 32/102, the product-uncovered cell held at 15 of 16, and the Hebrew answers narrated with
/// <c>המדריכים</c>, a word the rewritten Hebrew block does not contain. The noun the model repeats back
/// is the one the ENVELOPE hands it. See <see cref="ProductChatPrompt.GuidesMarker"/> for the full
/// write-up including the general-route control that isolates the envelope as the carrier.</para>
///
/// <para>WHY THIS FILE EXISTS AT ALL, AND WHY IT IS NOT A STRING ASSERTION. Re-framing the envelope is the
/// easy half. The hard half is that the guide ids inside it are LOAD-BEARING: they are the only place the
/// model can read an id to write into its closing citation line, and that line is the only thing the
/// reader's citation chips are built from. A neutral envelope that silently killed the chips would be a
/// worse defect than the narration it fixed, and reading the code cannot show that it did not, because
/// <see cref="ProductChatCitations"/> never sees this prompt: it parses the MODEL'S output and intersects
/// it with the selection the service passes separately, so the parser's own unit tests would stay green
/// with the ids stripped out of the prompt entirely and the chips dead in production.</para>
///
/// <para>SO THE CENTRAL TEST DRIVES THE WHOLE LOOP WITH A MODEL THAT CAN ONLY SUCCEED BY READING THE
/// ENVELOPE. <see cref="TheChips_SurviveTheNeutralEnvelope_EndToEnd"/> stands a fake router in the model's
/// place that is given NO id ahead of time: it parses the composed instruction for a header, copies the id
/// it finds there, and answers with a citation line naming it. If the envelope stops carrying ids in a
/// readable shape, that model cites nothing, the parser falls back to the full selection, and the test
/// fails on the narrowing rather than passing on a fallback that looks the same from the outside. That is
/// the difference between proving the chips work and proving the fall-back works.</para>
///
/// <para>Pure and offline: a mocked <see cref="IAiRouter"/>, no corpus and no GPU.</para>
/// </summary>
public class ProductChatCitationEnvelopeTests
{
    private static GuideDocument Guide(string id, string lang = "en")
        => new(id, id, "author", "2026-08-02", lang, $"{id}.md", 10, Array.Empty<string>(), $"body of {id}");

    private static readonly IReadOnlyList<GuideDocument> Selection = new[]
    {
        Guide("export"), Guide("faq"), Guide("chapter-editing-passes")
    };

    private static string Compose(string language = "en")
        => ProductChatPrompt.ComposeInstruction(
            language, Selection, Array.Empty<ProductChatTurn>(), route: ChatRoute.Product);

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. THE ENVELOPE NAMES NO DOCUMENT CLASS ────────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The frame around the product corpus carries no noun for a class of document, in either language's
    /// composition. Asserted on the FRAME rather than on the whole instruction, because a guide's own BODY
    /// is product prose that may legitimately use the word (the corpus documents the product's chapter
    /// briefs, for one), and banning it there would be banning the corpus.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void TheEnvelope_NamesNoClassOfDocument(string language)
    {
        var instruction = Compose(language);

        // The frame is the marker line plus every header line: the lines this composer writes, as opposed
        // to the ones the corpus supplies.
        var frame = instruction
            .Split('\n')
            .Where(l => l.StartsWith(ProductChatPrompt.GuidesMarker, StringComparison.Ordinal)
                     || l.StartsWith(ProductChatPrompt.GuideHeaderPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(1 + Selection.Count, frame.Count);

        foreach (var line in frame)
        {
            Assert.DoesNotContain("GUIDE", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("מדריך", line, StringComparison.Ordinal);
            Assert.DoesNotContain("DOCUMENT", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MATERIAL", line, StringComparison.OrdinalIgnoreCase);
        }

        // VACUITY GUARD. The assertions above would also pass on an instruction that carried no guides at
        // all, which is exactly what the General route composes - and then they would be asserting nothing.
        Assert.Contains(ProductChatPrompt.GuidesMarker, instruction, StringComparison.Ordinal);
        Assert.Equal(
            Selection.Count,
            instruction.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// AND EVERY SELECTED ID IS STILL IN THE PROMPT, VERBATIM AND ON ITS OWN HEADER. This is the half of
    /// the change that is allowed to break the reader, so it is pinned separately from the neutrality above:
    /// the two properties pull in opposite directions and a single test asserting both would be satisfiable
    /// by dropping the ids.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void EverySelectedId_IsStillCarried_OnItsOwnHeader(string language)
    {
        var instruction = Compose(language);

        foreach (var guide in Selection)
        {
            Assert.Contains(
                $"{ProductChatPrompt.GuideHeaderPrefix}{guide.Id} lang={guide.Lang} ===",
                instruction, StringComparison.Ordinal);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. THE CHIPS, END TO END, THROUGH A MODEL THAT MUST READ THE ENVELOPE ──────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PROOF. The fake model is handed no id: it reads the id off the SECOND header in the composed
    /// instruction and cites that one, so a citation that arrives at the reader is evidence the envelope
    /// carried a readable id. Citing the second rather than the first also rules out the fall-back
    /// masquerading as a pass - the fall-back returns the whole selection in selection order, which starts
    /// at the first, so an assertion of exactly one id that is NOT the first cannot be satisfied by it.
    /// </summary>
    [Fact]
    public async Task TheChips_SurviveTheNeutralEnvelope_EndToEnd()
    {
        var captured = new List<AiRequest>();
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .Callback<AiRequest, CancellationToken>((req, _) => captured.Add(req))
              .ReturnsAsync((AiRequest req, CancellationToken _) => new AiResponse
              {
                  Content = "Export writes a DOCX.\n\nGuides: " + SecondIdFromEnvelope(req.Instruction),
                  Provider = "test",
                  Model = "test-model",
              });

        var svc = ProductChatBudgetTests.Service(router, out _, guidesDirectory: null, aiOptions: null);

        var result = await svc.AnswerAsync(
            new ProductChatRequest("How do I export my book to Word?"), CancellationToken.None);

        var request = Assert.Single(captured);
        var idsInPrompt = IdsFromEnvelope(request.Instruction);
        Assert.True(idsInPrompt.Count >= 2,
            "The envelope carried fewer than two ids, so this test could not have distinguished a real " +
            "citation from the full-selection fall-back. Prompt was:\n" + request.Instruction);

        // The chip resolved to the ONE id the model read out of the envelope, not to the fall-back set.
        Assert.Equal(new[] { idsInPrompt[1] }, result.GuideIds);
        Assert.NotEqual(idsInPrompt[0], result.GuideIds[0]);

        // And the citation line itself did not reach the reader.
        Assert.Equal("Export writes a DOCX.", result.Answer);
        Assert.DoesNotContain("Guides:", result.Answer, StringComparison.Ordinal);
        Assert.True(result.IsGrounded);
    }

    private static string SecondIdFromEnvelope(string? instruction)
    {
        var ids = IdsFromEnvelope(instruction);
        return ids.Count >= 2 ? ids[1] : "NO-ID-IN-ENVELOPE";
    }

    /// <summary>
    /// Reads the ids back out of the composed prompt the way a model would have to: find the header
    /// prefix, take the token up to the following space. Deliberately NOT a call into the composer.
    /// </summary>
    private static List<string> IdsFromEnvelope(string? instruction)
    {
        var ids = new List<string>();
        if (string.IsNullOrEmpty(instruction)) return ids;

        var at = instruction.IndexOf(ProductChatPrompt.GuideHeaderPrefix, StringComparison.Ordinal);
        while (at >= 0)
        {
            var start = at + ProductChatPrompt.GuideHeaderPrefix.Length;
            var end = instruction.IndexOf(' ', start);
            if (end > start) ids.Add(instruction[start..end]);

            at = instruction.IndexOf(ProductChatPrompt.GuideHeaderPrefix, start, StringComparison.Ordinal);
        }

        return ids;
    }
}
