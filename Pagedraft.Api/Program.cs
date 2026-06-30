using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;
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
builder.Services.AddSingleton<SuggestionDiffService>();
builder.Services.AddScoped<BookIntelligenceService>();
builder.Services.AddSingleton<AnalysisProgressTracker>();
builder.Services.AddScoped<IAnalysisContextService, AnalysisContextService>();
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

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
// Hebrew house-style toggles (e.g. ktiv-male enforcement). Default ON; bound from "Ai:HebrewStyle".
builder.Services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(
    builder.Configuration.GetSection(Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions.SectionName));
builder.Services.AddSingleton<Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker>();
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

// Register AI providers by name for IAiRouter
builder.Services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
{
    var dict = new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
    {
        ["Ollama"] = new OllamaProvider(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        ["OpenAI"] = new OpenAiProvider(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        ["Azure"] = new AzureOpenAiProvider(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        ["Anthropic"] = new AnthropicProvider(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        ["OpenRouter"] = new OpenAiCompatibleProvider("OpenRouter", sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IOptions<AiOptions>>())
    };
    return dict;
});
builder.Services.AddSingleton<IAiRouter, AiRouter>();

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
