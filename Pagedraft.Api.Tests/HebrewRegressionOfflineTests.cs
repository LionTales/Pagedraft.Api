using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
using HebrewRegressionCase = Pagedraft.Api.Tests.LanguageEngine.HebrewRegressionCase;
using HebrewRegressionTests = Pagedraft.Api.Tests.LanguageEngine.HebrewRegressionTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The GENUINELY OFFLINE half of the Hebrew regression harness, split out of
/// <see cref="Pagedraft.Api.Tests.LanguageEngine.HebrewRegressionTests"/> on 2026-07-29 so it runs in the
/// standing deterministic suite.
///
/// WHY THE SPLIT rather than a move. All four tests in the original class were deterministic in the sense
/// that matters here - no model, no GPU - so the whole class looked like a move candidate. It is not: ONE
/// of them (<c>FullPipeline_NormalizeAndDetect_ReturnsResult_ForAllCases</c>) drives the detect stage,
/// which really calls LanguageTool at <c>localhost:8081</c> and TOLERATES the timeout when it is down.
/// Measured 2026-07-29 with LanguageTool down: that one test costs 41 SECONDS, against 38ms and 43ms for
/// the two below. Moving the class wholesale would have taken the ~5s standing gate to ~46s AND made it
/// quietly environment-sensitive - slower on the machines that lack LanguageTool, which is the common
/// case. So the network-dependent test stays behind in the excluded namespace, marked
/// <c>[Trait("Category", "EnvironmentDependent")]</c>, and only these two come across.
///
/// They call the ORIGINAL class's <c>internal</c> loaders rather than copying them, so this file cannot
/// drift into pinning a look-alike fixture: there is still exactly one <c>hebrew-regression.json</c>
/// reader and one <c>HebrewNormalizeEngine</c> factory.
/// </summary>
public class HebrewRegressionOfflineTests
{
    /// <summary>
    /// The gold file exists at <c>AppContext.BaseDirectory/TestData</c> and deserializes. Pure file read.
    /// </summary>
    [Fact]
    public void LoadHebrewRegressionJson_ExistsAndDeserializes()
    {
        var path = HebrewRegressionTests.GetTestDataPath();
        Assert.True(File.Exists(path), $"Test data file not found: {path}");
        var json = File.ReadAllText(path);
        var cases = JsonSerializer.Deserialize<HebrewRegressionCase[]>(json, HebrewRegressionTests.JsonOptions);
        Assert.NotNull(cases);
        Assert.NotEmpty(cases);
    }

    /// <summary>
    /// The NORMALIZE stage over every case that declares an expected normalization.
    /// <c>HebrewNormalizeEngine</c> is constructed directly and is pure string work - no HTTP client, no
    /// LanguageTool, no router - which is what makes this half safe for the standing gate.
    /// </summary>
    [Fact]
    public async Task NormalizeStage_MatchesExpected_ForCasesWithExpectedNormalized()
    {
        var cases = HebrewRegressionTests.LoadCases();
        var engine = HebrewRegressionTests.CreateNormalizeEngine();

        var asserted = 0;
        foreach (var c in cases)
        {
            if (string.IsNullOrEmpty(c.ExpectedNormalized)) continue;

            var normalized = await engine.NormalizeAsync(c.Input, c.Language);
            Assert.True(
                normalized == c.ExpectedNormalized,
                $"Case {c.Id}: Expected normalized \"{c.ExpectedNormalized}\", got \"{normalized}\".");
            asserted++;
        }

        // Non-vacuity: the original test would pass on an empty or expectation-free gold. If every case
        // loses its expectedNormalized this must say so rather than go quietly green.
        Assert.True(asserted > 0,
            "no case in hebrew-regression.json declares expectedNormalized, so this test asserted nothing.");
    }
}
