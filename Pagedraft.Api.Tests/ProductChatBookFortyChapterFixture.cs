using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE FIXTURE HALF of <see cref="ProductChatBookFortyChapterFixtureTests"/> (f0, chatbot phase B gate
/// fixes): the 40-chapter Hebrew book itself, and the assembly helper that mirrors
/// <c>BookChatContextReader.ReadAsync</c>'s block order using the SAME real, pure static methods it
/// calls. Split into its own file (not a new concern - the same class, `partial`) purely to stay under
/// this codebase's ~700-line file-size guidance; see the test file for the actual [Fact]s and the
/// class-level "why this class exists" doc.
/// </summary>
public partial class ProductChatBookFortyChapterFixtureTests
{
    private const int ChapterCount = 40;
    private const string BookTitle = "צלליות על החוף";

    /// <summary>
    /// THE LANGUAGE OF THIS FIXTURE'S TURN, NAMED ONCE SO ASSEMBLY AND COMPOSITION CANNOT DRIFT APART
    /// (final-r05). <see cref="Assemble"/> renders the blocks and <see cref="ComposeFor"/> selects the
    /// guides and composes the payload; both used to say "he" separately, and the author-facing chapter
    /// name is now written in this value - so two literals would let the budget shapes be measured on a
    /// prompt whose blocks and whose grounding disagreed about the language, which is a prompt the
    /// product never builds. The book is Hebrew and its questions are Hebrew, so retrieval language and
    /// answer language coincide here; the cross-language turn is covered by the unit tests instead.
    /// </summary>
    private const string TurnLanguage = "he";

    private const string Miriam = "מרים כהן";
    private const string Doron = "דורון לוי";
    private const string Yonat = "יונת";
    private const string Sidekick = "שמעון הזקן";
    private const string SuppressedName = "הזקן מהמגדלור";

    // ─── Titles: distinct two-word Hebrew combinations, none of which is a generic structural word,
    // so BookArtifactSelector.MatchesTitle can find every one of them distinctive ──────────────────

    private static readonly string[] Nouns =
        { "האי", "המגדלור", "המכתב", "הסוד", "הסערה", "הנמל", "היומן", "הבור", "השביל", "הפעמון" };
    private static readonly string[] Adjectives = { "הנעלם", "האבוד", "השקט", "הנסתר" };

    private static string TitleFor(int order) =>
        $"{Nouns[order % Nouns.Length]} {Adjectives[(order / Nouns.Length) % Adjectives.Length]}";

    // ─── Content banks. Real Hebrew sentences, not filler characters, so the REAL FormatChapterBrief
    // renders REAL prose and the density measurement means something ──────────────────────────────

    private static readonly string[] PlotEventBank =
    {
        "מרים כהן מגלה מכתב ישן שהוסתר מאחורי אריח רופף בקיר המטבח, וכתב היד עליו דומה מאוד לכתב ידה של אמה המנוחה.",
        "סערה פתאומית מכה בחוף האי ומכריחה את התושבים להתפנות במהירות אל המקלט הישן שליד המגדלור.",
        "דורון לוי מוצא ביומן הישן של הסבא רישום על ספינה שטבעה בשנת אלף תשע מאות שלושים ושתיים ליד השונית הדרומית.",
        "עימות חריף פורץ בין מרים לבין ראש המועצה בנוגע לתוכנית להרוס את בית האבן הישן שעל שפת הנחל.",
        "יונת מגלה בגן הנטוש שביל חבוי המוביל אל מערה קטנה שבה מסתתרים חפצים מהתקופה העות'מאנית.",
        "מכתב אנונימי מגיע לתיבת הדואר של מרים ובו אזהרה מעורפלת שלא להמשיך לחפור בעבר המשפחתי.",
        "דורון ומרים נתקלים באקראי בתחנת האוטובוס הישנה ומחדשים קשר שנקטע לפני כעשר שנים.",
        "פגישת החירום של ועד השכונה מתקיימת בבית הכנסת הישן ובה מתגלה שהתוכנית לפיתוח החוף כבר אושרה בסתר.",
        "מרים מוצאת בארכיון העירייה תעודת לידה שאינה תואמת לסיפור שסופר לה כל חייה על מוצאה.",
        "סופה קשה מטלטלת את המגדלור הישן וחושפת יסודות בטון שאיש לא ידע על קיומם.",
        "יונת ודורון פותחים ביחד תיבה חתומה שנמצאה מתחת לרצפת המחסן, ובתוכה מסמכים בכתב יד עתיק.",
        "עדות בלתי צפויה של שכן זקן חושפת פרט מכריע על הלילה שבו נעלם אביה של מרים.",
    };

    private static readonly (string State, string Arc)[] MiriamBank =
    {
        ("נחושה לגלות את האמת שמאחורי הסוד המשפחתי", "מחוסר אמון לפתיחות זהירה"),
        ("המומה מהתגלית האחרונה ומתקשה לבטוח באיש", "מכעס לתקווה זהירה"),
        ("נסערת אך נחושה להמשיך בחקירה על אף האזהרות", "מפחד לנחישות"),
        ("עדיין חוששת לחשוף את מה שגילתה בארכיון העירייה", "מבדידות להתקרבות מחודשת"),
    };

    private static readonly (string State, string Arc)[] DoronBank =
    {
        ("שוקל לספר למרים על היומן שמצא בעליית הגג", "מהיסוס למחויבות"),
        ("מתלבט אם לחשוף את מה שידע על הספינה הטבועה", "מאשמה לשחרור"),
        ("נזהר מאוד סביב ראש המועצה ותוכניותיו", "מחשד לערנות"),
        ("שמח לחדש את הקשר עם מרים אחרי שנים של ניתוק", "מריחוק לקרבה"),
    };

    private static readonly (string State, string Arc)[] YonatBank =
    {
        ("סקרנית מאוד לגבי המערה שמצאה בגן הנטוש", "מסקרנות להתלהבות"),
        ("נלהבת לעזור לדורון לפענח את התיבה החתומה", "משעשוע לרצינות"),
        ("מרגישה שהיא חלק ממשהו גדול יותר מעצמה", "מבדידות לשייכות"),
    };

    private static readonly (string State, string Arc)[] SidekickBank =
    {
        ("זוכר את הלילה שבו נעלם אביה של מרים אך מסרב לספר", "משתיקה לגילוי חלקי"),
        ("עוקב אחרי האירועים באי מהמרפסת שלו כל ערב", "מאדישות למעורבות"),
        ("חושד שראש המועצה מסתיר משהו על תוכנית הפיתוח", "מחשד לפעולה"),
        ("שומר על קשר קבוע עם דיג הנמל בענייני האי", "מבדידות לחברות"),
    };

    private static readonly string[] ThematicMarkerBank =
    {
        "זהות משפחתית", "אובדן ותקווה", "סודות מהעבר", "אמון שבור", "שייכות למקום",
        "זיכרון קולקטיבי", "מתח דרמטי", "קצב",
    };

    private static readonly string[] OpenThreadBank =
    {
        "מי שלח את המכתב האנונימי למרים עדיין לא ידוע.",
        "התעודה החסרה מהארכיון עלולה לשנות את כל מה שמרים חשבה שהיא יודעת על משפחתה.",
        "הקשר בין הספינה הטבועה לבין המגדלור טרם התבהר.",
        "גורלו של הבית הישן על שפת הנחל תלוי בהצבעת המועצה הקרובה.",
        "התיבה החתומה עדיין מסתירה מסמך אחד שאיש לא הצליח לפענח.",
        "שמעון הזקן יודע משהו על הלילה שבו נעלם אביה של מרים, אך עדיין שותק.",
    };

    private static readonly string[] ToneNoteBank =
    {
        "טון מהורהר ואיטי, עם רגעי מתח קצרים המפריעים את השלווה הכפרית.",
        "אווירה כבדה של ציפייה, מתובלת בהומור עדין בדיאלוגים.",
        "מתח דרמטי הולך וגובר ככל שהעבר חושף את עצמו.",
        "נימה נוסטלגית עם רמזים לאיום מתקרב.",
    };

    // ─── L0 -> L1: the round trip the density trap is about ─────────────────────────────────────

    private static StructuredChunkSummaryData BuildL0(int order)
    {
        // Sized (10 plot events, up to 4 character states, 5 themes, 4 open threads) so the REAL
        // FormatChapterBrief renders ~700-800 tokens/chapter, matching d1's real-DB measurement -
        // verified, not assumed, by TheFixture_HitsRealStructuredDensity_ThroughTheRealFormatChapterBrief.
        var plotStart = (order * 3) % PlotEventBank.Length;
        var plotEvents = Enumerable.Range(0, 10)
            .Select(k => PlotEventBank[(plotStart + k) % PlotEventBank.Length])
            .ToList();

        var states = new List<ChapterCharacterState> { NamedState(Sidekick, SidekickBank[order % SidekickBank.Length]) };
        if (order % 3 == 0) states.Add(NamedState(Miriam, MiriamBank[order % MiriamBank.Length]));
        if (order % 4 == 0) states.Add(NamedState(Doron, DoronBank[order % DoronBank.Length]));
        if (order % 7 == 0) states.Add(NamedState(Yonat, YonatBank[order % YonatBank.Length]));

        var themeStart = (order * 2) % ThematicMarkerBank.Length;
        var themes = Enumerable.Range(0, 5)
            .Select(k => ThematicMarkerBank[(themeStart + k) % ThematicMarkerBank.Length])
            .Distinct()
            .ToList();
        // Every 5th chapter names pacing explicitly, IN THE BOOK'S LANGUAGE, so shape 5's dimension
        // question has real, content-anchored chapters to rank and not just the findings. The Hebrew
        // surface form is the point rather than an incidental choice: this fixture is a Hebrew book, and
        // for the whole of this fixture's first life the marker scored NOTHING, because RankChapterBriefs
        // compared it against the canonical English slug. The claim on this line was false and the shape-5
        // assertion below rested on it. Keep the marker in Hebrew; it is what holds that fix down.
        if (order % 5 == 0 && !themes.Contains("קצב")) themes.Add("קצב");

        var threadStart = (order * 5) % OpenThreadBank.Length;
        var threads = Enumerable.Range(0, 4)
            .Select(k => OpenThreadBank[(threadStart + k) % OpenThreadBank.Length])
            .Distinct()
            .ToList();

        return new StructuredChunkSummaryData
        {
            PlotEvents = plotEvents,
            CharacterStates = states,
            ThematicMarkers = themes,
            ToneNotes = ToneNoteBank[order % ToneNoteBank.Length],
            OpenThreads = threads,
        };
    }

    private static ChapterCharacterState NamedState(string name, (string State, string Arc) sa)
        => new() { Name = name, State = sa.State, EmotionalArc = sa.Arc };

    /// <summary>
    /// Builds all 40 chapter briefs by SERIALIZING <see cref="BuildL0"/> to a JSON string - standing in
    /// for the persisted <c>ChunkSummary.StructuredJson</c> column, since this suite has no database -
    /// and reading it back through the REAL <see cref="StructuredChunkSummaryParser.Parse"/>, then mapping
    /// into <see cref="ChapterBrief"/> the SAME way <c>BookSummaryService.ComposeChapterBriefsAsync</c>
    /// does (line-for-line: <c>Summary</c> stays null, the five structured lists map straight through).
    /// If the density lived anywhere the real parser or the real mapping does not read, this round trip
    /// is exactly where it would go missing.
    /// </summary>
    internal static IReadOnlyList<ChapterBrief> BuildBriefs()
    {
        var briefs = new List<ChapterBrief>();
        for (var order = 0; order < ChapterCount; order++)
        {
            var json = JsonSerializer.Serialize(BuildL0(order));
            var l0 = StructuredChunkSummaryParser.Parse(json);
            Assert.NotNull(l0);   // the round trip through the REAL parser must succeed

            briefs.Add(new ChapterBrief
            {
                Title = TitleFor(order),
                Order = order,
                Summary = null,
                PlotEvents = l0!.PlotEvents,
                CharacterStates = l0.CharacterStates,
                ThematicMarkers = l0.ThematicMarkers,
                ToneNotes = l0.ToneNotes,
                OpenThreads = l0.OpenThreads,
            });
        }

        return briefs;
    }

    // ─── Raw chapter text for the two escalation targets ─────────────────────────────────────────

    private static readonly string[] LongChapterParagraphBank =
    {
        "הרוח נשבה חזק מהים והצליפה בחלונות הבית הישן שעל שפת הנחל, בעוד מרים ישבה ליד השולחן וקראה שוב את " +
        "המכתב שמצאה, מנסה להבין מה בדיוק ניסתה אמה להסתיר ממנה כל השנים הללו.",
        "בסיפון הספינה הטבועה, לפני עשרות שנים, עמד רב החובל והביט אל האופק החשוך, ולא ידע שהשונית הדרומית " +
        "ממתינה לו במים הקרים; זהו הסיפור שדורון קרא ביומן הישן וחזר עליו שוב ושוב בלילה שלפני שסיפר למרים.",
        "האי כולו נראה קטן יותר בעיניה של מרים מדי פעם שחזרה אליו, אך הפעם הוא הרגיש עצום, מלא בפינות שלא " +
        "ביקרה בהן מעולם, ובסודות שהמתינו רק לה שתגלה אותם.",
        "שמעון הזקן ישב על המרפסת שלו כרגיל וצפה בשקיעה, ומדי פעם מלמל לעצמו משפט שאיש לא שמע במלואו על " +
        "הלילה שבו נעלם אביה של מרים, לילה שהוא זוכר טוב יותר משהוא מוכן להודות.",
        "יונת רצה במורד השביל אל הגן הנטוש כדי לספר לדורון על המערה שמצאה, נושמת בכבדות מההתרגשות ולא " +
        "מההליכה, ומקווה שהפעם הוא לא יגיד לה שזה שוב סתם עוד סיפור ילדים.",
        "ראש המועצה עמד מול הקהל הנסער באולם בית הכנסת הישן וניסה להסביר את תוכנית הפיתוח לחוף, אך איש לא " +
        "האמין לו יותר אחרי שהתברר שהאישור כבר ניתן בסתר שבועות קודם לכן.",
        "המגדלור עמד בודד מול הסערה, אורו מהבהב לסירוגין, ומרים חשבה על כל הדורות שקדמו לה שעמדו באותו מקום " +
        "בדיוק והביטו אל אותו הים שאולי בלע את הספינה הטבועה שנה אחת קודם לכן.",
        "התיבה החתומה ששכבה מתחת לרצפת המחסן במשך עשרות שנים נפתחה סוף סוף, ומתוכה עלה ריח של עץ ישן ונייר " +
        "מתפורר, ובתוכה מסמכים שדורון ויונת ידעו מיד שהם חשובים בלי לדעת עדיין למה.",
        "הארכיון העירוני היה שקט וקריר, והפקידה שם על השולחן מול מרים תעודת לידה ישנה שלא תאמה שום דבר " +
        "שסופר לה מעולם, ומרים הרגישה את הרצפה נשמטת מתחתיה.",
        "שמונה עשרה שנה אחרי שנעלם אביה, מרים עדיין הייתה בטוחה שיום אחד היא תדע בדיוק מה קרה, ועכשיו, עם " +
        "כל פיסת מידע חדשה, היא הרגישה שהיא קרובה יותר מתמיד ורחוקה יותר מתמיד באותה נשימה.",
    };

    private static readonly string[] ShortChapterSentenceBank =
    {
        "השנים חלפו והאי המשיך להתקיים כפי שתמיד היה, שקט בבוקר וסוער לעיתים לפנות ערב.",
        "מרים עמדה על המזח האחרון פעם ולוחצת ביד את דורון, ושניהם ידעו שהפרק הזה בחייהם הגיע לסיומו.",
        "שמעון הזקן חייך אליהם מהמרפסת, ולראשונה מזה שנים לא היה נראה כמו מישהו ששומר סוד.",
        "יונת רשמה ביומן שלה את כל מה שקרה, כדי שיום אחד תוכל לספר את הסיפור למישהו אחר.",
        "האור במגדלור המשיך להבהב כל לילה, ואיש באי כבר לא פחד ממנו.",
    };

    private static string BuildLongChapterText()
    {
        var sb = new StringBuilder();
        // Sized to comfortably exceed the 3,500-token escalation slice ALONE (d1 measured a real max
        // chapter at ~14,006 tokens; this targets the same order of magnitude so the excerpt path is
        // exercised, not merely approached).
        while (ProductChatBudget.EstimateTokens(sb.ToString()) < 14_000)
        {
            foreach (var p in LongChapterParagraphBank) sb.Append(p).Append(' ');
        }

        return sb.ToString().Trim();
    }

    private static string BuildShortChapterText() => string.Join(" ", ShortChapterSentenceBank);

    // ─── Findings ───────────────────────────────────────────────────────────────────────────────

    private static readonly string[] OtherDimensions = { "plot", "character", "tone", "theme", "continuity" };

    internal static IReadOnlyList<BookFinding> BuildFindings()
    {
        var findings = new List<BookFinding>();

        // TEN pacing findings: more than MaxFindings (8), so the dimension question (shape 5) has a
        // real cap to hit, not an empty one.
        for (var i = 0; i < 10; i++)
        {
            var order = i % ChapterCount;
            findings.Add(new BookFinding
            {
                Language = "he",
                Dimension = "pacing",
                Verdict = i % 3 == 0 ? "improve" : "keep",
                Severity = (i % 3) + 1,
                Rationale = $"קצב פרק {order} מואט משמעותית בגלל תיאור נוף ארוך שמעכב את ההתקדמות בעלילה.",
                ChapterAnchorsJson = $"[{{\"order\":{order}}}]",
                SuggestedAction = i % 2 == 0 ? "לקצר את קטע התיאור ולחזור לפעולה מוקדם יותר." : null,
                Status = FindingStatusPartition.Open,
            });
        }

        // Two findings on each of the other five dimensions, spread across chapters, so "several
        // chapters" carry findings and the backbone-only shape has real ledger content too.
        for (var i = 0; i < OtherDimensions.Length; i++)
        {
            for (var j = 0; j < 2; j++)
            {
                var order = (i * 7 + j * 3) % ChapterCount;
                findings.Add(new BookFinding
                {
                    Language = "he",
                    Dimension = OtherDimensions[i],
                    Verdict = "keep",
                    Severity = 2,
                    Rationale = $"ממצא ב{OtherDimensions[i]} בפרק {order}: התפתחות עקבית שאינה דורשת התערבות מיידית.",
                    ChapterAnchorsJson = $"[{{\"order\":{order}}}]",
                    Status = FindingStatusPartition.Open,
                });
            }
        }

        return findings;
    }

    // ─── Register: a confirmed entry, an unconfirmed one, and a PERMANENTLY SUPPRESSED one ─────────

    internal static CharacterRegister BuildRegister() => new()
    {
        Characters = new[]
        {
            new CharacterRegisterEntry
            {
                Name = Miriam, Gender = "female", Role = "protagonist",
                IsCharacter = true, GenderConfirmed = true, IsCharacterConfirmed = true,
                Aliases = new[] { "מרים" },
            },
            new CharacterRegisterEntry { Name = Doron, Gender = "male", Role = "supporting", IsCharacter = true },
            new CharacterRegisterEntry { Name = Yonat, Gender = "female", Role = "minor", IsCharacter = true },
            new CharacterRegisterEntry
            {
                // The author said "not a character": must never ground an answer (d1 section (1)).
                Name = SuppressedName, IsCharacter = false, IsCharacterConfirmed = true,
            },
        },
    };

    // ─── Statuses: a realistic BETWEEN-THE-POLES state, not all-built or all-missing ──────────────

    private static BookSummaryStatus BuildSummaryStatus() => new()
    {
        TotalChapters = ChapterCount, BuiltChapters = 38, StaleCount = 2,
        HasSummary = true, SummaryCoversBuiltChapters = true, Language = "he",
    };

    private static BookReviewStatus BuildReviewStatus(int findingCount, int openCount, int resolvedCount) => new()
    {
        HasBriefs = true, HasReview = true, StaleVsBriefs = false,
        FindingCount = findingCount, OpenFindingCount = openCount, ResolvedFindingCount = resolvedCount,
        ChaptersReviewed = 38, ChaptersTotal = ChapterCount, Language = "he",
    };

    private static BookStyleBaselineStatus BuildBaselineStatus() => new()
    {
        TotalChapters = ChapterCount, BuiltChapters = ChapterCount, StaleCount = 0, HasBaseline = true,
    };

    private static BookBrief BuildBookBrief(IReadOnlyList<ChapterBrief> briefs) => new()
    {
        Genre = "דרמה משפחתית", SubGenre = "מסתורין", TargetAudience = "מבוגרים",
        LiteratureLevel = 6,
        Themes = briefs.SelectMany(b => b.ThematicMarkers).Distinct().Take(10).ToList(),
        Synopsis = "רומן על משפחה באי קטן שסודות עברה צפים אל פני השטח דור אחרי דור, וילדה מגלה שהאמת " +
                   "רחוקה מהרבה יותר מהסיפור שסופר לה כל חייה, ושכל מי שסביבה יודע פיסה אחרת ממנה.",
    };

    private static IReadOnlyList<BookArtifactSelector.ChapterRef> BuildChapterRefs()
        => Enumerable.Range(0, ChapterCount)
            .Select(o => new BookArtifactSelector.ChapterRef(o, TitleFor(o)))
            .ToList();

    private static readonly IReadOnlyList<(string Type, int? ChapterOrder, DateTimeOffset At)> History = new[]
    {
        ("Proofread", (int?)3, DateTimeOffset.Parse("2026-08-01T10:00:00Z")),
        ("LineEdit", (int?)7, DateTimeOffset.Parse("2026-08-02T11:00:00Z")),
        ("BookReview", (int?)null, DateTimeOffset.Parse("2026-08-03T09:00:00Z")),
        ("ChapterSummary", (int?)12, DateTimeOffset.Parse("2026-08-04T14:00:00Z")),
    };

    // ─── Assembly: mirrors BookChatContextReader.ReadAsync's block order, using the SAME real,
    // pure static methods it calls - only the database I/O is replaced by the fixture above ────────

    private sealed record AssembledContext(
        IReadOnlyList<BookArtifactBlock> Blocks,
        BookArtifactSelector.BookQuestionKeys Keys,
        IReadOnlyList<int> EscalatedWhole,
        IReadOnlyList<int> EscalatedExcerpt);

    private static AssembledContext Assemble(
        string question,
        IReadOnlyList<ChapterBrief> briefs,
        IReadOnlyList<BookFinding> findings,
        CharacterRegister register,
        IReadOnlyDictionary<int, string> rawTextByOrder,
        IReadOnlyDictionary<int, string>? authorSummariesByOrder = null)
    {
        var filteredRegister = CharacterRegisterMerge.ForAnalysis(register);
        var chapterRefs = BuildChapterRefs();
        var keys = BookArtifactSelector.Select(question, chapterRefs, filteredRegister);

        var blocks = new List<BookArtifactBlock>();

        var openCount = findings.Count(f => f.Status == FindingStatusPartition.Open);
        blocks.Add(BookArtifactBlocks.Statuses(
            BuildSummaryStatus(), BuildReviewStatus(findings.Count, openCount, resolvedCount: 0), BuildBaselineStatus()));

        var briefBlock = BookArtifactBlocks.BookBrief(
            BuildBookBrief(briefs), BookTitle, BookArtifactBlocks.DefaultBookBriefMaxTokens);
        if (briefBlock != null) blocks.Add(briefBlock);

        // ESCALATION RUNS BEFORE THE BRIEF SELECTION, mirroring ReadAsync's own order: the brief
        // exclusion keys on the raw text that ACTUALLY rode along, which only the escalation can report
        // (g1 F-7). Ordered this way here too, or this helper would measure a different assembler than
        // the one that ships.
        var whole = new List<int>();
        var excerpted = new List<int>();
        var remaining = BookChatExcerpts.EscalationBudgetTokens;
        foreach (var order in keys.EscalationChapterOrders.Take(BookChatContextReader.MaxEscalatedChapters))
        {
            if (remaining <= 0) break;
            if (!rawTextByOrder.TryGetValue(order, out var text)) continue;

            var excerpt = BookChatExcerpts.Build(text, question, remaining);
            if (!excerpt.HasText) continue;

            var block = BookArtifactBlocks.ChapterText(
                TurnLanguage, order, TitleFor(order), excerpt, rank: 100 - order);
            if (block == null) continue;

            blocks.Add(block);
            remaining -= excerpt.EstimatedTokens;
            (excerpt.IsWholeChapter ? whole : excerpted).Add(order);
        }

        var carriedRawText = whole.Concat(excerpted).ToList();
        var authorSummaries = authorSummariesByOrder ?? new Dictionary<int, string>();

        foreach (var (brief, rank) in BookChatContextReader.RankChapterBriefs(briefs, keys, carriedRawText))
        {
            authorSummaries.TryGetValue(brief.Order, out var authorSummary);
            blocks.Add(BookArtifactBlocks.ChapterBrief(TurnLanguage, brief, authorSummary, rank));
        }

        foreach (var order in carriedRawText.OrderBy(o => o))
        {
            if (!authorSummaries.TryGetValue(order, out var authorSummary)) continue;
            var summaryBlock = BookArtifactBlocks.AuthorSummary(
                TurnLanguage, order, TitleFor(order), authorSummary, rank: 100 - order);
            if (summaryBlock != null) blocks.Add(summaryBlock);
        }

        var registerBlock = BookArtifactBlocks.Register(filteredRegister);
        if (registerBlock != null) blocks.Add(registerBlock);

        var rankedFindings = findings
            .Select(f => (Finding: f, Rank: BookChatContextReader.FindingRank(f, keys)))
            .OrderByDescending(x => x.Rank)
            .ThenByDescending(x => x.Finding.Severity)
            .ThenBy(x => x.Finding.Dimension, StringComparer.Ordinal)
            .ThenBy(x => x.Finding.Id)
            .Take(BookChatContextReader.MaxFindings);
        foreach (var (finding, rank) in rankedFindings) blocks.Add(BookArtifactBlocks.Finding(finding, rank));

        var historyBlock = BookArtifactBlocks.History(History);
        if (historyBlock != null) blocks.Add(historyBlock);

        return new AssembledContext(blocks, keys, whole, excerpted);
    }

    private static ProductChatBudget.Composition ComposeFor(
        string question, IReadOnlyList<BookArtifactBlock> blocks, IReadOnlyList<ProductChatTurn>? history = null)
    {
        var corpus = ProductChatCorpusTests.LoadRealCorpus();
        var guides = GuideSelector.Select(
            question, corpus.Documents, TurnLanguage, ProductChatService.BookAwareGuideCount);

        var options = ProductChatBudgetTests.AiConfig();
        var budget = ProductChatBudget.InputTokenBudget(
            BookContextAssembler.ResolveNumCtxForTask(options, AiTaskType.ProductChat),
            BookContextAssembler.ResolveOutputReserveForTask(options, AiTaskType.ProductChat));

        return ProductChatBudget.Compose(
            TurnLanguage, guides, history ?? Array.Empty<ProductChatTurn>(), question, budget, blocks, BookTitle);
    }

    /// <summary>A full 8-turn Hebrew history at the per-turn character cap - phase A's own measured
    /// worst case (g1 F2), reused here to see whether COMBINING it with a book-scoped turn is what it
    /// takes to trip the cascade at 40 chapters, since none of the five shapes alone did.</summary>
    private static IReadOnlyList<ProductChatTurn> FullHebrewHistory()
        => ProductChatService.CapHistory(
            Enumerable.Range(1, ProductChatService.MaxHistoryTurns)
                .Select(i => new ProductChatTurnDto(
                    i % 2 == 0 ? "assistant" : "user",
                    "ת" + i.ToString("00") + new string('ש', ProductChatService.MaxHistoryTurnChars - 3)))
                .ToList());

    private static string DescribeRefKind(string reference)
    {
        if (reference.StartsWith(BookArtifactRefs.FindingPrefix, StringComparison.Ordinal)) return "finding";
        if (reference.StartsWith(BookArtifactRefs.ChapterBriefPrefix, StringComparison.Ordinal)) return "chapter-brief";
        if (reference.StartsWith(BookArtifactRefs.ChapterSummaryPrefix, StringComparison.Ordinal)) return "chapter-summary";
        if (reference.StartsWith(BookArtifactRefs.ChapterTextPrefix, StringComparison.Ordinal)) return "chapter-text";
        if (reference == BookArtifactRefs.Register) return "register";
        if (reference == BookArtifactRefs.History) return "history";
        if (reference == BookArtifactRefs.BookBrief) return "book-brief";
        return reference;
    }
}
