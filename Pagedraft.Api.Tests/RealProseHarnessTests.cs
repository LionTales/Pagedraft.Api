using System;
using System.Linq;
using System.Threading.Tasks;
using Pagedraft.Api.Services.Ai;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL, NO-GPU gate on the real-prose HARNESS - the half of the surface that is
/// behaviour rather than shape. <c>RealProsePrecisionFixtureTests</c> proves the passages and seeds are
/// what they claim to be; this file proves that driving them through the production entry point does
/// what it claims to do.
///
/// The two are separate files because they fail for different reasons and a reader chasing one should
/// not have to read the other: a failure here means the pipeline or the arm wiring moved, a failure
/// there means the corpus moved.
/// </summary>
public class RealProseHarnessTests
{
    /// <summary>
    /// AN OFFLINE END-TO-END RUN of the harness on one passage, per arm, with the router REPLAYING the
    /// chunk unchanged. It proves five things no static assertion can:
    ///  - the production path really takes the CHUNKED route for this passage;
    ///  - one model call is made per chunk, and each is resolved to its own chunk BY IDENTITY;
    ///  - the arm's text is present in EVERY per-chunk instruction under the arm, and in NONE under Off;
    ///  - a model that changes nothing yields ZERO suggestions - so the whitespace-only tripwire is at
    ///    zero on an untouched round trip and a real run's tripwire count means what it says;
    ///  - the recall detector reports the seeded defects as MISSED when the model changes nothing,
    ///    which is what makes a non-zero recall column attributable to the model.
    /// </summary>
    [Fact]
    public async Task TheHarness_DrivesTheChunkedPath_RendersTheArmPerChunk_AndIsSilentOnAnUntouchedRoundTrip()
    {
        var passage = RealProsePrecisionFixtures.ById(RealProsePrecisionFixtures.NarrationNoQuotesId);

        foreach (var arm in Enum.GetValues<ProofreadPromptArm>())
        foreach (var variant in passage.Variants)
        {
            var run = await RealProseHarness.RunAsync(passage, variant, arm);

            Assert.True(run.RanChunked,
                $"{arm}/{variant}: the production path did NOT take the chunked route, so no per-chunk " +
                "instruction was composed and this surface measured the single-shot regime");
            Assert.Empty(run.Failures);
            Assert.Equal(passage.ExpectedChunkCount, run.Chunks.Count);
            Assert.Equal(passage.ExpectedChunkCount, run.Calls.Count);
            Assert.Equal(
                Enumerable.Range(0, passage.ExpectedChunkCount),
                run.Calls.Select(c => c.ChunkIndex).OrderBy(i => i));

            // The arm renders (or does not) in EVERY per-chunk instruction, including chunk 0 - the
            // line it extends is conditional PROSE in the body, not a conditional section.
            Assert.True(run.ArmRenderedInEveryCall,
                $"{arm}/{variant}: the arm's expected prompt state does not hold on every call");
            foreach (var call in run.Calls)
            {
                var hasArm = call.Instruction.Contains(
                    PromptFactory.OverlapReferentLicenceHe, StringComparison.Ordinal);
                Assert.Equal(arm == ProofreadPromptArm.OverlapReferentLicence, hasArm);
            }

            // Chunk 1 carried a real [CONTEXT_BEFORE] section into the composed prompt.
            var second = run.Calls.Single(c => c.ChunkIndex == 1);
            Assert.False(string.IsNullOrWhiteSpace(second.OverlapPrefix));
            Assert.Null(run.Calls.Single(c => c.ChunkIndex == 0).OverlapPrefix);

            // An untouched round trip proposes nothing at all - neither substantive nor whitespace-only.
            Assert.Empty(run.SubstantiveSuggestions);
            Assert.Empty(run.WhitespaceOnlySuggestions);
            Assert.Equal(0, run.EditCount);
            Assert.Equal(0, run.EditComposition.Values.Sum());

            // ...and the recall detector calls every seeded defect MISSED, because it is still there.
            if (variant == RealProseVariant.Seeded)
            {
                Assert.Empty(run.RepairedSeeds);
                Assert.Equal(passage.Seeds.Count, run.MissedSeeds.Count);
            }
            else
            {
                Assert.Empty(run.RepairedSeeds);
                Assert.Empty(run.MissedSeeds);
            }
        }
    }

    /// <summary>
    /// THE RECALL COLUMN IS REACHABLE. A replay that applies every seed's repair must score full recall
    /// on the seeded run - otherwise the recall denominator could be right while the numerator is
    /// permanently zero, and a "recall held" verdict would be unfalsifiable.
    ///
    /// This is the counterpart of the untouched round trip above: together they pin BOTH ends of the
    /// recall detector on the real pipeline (chunker, per-chunk merge and response sanitizer included),
    /// not just on the span arithmetic the fixture test already covers.
    /// </summary>
    [Fact]
    public async Task AReplayThatRepairsEverySeed_ScoresFullRecall_ThroughTheRealMergePath()
    {
        foreach (var passage in RealProsePrecisionFixtures.Seeded)
        {
            var run = await RealProseHarness.RunAsync(
                passage,
                RealProseVariant.Seeded,
                ProofreadPromptArm.Off,
                replay: capture => passage.Seeds.Aggregate(
                    capture.WrappedInputText,
                    (text, seed) => text.Replace(seed.SeededSpan, seed.CleanSpan, StringComparison.Ordinal)));

            Assert.Empty(run.Failures);
            Assert.Equal(passage.Seeds.Count, run.RepairedSeeds.Count);
            Assert.Empty(run.MissedSeeds);
            Assert.NotEmpty(run.SubstantiveSuggestions);
        }
    }

    /// <summary>
    /// THE EDIT-COMPOSITION BUCKETS ARE A PARTITION AND THEY DISCRIMINATE. Caveat 2 of the artifact
    /// analysis: the arm does not only REMOVE edits, it also introduces a new spurious family, and a NET
    /// total hides that completely because a removal and an addition cancel. So the report is bucketed -
    /// which is only worth anything if the buckets actually separate the shapes they name.
    ///
    /// Every case below is a shape observed in (or predicted for) this corpus, and each must land in its
    /// own bucket. A single table-driven test rather than seven, because what is being asserted is that
    /// the classification is a FUNCTION with these values, not seven independent facts.
    /// </summary>
    [Theory]
    [InlineData("\"שלום\"", "״שלום״", RealProseEditBucket.QuoteNormalization)]
    [InlineData("הלכתי, ואז", "הלכתי ואז", RealProseEditBucket.PunctuationOnly)]
    [InlineData("מסמיקה .", "מסמיקה.", RealProseEditBucket.PunctuationOnly)]
    [InlineData("עצמה", "עוצמה", RealProseEditBucket.SingleWordSubstitution)]
    [InlineData("הוא הלך", "הוא הלך מהר", RealProseEditBucket.WordInsertion)]
    [InlineData("הוא הלך מהר", "הוא הלך", RealProseEditBucket.WordDeletion)]
    [InlineData("הוא הלך מהר", "היא רצה לאט", RealProseEditBucket.MultiWordRewrite)]
    [InlineData("  ", "\n", RealProseEditBucket.WhitespaceOnly)]
    public void TheEditBuckets_SeparateTheShapesTheyName(
        string original, string suggested, RealProseEditBucket expected) =>
        Assert.Equal(expected, RealProseHarness.BucketOf(original, suggested));

    /// <summary>
    /// ...and the bucket set is a TOTAL function: no pair of texts falls outside it. Asserted over every
    /// pairing of a small but varied text set, so "the buckets partition the edits" is checked rather
    /// than asserted in a comment.
    /// </summary>
    [Fact]
    public void TheEditBuckets_ClassifyEveryPairOfTexts_WithNoGaps()
    {
        string[] texts =
        {
            "", "   ", "שלום", "שלום.", "\"שלום\"", "״שלום״", "שלום עולם", "שלום עולם גדול",
            "עולם שלום", "מסמיקה .", "מסמיקה.", "אניאשליך", "אני אשליך"
        };

        var declared = Enum.GetValues<RealProseEditBucket>().ToHashSet();
        var classified = 0;
        foreach (var a in texts)
        foreach (var b in texts)
        {
            var bucket = RealProseHarness.BucketOf(a, b);
            Assert.Contains(bucket, declared);
            classified++;
        }

        Assert.Equal(texts.Length * texts.Length, classified);
        Assert.True(classified > 0, "no pair was classified, so this test proved nothing");
    }
}
