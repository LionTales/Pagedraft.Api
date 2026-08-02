using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
/// THE IP BOUNDARY ON THE TIER PAYLOAD (tier-ux-rework c2). Model identity - the provider, the model id, a
/// version - is internal, it changes without notice, and until c2 the tier DTO shipped it per task in a
/// <c>routes</c> array that the client rendered verbatim. Deleting the client's rendering was not the fix:
/// the payload is what a browser holds, what a proxy logs and what the next consumer reads, so the strings
/// have to be ABSENT FROM THE WIRE.
///
/// THIS TEST SERIALIZES, IT DOES NOT READ THE CLASS. A field can be added to the record, or inherited, or
/// projected in by a future <c>Select</c>, and a reviewer reading <c>BookAiTierDto</c> would still see a clean
/// shape - so the assertion runs the DTO through the same <c>JsonSerializerDefaults.Web</c> serializer
/// ASP.NET Core uses and searches the resulting TEXT.
///
/// THE FORBIDDEN LIST IS READ FROM THE SHIPPED CONFIGURATION, NOT HARDCODED. A hardcoded list of today's
/// model ids would go stale the first time somebody swapped a model, and would then pass while leaking the
/// new one. Every <c>Ai:DefaultProvider</c> / <c>Ai:DefaultModel</c> / <c>Ai:FeatureModels:*</c> /
/// <c>Ai:Providers:*</c> identity string in appsettings.json is forbidden, whatever it is today, PLUS a fixed
/// vendor-substring list so a provider that is not in this file's config yet still cannot appear.
///
/// NO FIELD IS EXCLUDED FROM THE SEARCH. The consent copy is CLIENT-side (the client is he/en bilingual and
/// owns the localized wording); the server sends the <c>consentRequired</c> boolean and no prose at all, so
/// there is no legitimate place for a vendor name to appear and no exclusion to argue about. Since be-c03 the
/// payload carries no routing-derived value whatsoever - the per-task <c>processingLocation</c> token was the
/// last one, and dropping it narrowed the surface this class has to police rather than the policing itself.
///
/// Named *AiTier* so the standing deterministic filter picks the file up. In the tier environment collection
/// because the readiness evaluation consults the process-wide <c>AI_{PROVIDER}_APIKEY</c> variables.
/// </summary>
[Collection(AiTierEnvironmentCollection.Name)]
public class AiTierDtoDeidentificationTests
{
    /// <summary>
    /// Vendor substrings that must never appear in the payload even if no CURRENT config value contains them.
    /// The config-derived list above cannot see a provider this deployment has not wired yet; this one closes
    /// the "we added Anthropic routing and the surface started printing it" hole in advance. Matched
    /// case-insensitively.
    /// </summary>
    private static readonly string[] VendorSubstrings =
        { "gemma", "ollama", "openrouter", "gpt", "claude", "qwen", "dicta", "anthropic", "openai", "azure" };

    /// <summary>
    /// Property NAMES that would announce a re-leak even before a value did. Checked separately from the
    /// values because a future field could be null or empty on THIS book's config and still be the wrong
    /// contract - <c>"model": null</c> passes a value search and fails the design.
    /// </summary>
    private static readonly string[] ForbiddenPropertyNameFragments =
        { "provider", "model", "version", "route" };

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-deid-{Guid.NewGuid()}").Options);

    private static BooksController Controller(AppDbContext db, AiTierStatusService tierStatus)
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

    private static BookAiTierDto Ok(ActionResult<BookAiTierDto> result) =>
        Assert.IsType<BookAiTierDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    /// <summary>
    /// Every identity string the SHIPPED appsettings.json names, de-duplicated. Short values are dropped
    /// (nothing shorter than three characters is a meaningful model identifier, and a two-character token
    /// would collide with unrelated JSON by accident).
    /// </summary>
    internal static IReadOnlyList<string> ConfiguredIdentityStrings()
    {
        var config = ProviderTuningConfigParityTests.LoadShippedConfiguration();
        var found = new List<string?>
        {
            config["Ai:DefaultProvider"],
            config["Ai:DefaultModel"]
        };

        foreach (var feature in config.GetSection("Ai:FeatureModels").GetChildren())
        {
            found.Add(feature["Provider"]);
            found.Add(feature["Model"]);
        }

        foreach (var provider in config.GetSection("Ai:Providers").GetChildren())
        {
            found.Add(provider.Key);              // the provider NAME itself, e.g. "OpenRouter"
            found.Add(provider["Model"]);
            found.Add(provider["DefaultModel"]);
            found.Add(provider["DeploymentName"]);
        }

        var identities = found
            .Where(v => !string.IsNullOrWhiteSpace(v) && v!.Trim().Length >= 3)
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(identities.Count >= 5,
            "The de-identification test read fewer than five provider/model strings out of the shipped " +
            "appsettings.json, so it is almost certainly asserting against an empty list and would pass " +
            "against ANY payload. Check Ai:DefaultProvider / Ai:FeatureModels / Ai:Providers.");

        return identities;
    }

    /// <summary>Every DTO shape the endpoint can return for a book, serialized, on the SHIPPED config.</summary>
    private static async Task<List<(string Label, string Json)>> ShippedPayloadsAsync()
    {
        var service = AiTierStatusServiceTests.Service(ProviderTuningConfigParityTests.LoadShippedAiOptions());
        var payloads = new List<(string, string)>();

        foreach (var language in new[] { "he", "en" })
        foreach (var bookDefault in new string?[] { null, "fast", "thinking" })
        {
            using var db = NewDb();
            var book = new Book
            {
                // Fixed id per case so a random GUID's hex can never accidentally contain (or dodge) a
                // forbidden substring and make this test flaky in either direction.
                Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Title = "Deid Book",
                Language = language,
                AiTier = bookDefault
            };
            db.Books.Add(book);
            await db.SaveChangesAsync();

            var controller = Controller(db, service);
            payloads.Add((
                $"GET language={language} default={bookDefault ?? "<null>"}",
                JsonSerializer.Serialize(Ok(await controller.GetAiTier(book.Id, CancellationToken.None)), WireOptions)));

            // ... and with an explicit per-task override on every task the surface offers, since a per-task
            // opt-in is the state that actually routes to the cloud and therefore the state most likely to
            // have wanted to print "which model".
            foreach (var task in AiTierPolicy.UserFacingTasks)
            {
                var put = await controller.UpdateAiTier(
                    book.Id, new UpdateBookAiTierRequest("thinking", task.ToString()), CancellationToken.None);
                if (put.Result is OkObjectResult ok)
                    payloads.Add((
                        $"PUT thinking task={task} language={language}",
                        JsonSerializer.Serialize((BookAiTierDto)ok.Value!, WireOptions)));
            }

            payloads.Add((
                $"GET after per-task opt-ins language={language} default={bookDefault ?? "<null>"}",
                JsonSerializer.Serialize(Ok(await controller.GetAiTier(book.Id, CancellationToken.None)), WireOptions)));
        }

        return payloads;
    }

    /// <summary>
    /// THE CONTRACT TEST THE TODO ASKS FOR: serialize the tier DTO for a seeded book against the SHIPPED
    /// configuration and assert that not one configured provider or model string survives into the JSON.
    /// Because the forbidden list is read from the same file the server routes with, changing a model cannot
    /// silently defeat it - the new value is forbidden the moment it is configured.
    /// </summary>
    [Fact]
    public async Task TheTierPayload_NamesNoConfiguredProviderOrModel()
    {
        var forbidden = ConfiguredIdentityStrings();
        var payloads = await ShippedPayloadsAsync();

        Assert.NotEmpty(payloads);
        foreach (var (label, json) in payloads)
        foreach (var identity in forbidden)
            Assert.False(
                json.Contains(identity, StringComparison.OrdinalIgnoreCase),
                $"The tier payload leaked the configured identity string \"{identity}\" ({label}). Model " +
                $"identity is internal IP and must not reach the client at all. Payload: {json}");
    }

    /// <summary>
    /// The same assertion against a FIXED vendor list, so a provider this deployment has not wired yet is
    /// forbidden in advance rather than the moment somebody notices. Case-insensitive substrings, which is
    /// deliberately stricter than whole-value equality: "google/gemma-4-31b-it" must fail on "gemma" alone.
    /// </summary>
    [Fact]
    public async Task TheTierPayload_ContainsNoVendorSubstring()
    {
        foreach (var (label, json) in await ShippedPayloadsAsync())
        foreach (var vendor in VendorSubstrings)
            Assert.False(
                json.Contains(vendor, StringComparison.OrdinalIgnoreCase),
                $"The tier payload contains the vendor substring \"{vendor}\" ({label}). Nothing on this " +
                $"payload may name a provider, a model or a version. Payload: {json}");
    }

    /// <summary>
    /// NOT VACUOUS. The two assertions above would also pass against an EMPTY object, so this pins that the
    /// payload still carries the facts the toggle needs: the book default, deployment readiness, the consent
    /// flag, and per user-facing task a stored/effective tier, a readiness token and a fallback flag. A future
    /// "fix" that satisfied the leak tests by deleting fields turns this red.
    ///
    /// be-c03 removed <c>processingLocation</c> from this list DELIBERATELY and by the opposite argument to
    /// the one this test defends against: it was not a fact the toggle needs, it was a fact nothing read, kept
    /// under a claim (the consent copy needs it) that was false - the copy is a client-side constant and the
    /// token described the task's CURRENT tier, not the tier consent is about. The value-shape assertion it
    /// carried moved to <c>effectiveTier</c> rather than being dropped, so this class still proves that a task
    /// row's tokens are the contract's own words and not a routing string in disguise.
    /// </summary>
    [Fact]
    public async Task TheTierPayload_StillCarriesEverythingTheToggleNeeds()
    {
        using var db = NewDb();
        var book = new Book { Title = "T", Language = "he", AiTier = "thinking" };
        db.Books.Add(book);
        await db.SaveChangesAsync();

        var service = AiTierStatusServiceTests.Service(ProviderTuningConfigParityTests.LoadShippedAiOptions());
        var json = JsonSerializer.Serialize(
            Ok(await Controller(db, service).GetAiTier(book.Id, CancellationToken.None)), WireOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var expected in new[] { "bookId", "tier", "thinkingReadiness", "fallbackActive", "consentRequired", "tasks" })
            Assert.True(root.TryGetProperty(expected, out _), $"The tier payload lost the '{expected}' field. Payload: {json}");

        var tasks = root.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(AiTierPolicy.UserFacingTasks.Count, tasks.Count);
        foreach (var task in tasks)
        foreach (var expected in new[] { "task", "storedTier", "effectiveTier", "thinkingReadiness", "fallbackActive" })
            Assert.True(task.TryGetProperty(expected, out _), $"A task entry lost the '{expected}' field. Payload: {json}");

        // Every tier token really is one of the two words, not a routing string in disguise.
        Assert.All(tasks, t => Assert.Contains(
            t.GetProperty("effectiveTier").GetString(),
            new[] { AiTierPolicy.FastStoredValue, AiTierPolicy.ThinkingStoredValue }));
    }

    /// <summary>
    /// THE FIELD THAT DOES NOT EXIST YET. The value searches cannot catch a new field whose value happens to
    /// be null or empty on this config, so the property NAMES are checked structurally too: nothing anywhere
    /// in the payload may be called provider, model, version or route. This is the assertion that makes
    /// "a future field cannot re-leak it" true rather than hopeful.
    /// </summary>
    [Fact]
    public async Task NoPropertyInTheTierPayload_IsNamedAfterAModelOrProvider()
    {
        foreach (var (label, json) in await ShippedPayloadsAsync())
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in PropertyNames(doc.RootElement))
            foreach (var fragment in ForbiddenPropertyNameFragments)
                Assert.False(
                    name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"The tier payload has a property named \"{name}\", which announces model/provider " +
                    $"identity even if its value is empty today ({label}).");
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in PropertyNames(property.Value)) yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                foreach (var nested in PropertyNames(item)) yield return nested;
                break;
        }
    }

    /// <summary>
    /// THE GUARD ON THE GUARD. If the searches above were broken - a typo in the comparison, an empty payload
    /// list - they would pass silently forever. So the SAME search is run over a payload that deliberately
    /// contains a real configured model id, and must FAIL to find nothing. This is the difference between
    /// "no leak" and "no test".
    /// </summary>
    [Fact]
    public void TheLeakSearch_ActuallyDetectsALeak()
    {
        var forbidden = ConfiguredIdentityStrings();
        var leaky = JsonSerializer.Serialize(new { bookId = Guid.Empty, model = forbidden[0] }, WireOptions);

        Assert.Contains(forbidden, identity => leaky.Contains(identity, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(PropertyNames(JsonDocument.Parse(leaky).RootElement),
            name => ForbiddenPropertyNameFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// THE CONSENT FLAG (tier-ux-rework c2), which is a RENDERING instruction and nothing more.
///
/// The consent step exists to tell an author that an unpublished manuscript is about to leave this machine,
/// and whether that sentence is true is a property of the DEPLOYMENT: in dev the fast tier is local and the
/// thinking tier is a third-party cloud provider, so the step is the whole point; in a hosted deployment both
/// tiers already run off-machine and the step would ask for consent to something that already happened.
/// Hence config, surfaced on the DTO, never hardcoded on either side.
///
/// Named *AiTier* so the standing deterministic filter picks the file up.
/// </summary>
[Collection(AiTierEnvironmentCollection.Name)]
public class AiTierConsentFlagTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-consent-{Guid.NewGuid()}").Options);

    private static AiTierStatusService ServiceWithConsent(bool? consentRequired)
    {
        var opt = AiTierStatusServiceTests.ShippedShape();
        if (consentRequired.HasValue) opt.Tier = new AiTierOptions { ConsentRequired = consentRequired.Value };
        return AiTierStatusServiceTests.Service(opt);
    }

    private static BooksController Controller(AppDbContext db, AiTierStatusService tierStatus)
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

    private static async Task<Guid> SeedBookAsync(AppDbContext db)
    {
        var book = new Book { Title = "T", Language = "he" };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    /// <summary>The flag reaches the DTO from configuration, BOTH ways - a test that only pinned "true"
    /// would pass against a hardcoded constant.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheConsentFlag_SurfacesFromConfiguration(bool configured)
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var controller = Controller(db, ServiceWithConsent(configured));

        var get = Assert.IsType<BookAiTierDto>(
            Assert.IsType<OkObjectResult>((await controller.GetAiTier(bookId, CancellationToken.None)).Result).Value);
        Assert.Equal(configured, get.ConsentRequired);

        // The write path answers with the same read model, so a client that only ever sees the PUT response
        // is not told a different thing from one that re-GETs.
        var put = Assert.IsType<BookAiTierDto>(
            Assert.IsType<OkObjectResult>(
                (await controller.UpdateAiTier(bookId, new UpdateBookAiTierRequest("fast"), CancellationToken.None)).Result).Value);
        Assert.Equal(configured, put.ConsentRequired);
    }

    /// <summary>
    /// An ABSENT <c>Ai:Tier</c> block defaults to TRUE, i.e. to MORE disclosure. Same safe-direction rule as
    /// <c>AiTierPolicy.Parse</c> degrading an unrecognised tier to fast: a missing value must not quietly
    /// remove the step that tells an author their manuscript is leaving the machine.
    /// </summary>
    [Fact]
    public async Task AnAbsentTierBlock_DefaultsToRequiringConsent()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var dto = Assert.IsType<BookAiTierDto>(
            Assert.IsType<OkObjectResult>(
                (await Controller(db, ServiceWithConsent(null)).GetAiTier(bookId, CancellationToken.None)).Result).Value);

        Assert.True(dto.ConsentRequired);
        Assert.True(new AiOptions().Tier.ConsentRequired);
    }

    /// <summary>
    /// CONSENT IS A UI STEP, NOT AN AUTHORIZATION GATE. With the flag OFF - the production shape - the
    /// server's 409 on a "thinking" request it cannot route is completely unchanged. If this ever went green
    /// the other way, a client could obtain an unroutable tier by simply not rendering a dialog.
    /// </summary>
    [Fact]
    public async Task WithConsentNotRequired_TheServerStill409sAnUnroutableThinkingRequest()
    {
        using var _ = new ClearedApiKeyEnvironment();
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var opt = AiTierStatusServiceTests.ShippedShape();
        opt.Tier = new AiTierOptions { ConsentRequired = false };
        var noKeys = AiTierStatusServiceTests.Service(opt, config: AiTierStatusServiceTests.Config());

        var result = await Controller(db, noKeys).UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("providerCredentialsMissing", JsonSerializer.Serialize(conflict.Value));
        Assert.Null((await db.Books.FindAsync(bookId))!.AiTier);

        // ... and the per-task 409 is equally unaffected.
        var perTask = await Controller(db, noKeys).UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", nameof(AiTaskType.LineEdit)), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(perTask.Result);
    }
}

/// <summary>
/// THE TWO SHIPPED VALUES OF THE CONSENT FLAG, BOUND FROM THE REAL FILES. appsettings.Production.json
/// REPLACES an object block rather than merging into it, so "inherit the base file's true" is not a thing
/// that happens - a Production file that omits the key falls through to the class default (also true) and
/// silently re-enables a step production does not want. Both values are therefore written out, and this test
/// is what notices if one of them stops being.
///
/// Named *AiTier* so the standing deterministic filter picks the file up.
/// </summary>
public class AiTierConsentFlagConfigParityTests
{
    private static IConfigurationRoot Load(string fileName)
        => new ConfigurationBuilder()
            .AddJsonFile(FindUpward(Path.Combine("Pagedraft.Api", fileName)), optional: false)
            .Build();

    [Fact]
    public void TheBaseFile_RequiresConsent_BecauseTheFastTierIsLocalInDev()
    {
        var raw = Load("appsettings.json")["Ai:Tier:ConsentRequired"];
        Assert.False(string.IsNullOrWhiteSpace(raw),
            "Ai:Tier:ConsentRequired is missing from appsettings.json. It must be written out: dev's fast " +
            "tier is local, so opting a task into thinking is the moment an unpublished manuscript first " +
            "leaves the machine and the user has to be told.");
        Assert.True(bool.Parse(raw!));
    }

    [Fact]
    public void TheProductionFile_DoesNotRequireConsent_AndSaysSoEXPLICITLY()
    {
        var raw = Load("appsettings.Production.json")["Ai:Tier:ConsentRequired"];
        Assert.False(string.IsNullOrWhiteSpace(raw),
            "Ai:Tier:ConsentRequired is missing from appsettings.Production.json. It cannot be inherited: an " +
            "object block in that file REPLACES the base block rather than merging with it, so an omitted " +
            "key falls through to AiTierOptions' class default (true) and re-enables a consent step for a " +
            "deployment where both tiers already run off-machine.");
        Assert.False(bool.Parse(raw!));
    }

    /// <summary>Each file binds to the value its own raw key states, through the real AiOptions binder.</summary>
    [Theory]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Production.json", false)]
    public void TheBoundOptions_MatchTheShippedFile(string fileName, bool expected)
    {
        var opt = new AiOptions();
        Load(fileName).GetSection(AiOptions.SectionName).Bind(opt);
        Assert.Equal(expected, opt.Tier.ConsentRequired);
    }

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
