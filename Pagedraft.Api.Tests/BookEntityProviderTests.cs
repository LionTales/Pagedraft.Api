using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic tests for <see cref="BookEntityProvider"/> (todo e2, extended by be-c03): the per-book
/// proper-noun list that feeds the classifier's <c>bookEntities</c> LEAVE lever. NO model / NO GPU / NO I/O
/// beyond an in-memory EF Core store. Covers the two harvest sources (stored CharacterAnalysis names + a
/// SCRIPT-AWARE manuscript scan — Latin names out of a Hebrew book, recurring Hebrew names out of a Latin
/// book), the over-harvest guards (common words / one-off words / the book's own NATIVE script NOT harvested),
/// case-insensitive membership, the per-source and whole-build fail-safes (malformed JSON / empty book /
/// missing book -> empty set), and the cache REFRESH contract (a non-empty set is cached until a producer
/// invalidates; an EMPTY set is never cached, so names produced after the first call still arrive).
///
/// Class name matches the ~BookEntity test filter used by the plan's deterministic verification.
/// </summary>
public class BookEntityProviderTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── (a) stored analysis names ────────────────────────────────────────────

    [Fact]
    public async Task StoredCharacterAnalysis_CharacterAndRelationshipNames_AreHarvested()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Stored Names", Language = "he" });
        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = SerializeCharacters(
                names: new[] { "Vincent van Gogh", "שרה" },
                rel: ("Vincent van Gogh", "שרה")),
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        // Title-Case Latin name tokens harvested...
        Assert.Contains("Vincent", set);
        Assert.Contains("Gogh", set);
        // ...the Hebrew name (no case) harvested so a foreign Hebrew run in a Latin book can be spared...
        Assert.Contains("שרה", set);
        // ...but the lowercase name PARTICLE "van" is NOT a standalone entity (the classifier handles it).
        Assert.DoesNotContain("van", set);
    }

    [Fact]
    public async Task StoredNames_FromBookProfileCharactersJson_AreHarvested()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Profile Names", Language = "he" });
        db.BookProfiles.Add(new BookProfile
        {
            BookId = bookId,
            Language = "he",
            CharactersJson = SerializeCharacters(names: new[] { "Dolores", "Bernard" }, rel: null),
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        Assert.Contains("Dolores", set);
        Assert.Contains("Bernard", set);
    }

    // ── (b) manuscript scan ──────────────────────────────────────────────────

    [Fact]
    public async Task ManuscriptScan_HarvestsNamesButNotCommonOrOneOffWords()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Manuscript", Language = "he" });

        // "Kafka" recurs across two chapters (mid-sentence); "Berlin" appears mid-sentence in one chapter.
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 0",
            ContentText = "הסופר הנודע Kafka כתב יצירות רבות. הוא התגורר בעיר Berlin תקופה ארוכה.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "פרק 1",
            ContentText = "שוב הופיע Kafka בסיפור והפעם ליד הנהר.",
        });
        // A one-off SENTENCE-INITIAL capitalized word ("Suddenly") + a lowercase Latin leak ("email").
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 2, Title = "פרק 2",
            ContentText = "שלחתי email לחבר. Suddenly he arrived at the door.",
        });
        // "The" recurs SENTENCE-INITIALLY across two chapters — a recurring common word the stop-list must reject.
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 3, Title = "פרק 3",
            ContentText = "The morning came slowly.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 4, Title = "פרק 4",
            ContentText = "The evening was cold.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        // Names harvested: recurring cross-chapter "Kafka" + mid-sentence "Berlin".
        Assert.Contains("Kafka", set);
        Assert.Contains("Berlin", set);

        // Over-harvest guards: lowercase leak, one-off sentence-initial word, and recurring stop-listed word.
        Assert.DoesNotContain("email", set);     // lowercase — not proper-noun-shaped
        Assert.DoesNotContain("Suddenly", set);  // Title-Case but sentence-initial + one chapter only
        Assert.DoesNotContain("The", set);       // recurs, but a stop-listed common word
        Assert.DoesNotContain("he", set);        // lowercase pronoun
        Assert.DoesNotContain("door", set);      // lowercase common word
    }

    // ── (b) manuscript scan — the FOREIGN script is what gets harvested (be-c03) ──────────────

    [Fact]
    public async Task LatinNativeBook_HarvestsRecurringHebrewNames_NotCommonHebrewWords()
    {
        // Bug A (be-c03): the scan used to be LATIN-ONLY, so an ENGLISH book harvested NOTHING from its
        // manuscript — even though Hebrew is the FOREIGN script there, Hebrew has no letter case (so the
        // classifier has no Title-Case / all-caps / name-particle signal at all), and this entity set is
        // therefore the ONLY lever that can spare a legitimate Hebrew run. Now the scan follows the book's
        // language: a Latin-native book harvests the recurring HEBREW tokens.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "The Letters", Language = "en" });

        // "שרה" (a character) and "ירושלים" (a place) recur across two chapters -> names.
        // "אבל" (= "but", a common function word) ALSO recurs across the two chapters -> the stop-list must
        // reject it, because recurrence is the WHOLE gate in the caseless Hebrew direction.
        // "מכתב" (= "letter") appears in ONE chapter only -> no recurrence -> not harvested.
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "Chapter 1",
            ContentText = "Daniel met שרה outside the gates of ירושלים. She said אבל nothing, and handed him a מכתב.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "Chapter 2",
            ContentText = "Years later שרה wrote from ירושלים again, and the note said אבל very little.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "en");

        // The names this book's own prose keeps using — the runs the repair model would otherwise rewrite.
        Assert.Contains("שרה", set);
        Assert.Contains("ירושלים", set);

        // Over-harvest guards: a recurring Hebrew FUNCTION word is stop-listed, a one-off Hebrew word does not
        // clear the recurrence gate. Harvesting either would only spare a leak (cosmetic), but harvesting
        // ordinary prose wholesale would gut the cleaning gate.
        Assert.DoesNotContain("אבל", set);
        Assert.DoesNotContain("מכתב", set);

        // The NATIVE script is not scanned: a Latin token can never be a foreign run in a Latin-native book,
        // so harvesting it would be inert noise (and would eat into the set cap).
        Assert.DoesNotContain("Daniel", set);
    }

    [Fact]
    public async Task HebrewNativeBook_ScanStaysLatin_HebrewProseIsNotHarvested()
    {
        // The non-regression twin of the test above: making the scan script-aware must NOT start harvesting the
        // Hebrew-native book's OWN prose (which is its NATIVE script — never a foreign run — and would flood the
        // set). A Hebrew book keeps harvesting Latin Title-Case names exactly as before.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "ספר עברי", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 0",
            ContentText = "הדמות המרכזית פגשה את Kafka בתחנה. הדמות שתקה.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "פרק 1",
            ContentText = "שוב הופיע Kafka והדמות המרכזית חייכה.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        Assert.Contains("Kafka", set);                 // the FOREIGN script for this book
        Assert.DoesNotContain("הדמות", set);           // recurs across both chapters, but it is the NATIVE script
        Assert.DoesNotContain("המרכזית", set);
    }

    // ── the TWO MATCHING TIERS (be-c04) ───────────────────────────────────────

    [Fact]
    public async Task DeclaredNames_MatchCaseInsensitively()
    {
        // Tier 1. A DECLARED character name is an authoritative proper noun, so EVERY casing of it is the name
        // and every casing must be spared — including a model that writes it lowercase in its analysis prose.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Case", Language = "he" });
        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = SerializeCharacters(names: new[] { "Dolores" }, rel: null),
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        Assert.Contains("Dolores", set);
        Assert.Contains("dolores", set);
        Assert.Contains("DOLORES", set);
    }

    [Fact]
    public async Task ManuscriptTokens_MatchCaseSensitively()
    {
        // Tier 2 (be-c04). This test formerly asserted that a MANUSCRIPT-harvested token matches
        // case-insensitively too — i.e. it PINNED the recall bug as intended behaviour. The only evidence for a
        // harvested token is the CAPITALIZED surface form the scan saw; a vocabulary leak is LOWERCASE by
        // construction. So a harvested "Paris" spares "Paris" (and the sentence-initial "Paris" that classifier
        // rule (7) would not spare), but it must NOT spare "paris" — otherwise one capitalized token anywhere in
        // the manuscript turns off the cleaning gate for that word everywhere.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Case", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 0",
            ContentText = "הדמות פגשה את Paris ליד המזרקה.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "פרק 1",
            ContentText = "שוב חזרה אל Paris באביב.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        Assert.Contains("Paris", set);        // the observed surface form IS spared (bias to LEAVE for names)
        Assert.DoesNotContain("paris", set);  // ...but its lowercase form is NOT
        Assert.DoesNotContain("PARIS", set);
    }

    [Fact]
    public async Task HebrewHarvestDirection_IsUnaffectedByTheCaseSensitiveTier()
    {
        // Hebrew has no letter case, so Ordinal and OrdinalIgnoreCase agree on every Hebrew token: the be-c04
        // tightening is a NO-OP for the Latin-native-book direction be-c03 added, and cannot weaken the ONLY
        // lever that can spare a Hebrew run in an English book. (This is also why the harvest condition could
        // NOT be tightened to `recurs && midSentence`: MidSentenceCount is structurally 0 here, so ANDing would
        // harvest nothing at all.)
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "The Letters", Language = "en" });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "Chapter 1",
            ContentText = "Daniel met שרה outside the gates of ירושלים.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "Chapter 2",
            ContentText = "Years later שרה wrote from ירושלים again.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "en");

        Assert.Contains("שרה", set);
        Assert.Contains("ירושלים", set);
    }

    [Fact]
    public async Task LeakWordCapitalizedOnceInTheManuscript_StillRepairsInAnalysisProse()
    {
        // THE be-c04 REGRESSION TEST. The harvest fires on a SINGLE mid-sentence occurrence, so one English
        // epigraph / quoted line / brand mention anywhere in a Hebrew manuscript harvests its capitalized words.
        // With case-INSENSITIVE membership that spared EVERY LOWERCASE occurrence of those words in the analysis
        // output — measured against the real 80-chapter Hebrew manuscript fixture, ONE added epigraph line
        // ("A story of Confusion and Nostalgia, of Tension without Catharsis.") flipped 3 of the 10 d5 leak seeds
        // (confusion / nostalgia / catharsis) from REPAIR to LEAVE: a 30% recall regression on the exact leak
        // class the dynamic repair exists to clean.
        //
        // This drives the REAL provider into the REAL detector + classifier, the way the shipped repair path does.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "מכשף", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 0",
            // The English epigraph: each Latin word occurs EXACTLY ONCE, mid-sentence, in the whole manuscript.
            ContentText = "הוא ציטט את הפתגם: \"A story of Confusion and Nostalgia, of Tension without Catharsis.\"",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "פרק 1",
            ContentText = "הדמות המשיכה בדרכה אל היער האפל.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var entities = await sut.GetEntitiesAsync(bookId, "he");

        // The single capitalized occurrence still harvests the token (the harvest is unchanged, bias to LEAVE)...
        Assert.Contains("Confusion", entities);
        Assert.Contains("Nostalgia", entities);
        Assert.Contains("Catharsis", entities);

        // ...but it must NOT spare the LOWERCASE leak in the analysis output. The d5 seed values, through the
        // shipped detect -> classify path.
        var seeds = new (string Leak, string Value)[]
        {
            ("confusion", "הדמות הראשית שקעה בתחושת confusion עמוקה כשהתגלתה לה האמת על אביה."),
            ("nostalgia", "הפרק כולו ספוג nostalgia אל ימי הילדות בכפר הגלילי הישן."),
            ("catharsis", "הסצנה האחרונה מביאה את הקורא אל catharsis רגשי משחרר וצלול."),
        };

        foreach (var (leak, value) in seeds)
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
            var toRepair = ForeignRunClassifier.RunsToRepair(runs, value, ExpectedScript.Hebrew, entities);

            Assert.Contains(toRepair, r => string.Equals(r.Text, leak, StringComparison.Ordinal));
        }
    }

    // ── fail-safe ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyBook_NoChaptersNoAnalysis_ReturnsEmptySet()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Empty", Language = "he" });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        Assert.Empty(set);
    }

    [Fact]
    public async Task MissingBook_ReturnsEmptySet()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(Guid.NewGuid(), "he"); // never seeded

        Assert.Empty(set);
    }

    [Fact]
    public async Task GuidEmpty_ReturnsEmptySet()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(Guid.Empty, "he");

        Assert.Empty(set);
    }

    [Fact]
    public async Task MalformedStoredJson_IsSkipped_ManuscriptNamesStillHarvested()
    {
        // A malformed StructuredResult must NOT throw and must NOT prevent the manuscript scan from running —
        // the per-source parse is fail-safe, the whole build still succeeds.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Malformed", Language = "he" });
        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = "{ this is not valid json ]]",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 0",
            ContentText = "הדמות פגשה את Dostoevsky ברחוב.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "פרק 1",
            ContentText = "שוב הופיע Dostoevsky בהמשך.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he"); // must not throw

        Assert.Contains("Dostoevsky", set);
    }

    [Fact]
    public async Task NullStructuredCollections_DoNotThrow()
    {
        // System.Text.Json nulls out a `= new()` collection on an explicit JSON null; the null-guard must keep
        // the harvest from throwing (RepairableFields convention).
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Null Collections", Language = "he" });
        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = "{ \"characters\": null, \"relationships\": null, \"summary\": null }",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he"); // must not throw

        Assert.Empty(set);
    }

    // ── cache + refresh contract (be-c03) ─────────────────────────────────────

    [Fact]
    public async Task NonEmptyResult_IsCachedPerBook_UntilInvalidated()
    {
        // The cache contract for a book that HAS harvest sources: the built set is reused (no rebuild per call)
        // until a producer invalidates it. This test formerly asserted that an EMPTY set stays cached until an
        // explicit Invalidate — i.e. it PINNED bug B as intended behaviour. It now pins the caching of a real
        // (non-empty) set, and the empty-set refresh contract has its own test below.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Cache", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 0",
            ContentText = "הדמות פגשה את Tolstoy בערב.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "פרק 1",
            ContentText = "שוב הופיע Tolstoy למחרת.",
        });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();

        // First build: a real (non-empty) set -> cached.
        var first = await sut.GetEntitiesAsync(bookId, "he");
        Assert.Contains("Tolstoy", first);

        // A NEW name lands in the manuscript AFTER the build (mid-sentence, so one chapter is enough).
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 2, Title = "פרק 2",
            ContentText = "בערב הופיע Chekhov בפתח.",
        });
        await db.SaveChangesAsync();

        // The CACHED set is still served — it is not rebuilt on every call.
        var cached = await sut.GetEntitiesAsync(bookId, "he");
        Assert.Contains("Tolstoy", cached);
        Assert.DoesNotContain("Chekhov", cached);

        // ...until the producer of that source invalidates (ChapterService does this on every content write).
        sut.Invalidate(bookId);
        var rebuilt = await sut.GetEntitiesAsync(bookId, "he");
        Assert.Contains("Tolstoy", rebuilt);
        Assert.Contains("Chekhov", rebuilt);
    }

    [Fact]
    public async Task CharacterAnalysis_ProducedAfterTheFirstCall_IsPickedUpOnTheNextCall()
    {
        // Bug B (be-c03): the ORDINARY production sequence used to defeat the stored-names source outright —
        //   1. a chapter analysis runs on a fresh book -> the set is built and CACHED from the manuscript alone
        //      (no CharacterAnalysis exists yet, so the build is EMPTY... and the empty set was cached);
        //   2. BuildBookProfileAsync LATER produces the very CharacterAnalysis the provider wanted;
        //   3. nothing invalidated -> those character names never entered the set for the whole process lifetime.
        // An EMPTY build is now never cached: it means "no harvest source exists YET", which is exactly the state
        // that is about to change. The next call rebuilds and sees the names — WITHOUT any explicit Invalidate.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Fresh Book", Language = "he" });
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();

        // Step 1-2: the first analysis on a fresh book — nothing to harvest yet.
        var first = await sut.GetEntitiesAsync(bookId, "he");
        Assert.Empty(first);

        // Step 3: the profile build produces the CharacterAnalysis.
        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = SerializeCharacters(names: new[] { "Dolores", "שרה" }, rel: null),
        });
        await db.SaveChangesAsync();

        // Step 4: the NEXT call sees them. No Invalidate call in this test — the empty set was never cached.
        var next = await sut.GetEntitiesAsync(bookId, "he");
        Assert.Contains("Dolores", next);
        Assert.Contains("שרה", next);
    }

    // ── the harvest DIRECTION follows the ANALYSIS language, not Books.Language (final-r02) ───────────
    //
    // THE BUG. The provider used to resolve the harvest direction from Books.Language, while the classifier
    // resolves what IS foreign from the ANALYSIS language — the caller-supplied `language` threaded down
    // ApplyAnalysisRepairAsync -> DynamicTermRepairService. AnalysisController prefers RunAnalysisRequest.Language
    // over the book's stored one, so the two really can differ. When they did (an ENGLISH-language analysis of a
    // HEBREW book), the classifier looked up HEBREW runs while the manuscript tier held only LATIN tokens: the
    // entity lever — the ONLY lever that can spare a Hebrew run in a Latin-expected direction (Hebrew has no case,
    // so no Title-Case / all-caps / name-particle rule can fire) — was SILENTLY INERT, and an undeclared Hebrew
    // name went to the repair model and was rewritten. A corrupted name in persisted analysis prose.
    //
    // The fix: GetEntitiesAsync takes the ANALYSIS language and resolves the direction with the SAME
    // ExpectedScriptForLanguage helper the repair layer uses, so harvest and classify agree BY CONSTRUCTION.

    /// <summary>Seeds ONE Hebrew-native book carrying BOTH a recurring Latin name (Kafka) and recurring Hebrew
    /// names (שרה, ירושלים), so the SAME book has a direction-correct answer in each direction and the two are
    /// distinguishable.</summary>
    private static Guid SeedBidirectionalHebrewBook(AppDbContext db)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "ספר", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 0",
            ContentText = "הסופר Kafka צעד ברחובות ירושלים והביט סביבו. שרה חיכתה לו בכיכר המרכזית.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "פרק 1",
            ContentText = "שוב פגש את Kafka ליד השער. שרה שבה אל ירושלים באביב.",
        });
        return bookId;
    }

    [Fact]
    public async Task HebrewBook_AnalysedInEnglish_HarvestsTheHebrewTokens_NotTheLatinOnes()
    {
        // THE final-r02 REGRESSION TEST. Books.Language = "he", but the analysis runs with language = "en"
        // (RunAnalysisRequest.Language overriding the book's). The classifier will therefore treat HEBREW runs as
        // foreign — so the HEBREW tokens are the ones that must be harvested. Against the un-patched provider
        // (which read Books.Language) this set came back holding "Kafka" and NO Hebrew at all.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = SeedBidirectionalHebrewBook(db);
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "en"); // the ANALYSIS language, not the book's

        // The direction the CLASSIFIER will use is Latin-expected, so the book's own HEBREW names are what needs
        // sparing — and they are exactly what the harvest must produce.
        Assert.Contains("שרה", set);
        Assert.Contains("ירושלים", set);

        // ...and NOT the Latin tokens: in a Latin-expected direction a Latin run is never foreign, so a harvested
        // "Kafka" could not gate anything. Its PRESENCE is the fingerprint of the bug (a Hebrew-direction harvest).
        Assert.DoesNotContain("Kafka", set);

        // END TO END, through the shipped detect -> classify path the repair layer runs: the Hebrew name in the
        // ENGLISH analysis output is SPARED.
        const string englishOutput = "The character שרה travels to ירושלים in the final act of the novel.";
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(englishOutput, ExpectedScript.Latin);
        var toRepair = ForeignRunClassifier.RunsToRepair(runs, englishOutput, ExpectedScript.Latin, set);
        Assert.DoesNotContain(toRepair, r => r.Text is "שרה" or "ירושלים");

        // NEGATIVE CONTROL — this is what the bug DID. Hand the SAME classification the WRONG-direction set (the
        // one the old provider built from Books.Language="he") and both names are handed to the repair model.
        var wrongDirectionSet = await sut.GetEntitiesAsync(bookId, "he");
        var toRepairWrong = ForeignRunClassifier.RunsToRepair(runs, englishOutput, ExpectedScript.Latin, wrongDirectionSet);
        Assert.Contains(toRepairWrong, r => r.Text == "שרה");
        Assert.Contains(toRepairWrong, r => r.Text == "ירושלים");
    }

    [Fact]
    public async Task HebrewBook_AnalysedInItsNativeLanguage_StillHarvestsLatin_NonRegression()
    {
        // The non-regression twin: the ordinary case (the FE always sends the book's own language) is byte-identical
        // to before. A Hebrew-expected analysis harvests the Latin names, and none of the book's own Hebrew prose.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = SeedBidirectionalHebrewBook(db);
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();
        var set = await sut.GetEntitiesAsync(bookId, "he");

        Assert.Contains("Kafka", set);
        Assert.DoesNotContain("שרה", set);      // the EXPECTED script here — never a foreign run, so inert noise
        Assert.DoesNotContain("ירושלים", set);
    }

    [Fact]
    public async Task Cache_IsKeyedPerDirection_TheTwoLanguagesDoNotClobberEachOther()
    {
        // The cache key is (bookId, ExpectedScript), not bookId alone: ONE book, TWO live directions, each served
        // its OWN set. Under the old bookId-only key the direction that built FIRST was served to BOTH — so this
        // interleaving is what would have exposed it.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = SeedBidirectionalHebrewBook(db);
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();

        var hebrewDirection = await sut.GetEntitiesAsync(bookId, "he");   // builds + caches the Latin harvest
        var latinDirection = await sut.GetEntitiesAsync(bookId, "en");    // must BUILD, not serve the entry above

        Assert.Contains("Kafka", hebrewDirection);
        Assert.DoesNotContain("Kafka", latinDirection);
        Assert.Contains("שרה", latinDirection);
        Assert.DoesNotContain("שרה", hebrewDirection);

        // Re-reading either direction still serves ITS OWN set (neither write evicted the other).
        var hebrewAgain = await sut.GetEntitiesAsync(bookId, "he");
        var latinAgain = await sut.GetEntitiesAsync(bookId, "en");
        Assert.Contains("Kafka", hebrewAgain);
        Assert.DoesNotContain("Kafka", latinAgain);
    }

    [Fact]
    public async Task Cache_CollapsesLocaleVariantsOntoTheSameEntry()
    {
        // The cache key is the CANONICAL direction (ExpectedScript), which is what ExpectedScriptForLanguage
        // collapses "en" / "en-US" / "fr" into — so a locale variant cannot land in a second slot and rebuild.
        // (The live preservation fixture really does pass "en-US" / "he-IL" while its books are seeded "en" / "he".)
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = SeedBidirectionalHebrewBook(db);
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();

        var built = await sut.GetEntitiesAsync(bookId, "en");
        Assert.Contains("שרה", built);

        // A new recurring Hebrew name lands in the manuscript AFTER the "en" entry was cached.
        SeedHaifaChapters(db, bookId);
        await db.SaveChangesAsync();

        // "en-US" must hit the SAME entry — i.e. serve the CACHED set, which predates חיפה. A second cache slot
        // would rebuild and pick it up.
        var localeVariant = await sut.GetEntitiesAsync(bookId, "en-US");
        Assert.DoesNotContain("חיפה", localeVariant);

        // Non-vacuity: the rebuild really would see חיפה — so the absence above is the shared cache entry, not a
        // scan that cannot find the token.
        sut.Invalidate(bookId);
        Assert.Contains("חיפה", await sut.GetEntitiesAsync(bookId, "en-US"));
    }

    [Fact]
    public async Task Invalidate_DropsEveryDirectionForTheBook()
    {
        // Invalidate(bookId) is called by producers that change the PROSE / the stored NAMES — which feed BOTH
        // directions. Dropping only one would leave the other serving a set built from the pre-write manuscript.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = SeedBidirectionalHebrewBook(db);
        await db.SaveChangesAsync();

        var sut = provider.GetRequiredService<IBookEntityProvider>();

        // Both directions built and CACHED (both are non-empty, so both really are cache entries).
        Assert.Contains("Kafka", await sut.GetEntitiesAsync(bookId, "he"));
        Assert.Contains("שרה", await sut.GetEntitiesAsync(bookId, "en"));

        // A chapter write adds a new name in EACH script (Chekhov mid-sentence; חיפה recurring across two chapters).
        SeedHaifaChapters(db, bookId);
        await db.SaveChangesAsync();

        // Both are still stale — proving they were both genuinely cached, so the assertion below is not vacuous.
        Assert.DoesNotContain("Chekhov", await sut.GetEntitiesAsync(bookId, "he"));
        Assert.DoesNotContain("חיפה", await sut.GetEntitiesAsync(bookId, "en"));

        sut.Invalidate(bookId);

        // EVERY direction was dropped — not just whichever one the key happened to name.
        Assert.Contains("Chekhov", await sut.GetEntitiesAsync(bookId, "he"));
        Assert.Contains("חיפה", await sut.GetEntitiesAsync(bookId, "en"));
    }

    /// <summary>Adds a new name in BOTH scripts to an existing book: "Chekhov" (Latin, mid-sentence — one
    /// occurrence is enough) and "חיפה" (Hebrew, across TWO chapters — recurrence is the whole gate there).</summary>
    private static void SeedHaifaChapters(AppDbContext db, Guid bookId)
    {
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 2, Title = "פרק 2",
            ContentText = "בערב הופיע Chekhov בפתח הבית אשר בעיר חיפה.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 3, Title = "פרק 3",
            ContentText = "הרכבת יצאה מן העיר חיפה עם שחר.",
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string SerializeCharacters(string[] names, (string a, string b)? rel)
    {
        var result = new CharacterAnalysisResult { Summary = "sum" };
        foreach (var name in names)
        {
            result.Characters.Add(new CharacterEntry { Name = name, Role = "supporting", Description = "d", Arc = "a" });
        }

        if (rel is { } r)
        {
            result.Relationships.Add(new CharacterRelationship { Character1 = r.a, Character2 = r.b, Relationship = "knows" });
        }

        return JsonSerializer.Serialize(result, CamelCase);
    }

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        // Registered exactly as production (singleton reading the DbContext through IServiceScopeFactory).
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
        return services.BuildServiceProvider();
    }
}
