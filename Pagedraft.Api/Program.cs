using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Pagedraft.Api.Services.LanguageEngine;
using Pagedraft.Api.Services.LanguageEngine.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Detect;
using Pagedraft.Api.Services.LanguageEngine.Normalize;
using Pagedraft.Api.Services.LanguageEngine.Rewrite;
using Pagedraft.Api.Services.LanguageEngine.Metrics;
using Syncfusion.Licensing;

var builder = WebApplication.CreateBuilder(args);

// TODO: Move this trial key to a secure location (user secrets / env var) before committing.
SyncfusionLicenseProvider.RegisterLicense("Ngo9BigBOggjHTQxAR8/V1JGaF5cXGpCf1FpRmJGdld5fUVHYVZUTXxaS00DNHVRdkdlWX1feHVQRGheUUF+WUtWYEs=");

var dbProvider = builder.Configuration.GetValue<string>("DatabaseProvider") ?? "SqlServer";
if (dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("DatabaseProvider=Sqlite is no longer supported. The current EF Core model and migrations target SQL Server only.");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Connection string 'DefaultConnection' is missing or empty.");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(connectionString, sqlOptions =>
    {
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null);
        sqlOptions.CommandTimeout(60);
    });
});
builder.Services.AddScoped<DocxParserService>();
builder.Services.AddScoped<SfdtConversionService>();
builder.Services.AddScoped<ChapterService>();
builder.Services.AddScoped<SceneService>();
builder.Services.AddScoped<BookAssemblyService>();
builder.Services.AddScoped<AiAnalysisService>();
builder.Services.AddScoped<UnifiedAnalysisService>();
// Value-scoped, fail-safe analysis-output repair (analysis-output-repair plan, p3). Scoped to match its
// consumer UnifiedAnalysisService; depends only on the singleton IAiRouter + a logger, so it has no
// captive-dependency concern. Wired in as the GuardOnly-gated Stage 2 of ApplyAnalysisRepairAsync (off by
// default; GuardOnly=true ships).
builder.Services.AddScoped<AnalysisRepairService>();
// Span-scoped, fail-safe DYNAMIC term repair (dynamic-term-repair-design plan, d4). Scoped like
// AnalysisRepairService (same IAiRouter + logger shape, no captive-dependency concern); consumed by
// UnifiedAnalysisService.ApplyAnalysisRepairAsync and BookReviewService's finalize->persist hook, both
// gated by Ai:AnalysisRepair.Mode (shipped default GlossaryThenDynamic = this service runs after the glossary; Mode=Glossary/Off is the rollback that skips it).
builder.Services.AddScoped<DynamicTermRepairService>();
// Per-book proper-noun list feeding the classifier's bookEntities LEAVE lever (dynamic-term-repair precision
// follow-up, e2). Deterministic (no model/GPU): harvests stored CharacterAnalysis names + a SCRIPT-AWARE
// manuscript scan whose DIRECTION follows the ANALYSIS LANGUAGE the caller passes to GetEntitiesAsync - the
// same value the repair layer resolves the classifier's expected script from, so the script HARVESTED is by
// construction the script the classifier LOOKS UP (be-c03 + final-r02: a Hebrew-expected analysis harvests
// Latin names, a Latin-expected one harvests recurring Hebrew names - and the analysis language is
// caller-overridable, so it is NOT always the book's stored language).
// SINGLETON so its cache persists across analysis requests (slow-changing data); it reads the
// DbContext through a short-lived scope per build (IServiceScopeFactory), so it never captures a scoped
// DbContext. The cache is BOUNDED (owned MemoryCache: size limit + sliding/absolute expiry), keyed per
// (book, direction), and REFRESHED - every producer of a harvest source calls Invalidate(bookId), which drops
// every direction: BookIntelligenceService.BuildBookProfileAsync, UnifiedAnalysisService's persisting seams
// (CharacterAnalysis), and ChapterService's content writes.
// Fail-safe: any fault/missing context -> empty set = current behavior.
builder.Services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
builder.Services.AddSingleton<SuggestionDiffService>();
builder.Services.AddScoped<BookIntelligenceService>();
builder.Services.AddSingleton<AnalysisProgressTracker>();
builder.Services.AddScoped<IAnalysisContextService, AnalysisContextService>();
// Author-editable character register (character-register-editing c1). Scoped: it reads/writes the
// scoped DbContext. Owns the AUTHOR's writes to BookBible.CharacterRegisterJson; the re-extraction
// write stays in AnalysisContextService and goes through CharacterRegisterMerge, so the two writers
// share one merge rule and one serializer configuration.
builder.Services.AddScoped<CharacterRegisterService>();
builder.Services.AddScoped<StyleBaselineService>();
// Structured per-chapter brief builder (wb1-c01). Scoped like StyleBaselineService; the book-wide build
// resolves a fresh instance per chapter from the scope factory so concurrent builds never share a
// non-thread-safe DbContext.
builder.Services.AddScoped<ChapterBriefService>();
// Book-wide L2 summary builder (wb1-c02). Scoped like StyleBaselineService; the book-wide build resolves a
// fresh ChapterBriefService per chapter from the scope factory so concurrent L0 builds never share a
// non-thread-safe DbContext. Aggregates L0 → L1 ChapterBrief → L2 BookBrief and caches the rollup.
builder.Services.AddScoped<BookSummaryService>();
// Whole-book context assembler (wb1-c03). The SINGLE budget-aware path that both the book-scope analysis
// context (AnalysisContextService.ResolveBookAsync) and the book-level analyses (BookIntelligenceService)
// route through, so a large book can no longer silently overflow the model context window. Scoped: it
// reads through the scoped DbContext + BookSummaryService.
builder.Services.AddScoped<BookContextAssembler>();
// Whole-book review orchestrator (wb2-c02). Scoped like BookSummaryService; assembles the budgeted book
// context ONCE via BookContextAssembler, fans out the six per-dimension review prompts through IAiRouter
// with a parallel cap, unions + dedups the findings, and persists BookFinding rows preserving user Status.
builder.Services.AddScoped<BookReviewService>();
// In-progress style-baseline build registry (DEF-2). MUST be singleton: the build runs on a background
// DI scope while later status requests run on their own scopes, and both must share the same map so a
// build started in one tab/session is visible (as BUILDING) after a reload or in a second tab.
builder.Services.AddSingleton<StyleBaselineBuildRegistry>();
// In-progress book-summary build registry — singleton for the SAME reason as StyleBaselineBuildRegistry.
builder.Services.AddSingleton<BookSummaryBuildRegistry>();
// In-progress whole-book review build registry — singleton for the SAME reason. Separate from the summary
// registry so a running summary build never blocks a review build (and vice versa) for the same book.
builder.Services.AddSingleton<BookReviewBuildRegistry>();

// ─── Grounded product chat over the shipped guides (chatbot phase A, c1) ──────────────────────────
// The corpus reader is SINGLETON and caches a successful load: Content/guides ships with the app and
// cannot change while the process runs, so re-reading 15 files per request would be pure waste. It
// resolves the directory from IHostEnvironment.ContentRootPath, i.e. the same on-disk location the
// csproj Content include copies to both the build output and a `dotnet publish` output. A FAULTED
// load is deliberately not cached, so a deployment missing its Content folder recovers the moment it
// is fixed rather than staying broken until a restart.
builder.Services.AddSingleton<GuidesCorpusReader>();
// The chat service itself is stateless (no DbContext, no conversation state - phase A keeps none) so
// it could be a singleton, but it is Scoped to match every other service here and to keep the door
// open for phase B's book-aware context, which will need the scoped DbContext.
builder.Services.AddScoped<ProductChatService>();

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
// Hebrew house-style toggles (e.g. ktiv-male enforcement). Default ON; bound from "Ai:HebrewStyle".
builder.Services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(
    builder.Configuration.GetSection(Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions.SectionName));
builder.Services.AddSingleton<Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker>();
// Per-chunk proofread prompt arms. EVERY switch defaults OFF, so an unconfigured deployment composes
// byte-for-byte the prompt it composed before this section existed; bound from "Ai:ProofreadPrompt".
builder.Services.Configure<ProofreadPromptOptions>(
    builder.Configuration.GetSection(ProofreadPromptOptions.SectionName));
builder.Services.AddSingleton<PromptFactory>();
builder.Services.AddScoped<IEmbeddingService, StubEmbeddingService>();
builder.Services.AddScoped<IEmbeddingStore, StubEmbeddingStore>();

// Ollama timeout is a generous-but-bounded CEILING, not a target: local models can legitimately take
// many minutes on a big unchunked context (e.g. book-scope LinguisticAnalysis at NumCtx=16384), so a
// short timeout fails healthy slow calls; an infinite one would hang the job on a wedged/looping model.
// Configurable via "Ai:Providers:Ollama:TimeoutMinutes" (default 30) so it can be tuned without a rebuild.
builder.Services.AddHttpClient("Ollama", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var timeoutMinutes = config.GetValue<int?>("Ai:Providers:Ollama:TimeoutMinutes") ?? 30;
    if (timeoutMinutes <= 0) timeoutMinutes = 30;
    client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
});
builder.Services.AddHttpClient("LanguageTool", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var serverUrl = config["LanguageEngine:LanguageTool:ServerUrl"] ?? "http://localhost:8081";
    client.BaseAddress = new Uri(serverUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient();

// Register AI providers by name for IAiRouter. The map itself lives in AiProviderRegistry so tests can
// enumerate the registered NAMES (p1-2) — every one of them must carry an output-knob classification in
// ProviderTuningResolver.KnownOutputKnobs, and a DI lambda is not reachable from a test.
builder.Services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
    AiProviderRegistry.Create(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<IOptions<AiOptions>>(),
        sp.GetRequiredService<ILogger<OllamaProvider>>()));
builder.Services.AddSingleton<IAiRouter, AiRouter>();

// The model tier's pre-flight surface (p3-4): resolves each allowlisted task's ACTUAL route through the same
// LinguisticModelResolver the router uses, and reports whether the thinking tier can route at all on this
// deployment. Singleton because it holds no per-request state and reads only IOptions/IConfiguration.
builder.Services.AddSingleton<AiTierStatusService>();

// Language Engine services
builder.Services.Configure<LanguageToolOptions>(builder.Configuration.GetSection(LanguageToolOptions.SectionName));
builder.Services.AddSingleton<ILanguageEngineMetrics, LoggingLanguageEngineMetrics>();
builder.Services.AddSingleton<INormalizeEngine, HebrewNormalizeEngine>();
builder.Services.AddSingleton<IDetectEngine, LanguageToolEngine>();
// Optionally register LLM-based detection as alternative: builder.Services.AddSingleton<IDetectEngine, LlmDetectEngine>();
builder.Services.AddSingleton<IRewriteEngine, LlmRewriteEngine>();
// Optionally register specialized rewrite engines:
// builder.Services.AddSingleton<IRewriteEngine, DictaLmRewriteEngine>();
// builder.Services.AddSingleton<IRewriteEngine, HebrewNemoRewriteEngine>();
builder.Services.AddSingleton<Pagedraft.Api.Services.LanguageEngine.Contracts.ILanguageEngine, LanguageEngine>();

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});
builder.Services.AddControllers();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

app.UseCors();
app.UseHttpsRedirection();
app.MapControllers();
app.MapHub<BookSyncHub>("/hubs/booksync");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("Pagedraft.Api.Startup");
    var dataSource = db.Database.GetDbConnection().DataSource ?? "(unknown)";
    logger.LogInformation("Database: {Path}", dataSource);
    await db.Database.MigrateAsync();
}

app.Run();
