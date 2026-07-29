using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC (no model, no GPU, no network) shape tests for
/// <c>TestData/linguistic-gold.json</c>, the gold set behind
/// <see cref="Pagedraft.Api.Tests.LanguageEngine.LinguisticQualityTests"/>.
///
/// LIVES AT THE TEST PROJECT ROOT ON PURPOSE, not next to the gold's bake-off harness. The
/// <c>Pagedraft.Api.Tests.LanguageEngine</c> namespace is the one the standing deterministic filter
/// EXCLUDES (`FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine`), because it holds the live-GPU
/// harnesses. While this class sat in that namespace it ran in NEITHER standing filter, so the GA gate
/// below could not fire. Do not move it back, and do not rename the class without adding the new name
/// to the standing narrow filter (`FullyQualifiedName~LinguisticGoldSchema`).
///
/// Why this class exists: the gold is a hand-authored data file that no compiler checks. A malformed
/// case does not fail the bake-off — it silently changes the DENOMINATOR of every metric
/// (plantedRecall / typeAccuracy divide by the planted-case count, cleanFpRate by the clean-case
/// count), so a bad edit reads as a model quality change. These tests pin the invariants the scorer
/// assumes.
///
/// The HEBREW VALIDATION invariant is the load-bearing one. Every Hebrew entry in the gold is an
/// AI-authored draft pending native-speaker review, which is a standing GA gate. Before 2026-07-29
/// that caveat lived only in prose (the file's _README plus each entry's `notes`), so a new Hebrew
/// case could be added without it and would then be indistinguishable from a validated one. p2-1
/// added the machine-readable <c>hebrewValidationStatus</c> field and this test enforces it.
/// </summary>
public class LinguisticGoldSchemaTests
{
    /// <summary>An AI-authored Hebrew case that has NOT been reviewed by a native speaker.</summary>
    private const string DraftStatus = "ai-authored-draft-pending-native-review";

    /// <summary>
    /// The only Hebrew case a native speaker has actually signed off (clean-he-04, a real passage from
    /// src/docs/test-text.txt, validated by the user on 2026-06-17). Adding a value here means someone
    /// really reviewed the Hebrew.
    /// </summary>
    private const string ValidatedStatus = "user-validated-2026-06-17";

    private static readonly string[] AllowedValidationStatuses = { DraftStatus, ValidatedStatus };

    /// <summary>The consistency types the shipped LinguisticAnalysis prompt is allowed to emit.</summary>
    private static readonly string[] AllowedConsistencyTypes = { "register", "tense", "pov" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Gold_EveryCase_HasAUniqueIdAndNonEmptyInput()
    {
        var cases = LoadCases();

        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id), "a gold case has no id");
            Assert.False(string.IsNullOrWhiteSpace(c.Input), $"{c.Id}: empty input");
            Assert.False(string.IsNullOrWhiteSpace(c.Language), $"{c.Id}: empty language");
            Assert.False(string.IsNullOrWhiteSpace(c.Notes), $"{c.Id}: empty notes");
        }

        var duplicates = cases.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            "duplicate gold case ids (a duplicate double-counts in every metric): " + string.Join(", ", duplicates));
    }

    [Fact]
    public void Gold_CleanCases_ExpectNoIssues_AndPlantedCases_ExpectAnAllowedType()
    {
        var cases = LoadCases();

        foreach (var c in cases)
        {
            var expected = c.ExpectedConsistencyTypes ?? Array.Empty<string>();

            if (c.ExpectClean)
            {
                // A clean case scores through cleanFalsePositives / maxConsistencyIssues only. Any
                // expected type here would be dead data that reads as an expectation.
                Assert.True(expected.Length == 0,
                    $"{c.Id}: expectClean=true but expectedConsistencyTypes is non-empty");
                Assert.True(c.MaxConsistencyIssues == 0,
                    $"{c.Id}: expectClean=true must allow 0 issues, found maxConsistencyIssues={c.MaxConsistencyIssues}");
            }
            else
            {
                // typeAccuracy is scored as "some returned issue's type is in this set", so an empty
                // set makes the case UNSCOREABLE on type while still counting in the denominator.
                Assert.True(expected.Length > 0,
                    $"{c.Id}: planted case has no expectedConsistencyTypes, so it can never score a type hit");
                foreach (var t in expected)
                {
                    Assert.True(
                        AllowedConsistencyTypes.Contains(t?.Trim(), StringComparer.OrdinalIgnoreCase),
                        $"{c.Id}: expected type '{t}' is not one the shipped prompt can emit " +
                        $"({string.Join("/", AllowedConsistencyTypes)})");
                }
                Assert.True(c.MaxConsistencyIssues > 0, $"{c.Id}: planted case caps issues at 0");
            }
        }
    }

    /// <summary>
    /// THE GA GATE. Every Hebrew case must declare its validation state in a machine-readable field,
    /// and a draft must also say so in its human-readable notes so the two cannot drift apart.
    /// English cases must NOT carry the field — it would imply a Hebrew review that does not apply.
    /// </summary>
    [Fact]
    public void Gold_EveryHebrewCase_DeclaresItsNativeSpeakerValidationState()
    {
        var cases = LoadCases();
        var hebrew = cases.Where(IsHebrew).ToList();

        Assert.True(hebrew.Count > 0, "no Hebrew cases found — this guard would be vacuous");

        foreach (var c in hebrew)
        {
            Assert.True(!string.IsNullOrWhiteSpace(c.HebrewValidationStatus),
                $"{c.Id}: Hebrew case is missing hebrewValidationStatus. Every Hebrew entry is an " +
                $"AI-authored draft until a native speaker reviews it; declare '{DraftStatus}'.");
            Assert.True(AllowedValidationStatuses.Contains(c.HebrewValidationStatus),
                $"{c.Id}: hebrewValidationStatus '{c.HebrewValidationStatus}' is not one of " +
                string.Join(" / ", AllowedValidationStatuses));

            if (c.HebrewValidationStatus == DraftStatus)
            {
                // Dash-agnostic on purpose: the older entries use an em-dash after "HEBREW DRAFT",
                // the newer ones a hyphen.
                Assert.Contains("REQUIRES NATIVE SPEAKER VALIDATION", c.Notes ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var c in cases.Where(x => !IsHebrew(x)))
        {
            Assert.True(string.IsNullOrEmpty(c.HebrewValidationStatus),
                $"{c.Id}: non-Hebrew case carries hebrewValidationStatus='{c.HebrewValidationStatus}'");
        }

        // Non-vacuity: the field must actually be discriminating, not all one value.
        Assert.Contains(hebrew, c => c.HebrewValidationStatus == DraftStatus);
        Assert.Contains(hebrew, c => c.HebrewValidationStatus == ValidatedStatus);
    }

    /// <summary>
    /// The _README note is what a human reads first, so the pending-validation id list in it must match
    /// the machine-readable field. A stale list is how a draft gets presented as validated.
    /// </summary>
    [Fact]
    public void Gold_ReadmeNote_ListsExactlyTheCasesStillPendingNativeValidation()
    {
        var readme = LoadReadme();
        var drafts = LoadCases().Where(c => c.HebrewValidationStatus == DraftStatus).Select(c => c.Id).ToList();

        Assert.True(drafts.Count > 0, "no draft Hebrew cases — this guard would be vacuous");
        foreach (var id in drafts)
        {
            Assert.True(readme.Contains(id, StringComparison.Ordinal),
                $"the _README note does not list '{id}' as pending native-speaker validation");
        }
    }

    /// <summary>
    /// Pins the p2-1 desaturation. The 18-case gold (5 clean / 13 planted) was SATURATED: local
    /// gemma4:12b scored the formula's 0.900 ceiling on it three times running, so it could not show a
    /// better model winning. The clean-case count matters independently: cleanFpRate divides by it, so
    /// shrinking the clean set silently re-weights the false-positive penalty. This is a floor, not an
    /// equality — the gold is expected to keep growing.
    /// </summary>
    [Fact]
    public void Gold_KeepsTheDesaturatedComposition_ClearlyAboveThePreP2_1Size()
    {
        var cases = LoadCases();
        var clean = cases.Count(c => c.ExpectClean);
        var planted = cases.Length - clean;

        Assert.True(cases.Length >= 32,
            $"gold shrank to {cases.Length} cases; p2-1 grew it to 32 to desaturate it (was 18 and at ceiling)");
        Assert.True(clean >= 11, $"clean cases dropped to {clean}; p2-1 raised them to 11 (was 5)");
        Assert.True(planted >= 21, $"planted cases dropped to {planted}; p2-1 raised them to 21 (was 13)");

        // Both languages must carry both kinds, or a per-language conclusion is unsupportable.
        foreach (var hebrewSide in new[] { true, false })
        {
            var side = cases.Where(c => IsHebrew(c) == hebrewSide).ToList();
            var label = hebrewSide ? "Hebrew" : "English";
            Assert.True(side.Any(c => c.ExpectClean), $"no clean {label} cases");
            Assert.True(side.Any(c => !c.ExpectClean), $"no planted {label} cases");
        }
    }

    // ─── Loading ───

    private static bool IsHebrew(GoldCase c)
        => (c.Language ?? string.Empty).StartsWith("he", StringComparison.OrdinalIgnoreCase);

    private static string GoldPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "linguistic-gold.json");
        Assert.True(File.Exists(path), $"linguistic-gold.json not found at {path}");
        return path;
    }

    /// <summary>Real cases only — the first array element is a {_README: ...} note with no id.</summary>
    private static GoldCase[] LoadCases()
    {
        var raw = JsonSerializer.Deserialize<GoldCase[]>(File.ReadAllText(GoldPath()), JsonOptions);
        Assert.NotNull(raw);
        return raw!.Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToArray();
    }

    private static string LoadReadme()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GoldPath()));
        var first = doc.RootElement[0];
        Assert.True(first.TryGetProperty("_README", out var note),
            "the first gold array element must be the {_README: ...} metadata note");
        return note.GetString() ?? string.Empty;
    }

    private sealed class GoldCase
    {
        public string Id { get; set; } = "";
        public string Input { get; set; } = "";
        public string Language { get; set; } = "";
        [JsonPropertyName("hebrewValidationStatus")]
        public string? HebrewValidationStatus { get; set; }
        public bool ExpectClean { get; set; }
        public string[]? ExpectedConsistencyTypes { get; set; }
        public int MaxConsistencyIssues { get; set; }
        public string? Notes { get; set; }
    }
}
