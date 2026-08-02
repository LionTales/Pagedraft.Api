using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Serializes the classes in this file, all of which read (and some of which temporarily CLEAR) the
/// process-wide <c>AI_{PROVIDER}_APIKEY</c> environment variables that
/// <see cref="ProviderCredentials"/> consults.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class AiTierEnvironmentCollection
{
    public const string Name = "AiTierEnvironment";
}

/// <summary>
/// Clears the API-key environment variables for the duration of a test and restores them exactly, so a
/// "credentials missing" assertion measures the CONFIG under test rather than whatever the developer's shell
/// happens to export. Restoring in a finally/Dispose matters: this machine really does carry an OpenRouter
/// key, and leaking a cleared value would break unrelated tests.
/// </summary>
internal sealed class ClearedApiKeyEnvironment : IDisposable
{
    private static readonly string[] Names = { "AI_OPENROUTER_APIKEY", "AI_OPENAI_APIKEY", "AI_AZURE_APIKEY", "AI_ANTHROPIC_APIKEY" };
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

    public ClearedApiKeyEnvironment()
    {
        foreach (var name in Names)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>
/// THE USER-FACING TIER SURFACE (model-tier-fast-thinking plan, p3-4): the read model behind the control,
/// the write path, and - the reason this file exists at all - the guarantee that a book can never advertise
/// a tier it is not actually running on.
///
/// RULE 0 FOR THE TIER is "the model that RAN is the model the UI said would run". The structural half of
/// that is here: <see cref="AiTierStatusService"/> answers the UI through
/// <see cref="LinguisticModelResolver"/>, the same function <see cref="AiRouter"/> resolves through, so the
/// promise and the request cannot be computed differently. These tests pin that they agree for every
/// (task, language, tier) the surface can describe, and pin the three ways the promise can legitimately be
/// "we are NOT on the tier you stored" so that state is rendered rather than swallowed.
///
/// Named *AiTier* so the standing deterministic filter picks the file up.
///
/// COLLECTION. Credential resolution consults the PROCESS environment (<c>AI_OPENROUTER_APIKEY</c>), and a
/// real key IS present on the machine this was written on - the "no key configured" cases have to clear it,
/// which is a process-wide mutation. All three classes in this file share one xUnit collection so they never
/// run in parallel with each other; without that, a test asserting "credentials missing" could observe the
/// key another class had just restored and fail intermittently.
/// </summary>
[Collection(AiTierEnvironmentCollection.Name)]
public class AiTierStatusServiceTests
{
    private const string Local = "Ollama";
    private const string Cloud = "OpenRouter";

    internal static AiOptions Options(params (string Key, string Provider, string Model)[] features)
    {
        var opt = new AiOptions
        {
            DefaultProvider = Local,
            DefaultModel = "default-model",
            FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.Ordinal)
        };
        foreach (var (key, provider, model) in features)
            opt.FeatureModels[key] = new FeatureModelOptions { Provider = provider, Model = model };
        return opt;
    }

    /// <summary>A shipped-shaped config: both tier keys wired, plus the English Proofread key.</summary>
    internal static AiOptions ShippedShape() => Options(
        ("LinguisticAnalysis", Local, "local-linguistic"),
        ("LinguisticAnalysis_thinking", Cloud, "cloud-linguistic"),
        ("Proofread", Local, "local-proofread"),
        ("Proofread_en", Local, "local-proofread-en"),
        ("Proofread_thinking", Cloud, "cloud-proofread"));

    internal static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    /// <summary>A registry containing exactly the named providers. The instances are never called.</summary>
    internal static IReadOnlyDictionary<string, IAiAnalysisProvider> Registry(params string[] names) =>
        names.ToDictionary(n => n, _ => new Mock<IAiAnalysisProvider>().Object, StringComparer.OrdinalIgnoreCase);

    internal static AiTierStatusService Service(
        AiOptions opt,
        IConfiguration? config = null,
        IReadOnlyDictionary<string, IAiAnalysisProvider>? registry = null)
        => new(
            Microsoft.Extensions.Options.Options.Create(opt),
            config ?? Config(("Ai:Providers:OpenRouter:ApiKey", "sk-test")),
            registry ?? Registry(Local, Cloud));

    // ── The promised routes ARE the router's routes ──────────────────────────────────────────────────────

    /// <summary>
    /// THE RULE 0 STRUCTURAL PIN. For every allowlisted task x every language shape x both tiers, the
    /// (provider, model) the surface promises is byte-identical to the one
    /// <see cref="AiRouter.ResolveSelectionForTest"/> hands the provider. If these two ever diverge, the tier
    /// control becomes a claim about what SHOULD run rather than a statement of what WILL, which is the
    /// failure mode the whole todo exists to prevent.
    /// </summary>
    [Fact]
    public void EveryPromisedRoute_EqualsWhatTheRouterWouldActuallyRequest()
    {
        var opt = ShippedShape();
        var svc = Service(opt);
        var languages = new string?[] { "he", "he-IL", "en", "en-US", "EN", "", null, "zz" };

        foreach (var language in languages)
        foreach (var tier in new[] { AiTier.Fast, AiTier.Thinking })
        {
            var status = svc.Describe(tier, language);
            foreach (var route in status.Routes)
            {
                var task = Enum.Parse<AiTaskType>(route.Task);
                var selection = AiRouter.ResolveSelectionForTest(
                    new AiRequest { InputText = "x", TaskType = task, Language = language!, Tier = tier }, opt);
                Assert.True(route.Provider == selection.Provider && route.Model == selection.Model,
                    $"lang={language ?? "<null>"} tier={tier} task={task}: surface promised " +
                    $"{route.Provider}:{route.Model} but the router would request {selection.Provider}:{selection.Model}.");
            }
        }
    }

    /// <summary>The surface describes exactly the allowlisted tasks - never more, never fewer.</summary>
    [Fact]
    public void TheDescribedTasks_AreExactlyTheAllowlist()
    {
        var status = Service(ShippedShape()).Describe(AiTier.Thinking, "he");
        Assert.Equal(
            AiTierPolicy.TieredTasks.Select(t => t.ToString()).OrderBy(s => s, StringComparer.Ordinal),
            status.Routes.Select(r => r.Task).OrderBy(s => s, StringComparer.Ordinal));
    }

    // ── UsesTier: the honest per-task flag ───────────────────────────────────────────────────────────────

    /// <summary>A Hebrew book on the thinking tier: BOTH allowlisted tasks actually move to the cloud.</summary>
    [Fact]
    public void AHebrewBookOnThinking_MovesBothAllowlistedTasks()
    {
        var status = Service(ShippedShape()).Describe(AiTier.Thinking, "he");
        Assert.All(status.Routes, r => Assert.True(r.UsesTier, $"{r.Task} did not move: {r.Provider}:{r.Model}"));
        Assert.All(status.Routes, r => Assert.Equal(Cloud, r.Provider));
        Assert.False(status.FallbackActive);
    }

    /// <summary>
    /// AN ENGLISH BOOK ON THINKING: LinguisticAnalysis moves, Proofread does NOT - <c>Proofread_en</c>
    /// outranks the tier rung (layer E3), which is how p2-4's Proofread_en NO-GO is enforced. The surface
    /// must report that per task rather than paint the whole book "thinking", or the copy would be telling an
    /// English author their proofreading goes to the cloud when it does not.
    /// </summary>
    [Fact]
    public void AnEnglishBookOnThinking_MovesLinguisticAnalysisButNotProofread()
    {
        var status = Service(ShippedShape()).Describe(AiTier.Thinking, "en");

        var linguistic = status.Routes.Single(r => r.Task == nameof(AiTaskType.LinguisticAnalysis));
        Assert.True(linguistic.UsesTier);
        Assert.Equal(Cloud, linguistic.Provider);

        var proofread = status.Routes.Single(r => r.Task == nameof(AiTaskType.Proofread));
        Assert.False(proofread.UsesTier);
        Assert.Equal(Local, proofread.Provider);
        Assert.Equal("local-proofread-en", proofread.Model);

        // At least one route moved, so this is NOT the silent-fallback state.
        Assert.False(status.FallbackActive);
    }

    /// <summary>On the fast tier nothing uses the tier and nothing is falling back - it IS the baseline.</summary>
    [Fact]
    public void AFastBook_UsesNoTierRoute_AndIsNotFallingBack()
    {
        var status = Service(ShippedShape()).Describe(AiTier.Fast, "he");
        Assert.All(status.Routes, r => Assert.False(r.UsesTier));
        Assert.All(status.Routes, r => Assert.Equal(Local, r.Provider));
        Assert.False(status.FallbackActive);
    }

    /// <summary>
    /// THE CASING TRAP (P3-12): a config that spells the bare key's provider "Ollama" and the tier key's
    /// provider "ollama" - both of which the (case-insensitive) provider registry routes IDENTICALLY - must
    /// not be reported as the tier having moved. Same model both sides, so the ONLY difference is casing.
    /// </summary>
    [Fact]
    public void ATierRoute_DifferingOnlyInProviderCasing_DoesNotCountAsUsingTheTier()
    {
        var opt = Options(
            ("LinguisticAnalysis", "Ollama", "local-linguistic"),
            ("LinguisticAnalysis_thinking", "ollama", "local-linguistic"),
            ("Proofread", "Ollama", "local-proofread"),
            ("Proofread_thinking", "ollama", "local-proofread"));
        var status = Service(opt, registry: Registry("Ollama")).Describe(AiTier.Thinking, "he");

        Assert.All(status.Routes, r => Assert.False(r.UsesTier, $"{r.Task} was reported as moved: {r.Provider}:{r.Model}"));
        Assert.True(status.FallbackActive);
        Assert.Equal(AiTierReadiness.RouteNotConfigured, Service(opt, registry: Registry("Ollama")).EvaluateThinkingReadiness("he"));
    }

    // ── The three not-usable states, which must never look the same ──────────────────────────────────────

    /// <summary>
    /// THE KILL-SWITCH STATE, and the one the todo asks about by name: a book stores "thinking" but the tier
    /// keys are gone, so it runs on the LOCAL models. Readiness says why, FallbackActive says it is happening
    /// now, and the routes name the local models the run will really use. Three separate facts, because the
    /// UI has to say all three.
    /// </summary>
    [Fact]
    public void WithTheTierKeysDeleted_AStoredThinkingBook_ReportsRouteNotConfigured_AndAVisibleFallback()
    {
        var opt = Options(
            ("LinguisticAnalysis", Local, "local-linguistic"),
            ("Proofread", Local, "local-proofread"));
        var status = Service(opt).Describe(AiTier.Thinking, "he");

        Assert.Equal(AiTierReadiness.RouteNotConfigured, status.ThinkingReadiness);
        Assert.True(status.FallbackActive);
        Assert.All(status.Routes, r => Assert.False(r.UsesTier));
        Assert.All(status.Routes, r => Assert.Equal(Local, r.Provider));
    }

    /// <summary>
    /// A HALF-CONFIGURED tier entry (Provider set, Model blank) falls through the shared both-non-empty
    /// predicate, so it is indistinguishable from "no key" and must produce the SAME visible fallback rather
    /// than a half-promise.
    /// </summary>
    [Theory]
    [InlineData(Cloud, "")]
    [InlineData("", "cloud-linguistic")]
    public void AHalfConfiguredTierEntry_IsReportedAsNotConfigured(string provider, string model)
    {
        var opt = Options(
            ("LinguisticAnalysis", Local, "local-linguistic"),
            ("Proofread", Local, "local-proofread"));
        opt.FeatureModels!["LinguisticAnalysis_thinking"] = new FeatureModelOptions { Provider = provider, Model = model };
        opt.FeatureModels!["Proofread_thinking"] = new FeatureModelOptions { Provider = provider, Model = model };

        var status = Service(opt).Describe(AiTier.Thinking, "he");
        Assert.Equal(AiTierReadiness.RouteNotConfigured, status.ThinkingReadiness);
        Assert.True(status.FallbackActive);
    }

    /// <summary>
    /// A tier route naming a provider nobody registered. The run would THROW "Unknown AI provider" - loud,
    /// not silent - but the surface must still pre-flight it, and it must NOT be reported as the
    /// falls-back-to-local state, because it does not fall back: it fails.
    /// </summary>
    [Fact]
    public void ATierRouteNamingAnUnregisteredProvider_IsProviderNotRegistered_NotAFallback()
    {
        var opt = Options(
            ("LinguisticAnalysis", Local, "local-linguistic"),
            ("LinguisticAnalysis_thinking", "NoSuchProvider", "some-model"),
            ("Proofread", Local, "local-proofread"));
        var status = Service(opt, registry: Registry(Local, Cloud)).Describe(AiTier.Thinking, "he");

        Assert.Equal(AiTierReadiness.ProviderNotRegistered, status.ThinkingReadiness);
        Assert.False(status.FallbackActive); // a route DID move; it just cannot be served
    }

    /// <summary>
    /// The most likely real-world failure: the keys are right, the provider is registered, and there is no
    /// API key. Reported distinctly from the kill-switch state, because the user's next action differs
    /// (set a key vs. accept the local tier).
    /// </summary>
    [Fact]
    public void ATierRouteWithNoApiKey_IsProviderCredentialsMissing()
    {
        using var _ = new ClearedApiKeyEnvironment();
        var status = Service(ShippedShape(), config: Config()).Describe(AiTier.Thinking, "he");
        Assert.Equal(AiTierReadiness.ProviderCredentialsMissing, status.ThinkingReadiness);
    }

    /// <summary>
    /// THE PLACEHOLDER TRAP, asserted end to end on the surface. <c>appsettings.json</c> commits
    /// <c>"__AI_OPENROUTER_APIKEY__"</c>; it is non-empty, so a naive check would call the tier ready and the
    /// run would 401. <see cref="ProviderCredentials"/> is the single implementation that both this check and
    /// <see cref="OpenAiCompatibleProvider"/> read, which is what keeps the two answers the same.
    /// </summary>
    [Fact]
    public void TheCommittedApiKeyPlaceholder_CountsAsNoKey()
    {
        using var _ = new ClearedApiKeyEnvironment();
        var status = Service(ShippedShape(), config: Config(("Ai:Providers:OpenRouter:ApiKey", "__AI_OPENROUTER_APIKEY__")))
            .Describe(AiTier.Thinking, "he");
        Assert.Equal(AiTierReadiness.ProviderCredentialsMissing, status.ThinkingReadiness);
    }

    /// <summary>The environment-variable rung the provider itself honours is honoured here too.</summary>
    [Fact]
    public void AnEnvironmentVariableApiKey_CountsAsConfigured()
    {
        const string EnvName = "AI_OPENROUTER_APIKEY";
        var previous = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "sk-from-env");
            var status = Service(ShippedShape(), config: Config(("Ai:Providers:OpenRouter:ApiKey", "__AI_OPENROUTER_APIKEY__")))
                .Describe(AiTier.Thinking, "he");
            Assert.Equal(AiTierReadiness.Ready, status.ThinkingReadiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, previous);
        }
    }

    /// <summary>
    /// READINESS IS A PROPERTY OF THE DEPLOYMENT, NOT OF THE BOOK. The control has to enable or disable the
    /// thinking option BEFORE anyone opts in, so the same verdict must come back for a book that is still on
    /// fast.
    /// </summary>
    [Fact]
    public void Readiness_IsTheSame_WhicheverTierTheBookCurrentlyStores()
    {
        var svc = Service(ShippedShape());
        Assert.Equal(
            svc.Describe(AiTier.Fast, "he").ThinkingReadiness,
            svc.Describe(AiTier.Thinking, "he").ThinkingReadiness);
        Assert.Equal(AiTierReadiness.Ready, svc.EvaluateThinkingReadiness("he"));
    }

    /// <summary>
    /// An ENGLISH book still gets a Ready verdict, because LinguisticAnalysis moves for it even though
    /// Proofread does not. Readiness must not be computed from Proofread alone, or English books would be
    /// told the tier is unavailable when half of it is available to them.
    /// </summary>
    [Fact]
    public void AnEnglishBook_IsStillReady_BecauseLinguisticAnalysisMoves()
        => Assert.Equal(AiTierReadiness.Ready, Service(ShippedShape()).EvaluateThinkingReadiness("en"));

    // ── The shipped configuration ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Against the REAL appsettings.json: a Hebrew book on the thinking tier moves BOTH tasks to
    /// OpenRouter/google/gemma-4-31b-it, and an English book keeps its local Proofread. This is the sentence
    /// the shipped UI copy makes, asserted against the shipped config rather than a fixture - so deleting a
    /// tier key (the documented kill-switch) turns it red instead of quietly changing what the UI claims.
    /// </summary>
    [Fact]
    public void TheShippedConfig_MovesBothTasksForHebrew_AndOnlyLinguisticAnalysisForEnglish()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        var svc = Service(opt);

        var hebrew = svc.Describe(AiTier.Thinking, "he");
        Assert.All(hebrew.Routes, r => Assert.True(r.UsesTier, $"{r.Task} did not move on the shipped config."));
        Assert.All(hebrew.Routes, r => Assert.Equal("OpenRouter", r.Provider));
        Assert.All(hebrew.Routes, r => Assert.Equal("google/gemma-4-31b-it", r.Model));

        var english = svc.Describe(AiTier.Thinking, "en");
        Assert.True(english.Routes.Single(r => r.Task == nameof(AiTaskType.LinguisticAnalysis)).UsesTier);
        var englishProofread = english.Routes.Single(r => r.Task == nameof(AiTaskType.Proofread));
        Assert.False(englishProofread.UsesTier);
        Assert.Equal("Ollama", englishProofread.Provider);
    }

    /// <summary>
    /// The fast tier's shipped routes are the LOCAL models, which is what the control's "fast" option claims.
    /// Pinned so a config edit that pointed a bare task key at a cloud provider could not make the "local,
    /// free, private" copy false without a test noticing.
    /// </summary>
    [Fact]
    public void TheShippedFastTier_IsLocalForEveryAllowlistedTask()
    {
        var status = Service(ProviderTuningConfigParityTests.LoadShippedAiOptions()).Describe(AiTier.Fast, "he");
        Assert.All(status.Routes, r => Assert.Equal("Ollama", r.Provider));
    }
}

/// <summary>
/// The BookDto/endpoint half of the surface (p3-4): normalization on read, and the write path's two
/// deliberate rejections.
/// </summary>
[Collection(AiTierEnvironmentCollection.Name)]
public class AiTierControllerTests
{
    private const string Local = "Ollama";
    private const string Cloud = "OpenRouter";

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-surface-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static BooksController NewController(AppDbContext db, AiTierStatusService tierStatus)
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);
        return new BooksController(
            db,
            bookIntelligence: null!,
            styleBaseline: null!,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: null!,
            aiTierStatus: tierStatus,
            scopeFactory: scopeFactory.Object,
            appLifetime: lifetime.Object,
            logger: NullLogger<BooksController>.Instance);
    }

    private static async Task<Guid> SeedBookAsync(AppDbContext db, string language, string? storedTier)
    {
        var book = new Book { Title = "T", Language = language, AiTier = storedTier };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    /// <summary>
    /// THE NORMALIZATION CONTRACT. The column is a nullable free string so a legacy or hand-edited row
    /// degrades instead of throwing; the WIRE value is always exactly "fast" or "thinking", so no client has
    /// to own a second copy of the defensive parse. Every one of these rows must read back as a clean token.
    /// </summary>
    [Theory]
    [InlineData(null, "fast")]
    [InlineData("", "fast")]
    [InlineData("   ", "fast")]
    [InlineData("fast", "fast")]
    [InlineData("thinking", "thinking")]
    [InlineData("Thinking", "thinking")]
    [InlineData("  THINKING  ", "thinking")]
    [InlineData("turbo", "fast")]
    public async Task TheStoredTier_IsNormalizedOnTheWire(string? stored, string expected)
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", stored);
        var controller = NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.ShippedShape()));

        var detail = Assert.IsType<BookDetailDto>(
            Assert.IsType<OkObjectResult>((await controller.GetById(bookId, CancellationToken.None)).Result).Value);
        Assert.Equal(expected, detail.AiTier);

        var list = Assert.IsType<List<BookDto>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None)).Result).Value);
        Assert.Equal(expected, list.Single().AiTier);

        var tier = Assert.IsType<BookAiTierDto>(
            Assert.IsType<OkObjectResult>((await controller.GetAiTier(bookId, CancellationToken.None)).Result).Value);
        Assert.Equal(expected, tier.Tier);
    }

    /// <summary>The happy path: opt in, and the stored column plus the reported routes both move.</summary>
    [Fact]
    public async Task OptingIn_StoresTheTier_AndTheReportedRoutesMove()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", null);
        var controller = NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.ShippedShape()));

        var dto = Assert.IsType<BookAiTierDto>(
            Assert.IsType<OkObjectResult>(
                (await controller.UpdateAiTier(bookId, new UpdateBookAiTierRequest("thinking"), CancellationToken.None)).Result).Value);

        Assert.Equal("thinking", dto.Tier);
        Assert.Equal("ready", dto.ThinkingReadiness);
        Assert.False(dto.FallbackActive);
        // c2: the moved routes are no longer on the wire, so "the routes moved" is now asserted through the
        // per-task facts that replaced them - every allowlisted task resolves thinking, is ready, and is not
        // falling back. (be-c03 dropped the processingLocation token this also checked; effectiveTier is the
        // fact that says the route moved, and readiness is the fact that says it can.)
        foreach (var task in AiTierPolicy.TieredTasks)
        {
            var reported = dto.Tasks.Single(t => t.Task == task.ToString());
            Assert.Equal("thinking", reported.EffectiveTier);
            Assert.False(reported.FallbackActive);
            Assert.Equal("ready", reported.ThinkingReadiness);
        }
        Assert.Equal("thinking", (await db.Books.FindAsync(bookId))!.AiTier);
    }

    /// <summary>Opting back out is always allowed, whatever the deployment's readiness.</summary>
    [Fact]
    public async Task OptingBackToFast_IsAlwaysAllowed_EvenWhenTheTierIsUnavailable()
    {
        using var _ = new ClearedApiKeyEnvironment();
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", "thinking");
        var controller = NewController(db, AiTierStatusServiceTests.Service(
            AiTierStatusServiceTests.ShippedShape(), config: AiTierStatusServiceTests.Config()));

        var dto = Assert.IsType<BookAiTierDto>(
            Assert.IsType<OkObjectResult>(
                (await controller.UpdateAiTier(bookId, new UpdateBookAiTierRequest("fast"), CancellationToken.None)).Result).Value);

        Assert.Equal("fast", dto.Tier);
        Assert.Equal("fast", (await db.Books.FindAsync(bookId))!.AiTier);
    }

    /// <summary>
    /// AN UNRECOGNISED TOKEN IS A 400, NOT A DEFENSIVE PARSE. <c>AiTierPolicy.Parse</c> is fail-safe for
    /// READS - a legacy row must never throw - but a WRITE that silently stored "fast" because the caller
    /// typed "thinkng" would leave the UI and the database disagreeing with nothing anywhere to notice.
    /// </summary>
    [Theory]
    [InlineData("thinkng")]
    [InlineData("cloud")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnUnrecognisedTier_IsRejected_AndDoesNotSilentlyStoreFast(string? requested)
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", "thinking");
        var controller = NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.ShippedShape()));

        var result = await controller.UpdateAiTier(bookId, new UpdateBookAiTierRequest(requested), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("thinking", (await db.Books.FindAsync(bookId))!.AiTier);
    }

    /// <summary>
    /// THE CORE "NEVER SILENTLY" GUARANTEE ON THE WRITE PATH. A deployment where the thinking tier cannot
    /// route must not accept a book being SET to it: the stored value would then advertise a tier that
    /// provably cannot run. The rejection carries the reason so the UI can say which of the three problems
    /// it is.
    /// </summary>
    [Fact]
    public async Task SettingThinking_IsRejected_WhenTheTierCannotRouteOnThisDeployment()
    {
        using var _ = new ClearedApiKeyEnvironment();
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", null);
        var noKeys = AiTierStatusServiceTests.Service(
            AiTierStatusServiceTests.ShippedShape(), config: AiTierStatusServiceTests.Config());
        var controller = NewController(db, noKeys);

        var result = await controller.UpdateAiTier(bookId, new UpdateBookAiTierRequest("thinking"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("providerCredentialsMissing", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        Assert.Null((await db.Books.FindAsync(bookId))!.AiTier);
    }

    /// <summary>
    /// A book ALREADY stored as thinking on a deployment where the keys have since been removed still reads
    /// back, and reads back TELLING THE TRUTH: readiness routeNotConfigured, fallbackActive true, and routes
    /// naming the local models. The kill-switch must degrade visibly for books that already opted in, not
    /// only block new ones.
    /// </summary>
    [Fact]
    public async Task AnAlreadyOptedInBook_ReadsBackAsVisiblyFallingBack_AfterTheKillSwitch()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", "thinking");
        var killSwitched = AiTierStatusServiceTests.Service(AiTierStatusServiceTests.Options(
            ("LinguisticAnalysis", Local, "local-linguistic"),
            ("Proofread", Local, "local-proofread")));
        var controller = NewController(db, killSwitched);

        var dto = Assert.IsType<BookAiTierDto>(
            Assert.IsType<OkObjectResult>((await controller.GetAiTier(bookId, CancellationToken.None)).Result).Value);

        Assert.Equal("thinking", dto.Tier);
        Assert.Equal("routeNotConfigured", dto.ThinkingReadiness);
        Assert.True(dto.FallbackActive);
        // c2: "the routes name the local models" is now the de-identified statement "every allowlisted task
        // is visibly falling back and none of them is actually running thinking". The model NAMES are
        // deliberately gone from the payload (AiTierDtoDeidentificationTests pins their absence); what the
        // user needs - that the stored tier is not what is running - survives.
        foreach (var task in AiTierPolicy.TieredTasks)
        {
            var reported = dto.Tasks.Single(t => t.Task == task.ToString());
            Assert.True(reported.FallbackActive);
            Assert.Equal("fast", reported.EffectiveTier);
        }
    }

    /// <summary>A missing book is a 404 on both verbs rather than a default-tier answer.</summary>
    [Fact]
    public async Task AMissingBook_Is404_OnBothVerbs()
    {
        using var db = NewDb();
        var controller = NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.ShippedShape()));
        Assert.IsType<NotFoundResult>((await controller.GetAiTier(Guid.NewGuid(), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>(
            (await controller.UpdateAiTier(Guid.NewGuid(), new UpdateBookAiTierRequest("fast"), CancellationToken.None)).Result);
    }
}

/// <summary>
/// <see cref="ProviderCredentials"/> is shared by the provider that MAKES the call and the pre-flight that
/// PROMISES it, so its rungs are pinned here rather than being an implementation detail of either.
/// </summary>
[Collection(AiTierEnvironmentCollection.Name)]
public class AiTierProviderCredentialsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        AiTierStatusServiceTests.Config(entries);

    [Fact]
    public void TheProvidersSection_Wins()
        => Assert.Equal("sk-a", ProviderCredentials.ResolveApiKey(Config(("Ai:Providers:OpenRouter:ApiKey", "sk-a")), "OpenRouter"));

    /// <summary>
    /// THE TRAP: the committed <c>__AI_OPENROUTER_APIKEY__</c> marker is non-empty and would otherwise be
    /// sent verbatim as a Bearer token, producing a 401 that reads like a bad key rather than a missing one.
    /// </summary>
    [Theory]
    [InlineData("__AI_OPENROUTER_APIKEY__")]
    [InlineData("__SOMETHING__")]
    public void AnUninterpolatedPlaceholder_CountsAsAbsent(string placeholder)
    {
        using var _ = new ClearedApiKeyEnvironment();
        Assert.Null(ProviderCredentials.ResolveApiKey(Config(("Ai:Providers:OpenRouter:ApiKey", placeholder)), "OpenRouter"));
    }

    [Fact]
    public void TheLegacySection_IsOnlyConsultedWhenAsked()
    {
        using var _ = new ClearedApiKeyEnvironment();
        var config = Config(("Ai:OpenAI:ApiKey", "sk-legacy"));
        Assert.Null(ProviderCredentials.ResolveApiKey(config, "OpenAI"));
        Assert.Equal("sk-legacy", ProviderCredentials.ResolveApiKey(config, "OpenAI", includeLegacySection: true));
    }

    [Fact]
    public void ABlankProviderName_IsNull_RatherThanAnEnvironmentLookupForAI__APIKEY()
        => Assert.Null(ProviderCredentials.ResolveApiKey(Config(), "  "));

    /// <summary>
    /// THE BUG THIS PINS: a key pasted with a trailing newline (a common user-secrets artifact) must resolve
    /// TRIMMED, not verbatim - an untrimmed value sent as a Bearer token fails as a malformed HTTP header
    /// rather than as a bad key, which is a materially worse diagnostic.
    /// </summary>
    [Fact]
    public void AConfigKey_WithTrailingWhitespace_ResolvesTrimmed()
        => Assert.Equal("sk-a", ProviderCredentials.ResolveApiKey(Config(("Ai:Providers:OpenRouter:ApiKey", "sk-a\n")), "OpenRouter"));

    /// <summary>Same normalization on the environment-variable rung, which is a separate return path from config.</summary>
    [Fact]
    public void AnEnvironmentVariableKey_WithSurroundingWhitespace_ResolvesTrimmed()
    {
        using var _ = new ClearedApiKeyEnvironment();
        Environment.SetEnvironmentVariable("AI_OPENROUTER_APIKEY", "  sk-env  ");
        try
        {
            Assert.Equal("sk-env", ProviderCredentials.ResolveApiKey(Config(), "OpenRouter"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_OPENROUTER_APIKEY", null);
        }
    }

    /// <summary>Regression pin: trimming the placeholder rung must not change its "still absent" outcome.</summary>
    [Fact]
    public void ThePlaceholder_StillResolvesNull_AfterTheTrimFix()
    {
        using var _ = new ClearedApiKeyEnvironment();
        Assert.Null(ProviderCredentials.ResolveApiKey(Config(("Ai:Providers:OpenRouter:ApiKey", "__AI_OPENROUTER_APIKEY__")), "OpenRouter"));
    }
}
