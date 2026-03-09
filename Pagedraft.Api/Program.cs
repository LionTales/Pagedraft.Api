using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
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
builder.Services.AddScoped<Pagedraft.Api.Services.Analysis.UnifiedAnalysisService>();
builder.Services.AddScoped<Pagedraft.Api.Services.Analysis.BookIntelligenceService>();
builder.Services.AddSingleton<Pagedraft.Api.Services.Analysis.AnalysisProgressTracker>();

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
builder.Services.AddSingleton<PromptFactory>();

builder.Services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(10));
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
        ["Anthropic"] = new AnthropicProvider(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IOptions<AiOptions>>())
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
