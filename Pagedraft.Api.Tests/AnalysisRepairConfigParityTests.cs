using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Guards the documented parity contract between <c>Pagedraft.Api/appsettings.json</c> and
/// <c>Pagedraft.Api/appsettings.Production.json</c> for <c>Ai:AnalysisRepair:PerType</c>
/// (docs/ANALYSIS_OUTPUT_REPAIR.md §4.1; the Production block comment says this block "MIRRORS base
/// appsettings.json"). Nothing at runtime enforces that mirror - Production.json fully overrides
/// (not merges with) the base file's Ai:AnalysisRepair block, so an edit to one PerType map that
/// forgets the other silently drifts the per-analysis-type repair gate between environments. This
/// test loads both files independently (mirroring the FindUpward + AddJsonFile pattern used by
/// LanguageEngine/AnalysisRepairSmokeTests.cs) and asserts the two PerType maps are equal - same
/// keys, same bool values.
/// </summary>
public class AnalysisRepairConfigParityTests
{
    [Fact]
    public void PerType_BaseAndProduction_AreEqual()
    {
        var basePath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var prodPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.Production.json"));

        var basePerType = LoadPerType(basePath);
        var prodPerType = LoadPerType(prodPath);

        Assert.True(basePerType is { Count: > 0 },
            $"appsettings.json Ai:AnalysisRepair:PerType was null or empty ({basePath}).");
        Assert.True(prodPerType is { Count: > 0 },
            $"appsettings.Production.json Ai:AnalysisRepair:PerType was null or empty ({prodPath}).");

        var baseKeys = basePerType!.Keys.ToHashSet(StringComparer.Ordinal);
        var prodKeys = prodPerType!.Keys.ToHashSet(StringComparer.Ordinal);

        var onlyInBase = baseKeys.Except(prodKeys).ToList();
        var onlyInProd = prodKeys.Except(baseKeys).ToList();
        Assert.True(onlyInBase.Count == 0 && onlyInProd.Count == 0,
            "Ai:AnalysisRepair:PerType key sets differ between appsettings.json and appsettings.Production.json. " +
            $"Only in appsettings.json: [{string.Join(", ", onlyInBase)}]. " +
            $"Only in appsettings.Production.json: [{string.Join(", ", onlyInProd)}].");

        var mismatched = baseKeys
            .Where(key => basePerType![key] != prodPerType![key])
            .Select(key => $"{key}: base={basePerType![key]} prod={prodPerType![key]}")
            .ToList();
        Assert.True(mismatched.Count == 0,
            "Ai:AnalysisRepair:PerType values differ between appsettings.json and appsettings.Production.json " +
            "for the following key(s), breaking the documented mirror (docs/ANALYSIS_OUTPUT_REPAIR.md §4.1): " +
            string.Join("; ", mismatched));
    }

    private static Dictionary<string, bool>? LoadPerType(string path)
    {
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        return config.GetSection("Ai:AnalysisRepair:PerType").Get<Dictionary<string, bool>>();
    }

    // Mirrors LanguageEngine/AnalysisRepairSmokeTests.FindUpward: walks up from the test assembly's
    // output directory to locate the API project's appsettings files.
    private static string FindUpward(string relativeSubPath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeSubPath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate " + relativeSubPath + " above " + AppContext.BaseDirectory);
    }
}
