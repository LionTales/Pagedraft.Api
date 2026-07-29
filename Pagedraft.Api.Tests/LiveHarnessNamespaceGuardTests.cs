using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE RECURRENCE GUARD for the defect be-c01 found and the 2026-07-29 follow-up fixed.
///
/// THE ROOT CAUSE it closes. Both standing verification filters key on a NAMESPACE:
/// <c>--filter "FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine"</c> excludes that namespace
/// outright, and the standing narrow filter names topics that mostly do not appear in it. The namespace
/// is therefore being used as a PROXY for a property - "this test needs a live model / an external
/// service, and would take 40+ minutes or hang the gate" - that NOTHING enforced. Anyone dropping a
/// deterministic test class into <c>LanguageEngine/</c> got a test that compiles, passes locally under a
/// bare <c>dotnet test</c>, and then never runs again in any standing gate. It is invisible by
/// construction: nothing goes red, a count simply fails to move. Seven classes and three stranded
/// <c>[Fact]</c>s, about 56 assertions, had accumulated that way, including the deterministic half of the
/// shipped proofread path.
///
/// WHAT THIS TEST ASSERTS. Every xUnit test method that remains in the excluded namespace must EITHER
/// declare the property the namespace is standing in for - a <c>[Trait("Category", ...)]</c> from
/// <see cref="LiveCategories"/>, on the method or on its declaring class - OR appear in
/// <see cref="Allowlist"/> with a written justification. A deterministic class added to that folder
/// tomorrow fails THIS test, in the deterministic suite, in milliseconds, naming itself.
///
/// WHY IT IS REFLECTION-DRIVEN. The set of test methods is derived from the ASSEMBLY, never from a
/// hand-authored list of class names. A hand-authored inventory would be the same defect one level up:
/// it would silently fail to mention the next new class, which is precisely how the original hole stayed
/// open. The only hand-written data here is the allowlist of EXCEPTIONS, and
/// <see cref="Allowlist_HasNoStaleEntries"/> keeps even that honest by failing when an entry stops
/// resolving to a real test method.
/// </summary>
public class LiveHarnessNamespaceGuardTests
{
    /// <summary>The namespace the standing deterministic filter excludes.</summary>
    private const string ExcludedNamespace = "Pagedraft.Api.Tests.LanguageEngine";

    /// <summary>The assembly root, where a deterministic test belongs.</summary>
    private const string RootNamespace = "Pagedraft.Api.Tests";

    /// <summary>
    /// The <c>Category</c> trait values that legitimately declare "do not run me in a standing gate".
    /// <c>LiveModel</c> / <c>LiveDiagnostic</c> mean a real model call (Ollama or cloud);
    /// <c>EnvironmentDependent</c> means an external service that is tolerated-when-absent but costs
    /// wall-clock to time out against (LanguageTool at localhost:8081, measured 41s down).
    /// </summary>
    private static readonly HashSet<string> LiveCategories = new(StringComparer.Ordinal)
    {
        "LiveModel",
        "LiveDiagnostic",
        "EnvironmentDependent",
    };

    /// <summary>
    /// Deterministic tests that are ALLOWED to stay in the excluded namespace, each with the reason. Keep
    /// this list short and keep every reason falsifiable: the default answer for a deterministic test is
    /// to MOVE it to the assembly root, not to add a line here. Key is
    /// <c>ClassName.MethodName</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Allowlist =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BookReviewWindowedCoverageTests.LargeBook_ForcesMultipleWindows_NoChapterDropped_PlantedChaptersSeparated"] =
                "Deterministic (pure AssembleWindowsAsync, no IAiRouter) but NOT liftable: it needs its " +
                "declaring class's live-harness DI container (BuildProvider), 48-chapter gold loader " +
                "(LoadLargeBook), DB seeder (SeedLargeBookAsync) and ITestOutputHelper, so moving it would " +
                "drag the live harness's setup to the root instead of leaving it behind. Reached instead by " +
                "the method-scoped term 'FullyQualifiedName~LargeBook_ForcesMultipleWindows' on the standing " +
                "narrow filter. If that term is ever dropped, MOVE the test rather than deleting this entry.",
        };

    /// <summary>
    /// Every test method left in the excluded namespace declares WHY it is excluded, or is a named
    /// exception. This is the guard itself.
    /// </summary>
    [Fact]
    public void EveryTestInTheExcludedNamespace_IsLiveMarkedOrAllowlisted()
    {
        var offenders = ExcludedNamespaceTestMethods()
            .Where(m => !IsLiveMarked(m) && !Allowlist.ContainsKey(Key(m)))
            .Select(Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} test(s) in {ExcludedNamespace} are neither live-marked nor allowlisted, " +
            "which means they run in NEITHER standing verification filter and are therefore dead weight " +
            "that only a forbidden bare `dotnet test` would execute:\n  " +
            string.Join("\n  ", offenders) +
            "\n\nFIX (in order of preference): (1) if the test is DETERMINISTIC, move its class out of " +
            $"the {ExcludedNamespace} namespace to the Pagedraft.Api.Tests assembly root, so the standing " +
            "deterministic suite runs it; (2) if it really needs a live model or an external service, add " +
            $"[Trait(\"Category\", \"...\")] with one of: {string.Join(", ", LiveCategories.OrderBy(c => c, StringComparer.Ordinal))}; " +
            "(3) only if it is deterministic but genuinely cannot be separated from a live harness, add it " +
            "to LiveHarnessNamespaceGuardTests.Allowlist WITH a written reason and a way for it to " +
            "actually run.");
    }

    /// <summary>
    /// The allowlist may not rot. Every entry must still resolve to a real test method in the excluded
    /// namespace, and must carry a non-trivial reason - otherwise a test that was moved or renamed leaves
    /// behind an entry that would silently excuse some FUTURE test that happens to take its name.
    /// </summary>
    [Fact]
    public void Allowlist_HasNoStaleEntries()
    {
        var live = ExcludedNamespaceTestMethods().Select(Key).ToHashSet(StringComparer.Ordinal);

        var stale = Allowlist.Keys.Where(k => !live.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "These allowlist entries no longer resolve to a test method in " + ExcludedNamespace +
            " (moved, renamed or deleted). Remove them, or the entry will silently excuse a future test " +
            "that reuses the name:\n  " + string.Join("\n  ", stale));

        var unjustified = Allowlist.Where(kv => string.IsNullOrWhiteSpace(kv.Value) || kv.Value.Trim().Length < 40)
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(unjustified.Count == 0,
            "These allowlist entries carry no real justification. An exception to a gate has to say why, " +
            "or it is just the gate turned off:\n  " + string.Join("\n  ", unjustified));
    }

    /// <summary>
    /// NON-VACUITY. If the reflection walk ever returns nothing - a namespace rename, a change in how
    /// xUnit attributes are discovered, a folder deleted - the two tests above would pass while asserting
    /// about an empty set, i.e. the guard would be silently disabled in exactly the way it exists to
    /// prevent. This fails instead, loudly.
    /// </summary>
    [Fact]
    public void TheGuard_ActuallySeesTheExcludedNamespace()
    {
        var methods = ExcludedNamespaceTestMethods();
        Assert.True(methods.Count > 0,
            $"The reflection walk found NO xUnit test methods in {ExcludedNamespace}. Either the live " +
            "harnesses moved (in which case retarget or delete this guard deliberately) or the walk broke " +
            "and this guard is now vacuous.");

        Assert.True(methods.Any(IsLiveMarked),
            $"No test in {ExcludedNamespace} carries a live-model trait, which means the trait convention " +
            "this guard enforces is not actually in use and the guard is passing for the wrong reason.");
    }

    /// <summary>
    /// THE GUARD MUST NOT EXCLUDE ITSELF, and neither may anything else that was moved to the root to
    /// escape the exclusion.
    ///
    /// This is not hypothetical: this class was first written as
    /// <c>LanguageEngineNamespaceIsLiveOnlyTests</c>, and its fully-qualified name
    /// <c>Pagedraft.Api.Tests.LanguageEngineNamespaceIsLiveOnlyTests</c> CONTAINS the string
    /// <c>Pagedraft.Api.Tests.LanguageEngine</c> as a prefix. The standing filter is a SUBSTRING match
    /// (<c>!~</c>), not a namespace match, so the guard silently excluded itself and its 3 tests never
    /// ran in the deterministic suite - the very defect it was written to prevent, reproduced by the
    /// prevention. It was caught only because the suite's total came up 3 short of the predicted count.
    ///
    /// So: every test type in the <c>Pagedraft.Api.Tests</c> ROOT namespace must have a fully-qualified
    /// name that the standing exclusion does NOT match. Any root-level class whose name begins with
    /// "LanguageEngine" reintroduces the hole, however deterministic it is.
    /// </summary>
    [Fact]
    public void NoRootLevelTestClass_IsSwallowedByTheSubstringExclusion()
    {
        var swallowed = typeof(LiveHarnessNamespaceGuardTests).Assembly
            .GetTypes()
            .Where(t => string.Equals(t.Namespace, RootNamespace, StringComparison.Ordinal))
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Any(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Any()))
            .Select(t => t.FullName!)
            .Where(fullName => fullName.Contains(ExcludedNamespace, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(swallowed.Count == 0,
            "These test classes live in the ROOT namespace, so they LOOK included in the standing " +
            "deterministic suite, but the standing filter excludes by SUBSTRING " +
            $"(FullyQualifiedName!~{ExcludedNamespace}) and their fully-qualified names contain that " +
            "substring anyway. They run in NEITHER standing filter:\n  " +
            string.Join("\n  ", swallowed) +
            $"\n\nFIX: rename the class so its full name does not start with '{ExcludedNamespace}' " +
            "(e.g. do not begin a root-level test class name with 'LanguageEngine').");
    }

    // ── reflection walk ───────────────────────────────────────────────────────

    /// <summary>
    /// Every <c>[Fact]</c> / <c>[Theory]</c> method declared in the excluded namespace, derived from the
    /// assembly. <c>TheoryAttribute</c> derives from <c>FactAttribute</c>, so one check covers both.
    /// </summary>
    private static IReadOnlyList<MethodInfo> ExcludedNamespaceTestMethods() =>
        typeof(LiveHarnessNamespaceGuardTests).Assembly
            .GetTypes()
            .Where(t => string.Equals(t.Namespace, ExcludedNamespace, StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static
                                          | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Any())
            .ToList();

    private static string Key(MethodInfo m) => $"{m.DeclaringType!.Name}.{m.Name}";

    /// <summary>
    /// True when the method, or the class that declares it, carries a <c>[Trait("Category", X)]</c> with
    /// X in <see cref="LiveCategories"/>. xUnit's <c>TraitAttribute</c> exposes no properties (it is read
    /// by a discoverer), so the name/value pair is read off the attribute's constructor arguments.
    /// </summary>
    private static bool IsLiveMarked(MethodInfo m) =>
        HasLiveTrait(m.GetCustomAttributesData()) || HasLiveTrait(m.DeclaringType!.GetCustomAttributesData());

    private static bool HasLiveTrait(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(a => a.AttributeType == typeof(TraitAttribute) && a.ConstructorArguments.Count == 2)
            .Any(a =>
                string.Equals(a.ConstructorArguments[0].Value as string, "Category", StringComparison.Ordinal) &&
                a.ConstructorArguments[1].Value is string v && LiveCategories.Contains(v));
}
