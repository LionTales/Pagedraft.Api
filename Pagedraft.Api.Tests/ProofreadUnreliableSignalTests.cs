using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for the additive <see cref="AnalysisResult.ProofreadResultUnreliable"/> signal: it must be
/// true ONLY for genuine failures (empty/blank output or unrelated/discarded content), and false for
/// genuinely-clean text — without disturbing <see cref="AnalysisResult.ProofreadNoChangesHint"/>.
/// </summary>
public class ProofreadUnreliableSignalTests
{
    private static UnifiedAnalysisService BuildService(
        AppDbContext db,
        string llmContent,
        string inputText,
        Guid chapterId,
        Guid bookId)
    {
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse
            {
                Content = llmContent,
                Provider = "test-provider",
                Model = "test-model"
            });

        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(),
                chapterId,
                AnalysisType.Proofread,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        return new UnifiedAnalysisService(
            db,
            routerMock.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions()),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            new SuggestionDiffService(),
            // Ktiv-male enforcement OFF so the deterministic spelling sub-check never appends extra
            // suggestions and these tests exercise ONLY the proofread-unreliable signal in isolation.
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance));
    }

    [Fact]
    public async Task RunAsync_Proofread_CleanText_NotUnreliable_AndNoChangesHintStillTrue()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה.";
        var llmOutput = inputText; // model echoes a NON-EMPTY input => genuinely clean / no-change

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.False(result.ProofreadResultUnreliable); // clean text is trustworthy
        Assert.True(result.ProofreadNoChangesHint);      // existing semantics preserved
    }

    [Fact]
    public async Task RunAsync_Proofread_EmptyOutput_IsUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה.";
        var llmOutput = "   "; // blank/whitespace output => genuine failure

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.True(result.ProofreadResultUnreliable);
    }

    [Fact]
    public async Task RunAsync_Proofread_UnrelatedOutput_IsUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText =
            "זהו טקסט לבדיקה שמטרתו למצוא שגיאות ולהציע תיקונים באיכות גבוהה ומקיפה לאותיות ודקדוק " +
            "במהלך הקריאה כדי לוודא שהמודל אכן מתקן.";

        // Long content unrelated to the input (includes a continuation marker) => IsProofreadResultUnrelated trips.
        var llmOutput =
            "Chapter 12: The story continues with completely different content and new characters, far away from the original text.";

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.True(result.ProofreadResultUnreliable);
    }

    [Fact]
    public async Task RunAsync_Proofread_DroppedSpan_IsUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        // Input: several sentences. The model output OMITS a contiguous middle span (a full sentence
        // plus a mid-text dialogue block), keeping the opening and closing intact. Word-similarity on the
        // prefix stays high (so IsProofreadResultUnrelated returns false) and the output is non-empty, so
        // the existing empty/unrelated checks miss it — only the dropped-content signal should catch it.
        var opening =
            "השמש שקעה מאחורי ההרים והאוויר התקרר במהירות. " +
            "דנה עמדה ליד החלון והביטה אל הרחוב הריק שמתחת. ";
        var droppedMiddle =
            "היא נזכרה בכל מה שקרה באותו יום ארוך ומתיש שבו הכול השתבש ללא תקנה. " +
            "\"אני לא מבינה איך הגענו לכאן,\" אמרה בקול חנוק, \"חשבתי שיהיה לנו די זמן להספיק הכול, " +
            "אבל הזמן פשוט אזל לנו בין האצבעות ולא נשאר דבר.\" " +
            "הוא לא ענה לה, רק הנהן בראשו לאט וסגר את הדלת אחריו בשקט גמור. ";
        var closing =
            "עכשיו, כשהלילה ירד, נותר רק להמתין ולקוות שהבוקר יביא עמו בשורה טובה יותר. " +
            "דנה נשמה עמוקות וניסתה להאמין שהכול עוד יסתדר בסופו של דבר.";

        var inputText = opening + droppedMiddle + closing;
        var llmOutput = opening + closing; // the entire middle span vanished

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.True(result.ProofreadResultUnreliable);
    }

    [Fact]
    public async Task RunAsync_Proofread_SmallReplacement_NotUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        // Clean input; the model makes a single small in-place spelling fix and keeps the rest identical
        // (same length/content otherwise). A normal proofread with a real fix must NOT be flagged.
        var inputText =
            "השמש שקעה מאחורי ההרים והאוויר התקרר במהירות. " +
            "דנה עמדה ליד החלון והביטה אל הרחוב הריק שמתחת. " +
            "היא נזכרה בכל מה שקרה באותו יום ארוך ומתיש שבו הכול השתבש. " +
            "עכשיו, כשהלילה ירד, נותר רק להמתין ולקוות שהבוקר יביא בשורה טובה.";
        // One-word in-place correction (replace "במהירות" with "במהירות רבה"-like single fix): swap a
        // single word for a corrected spelling, same surrounding text, near-identical length.
        var llmOutput = inputText.Replace("התקרר", "התקרֵר");

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.False(result.ProofreadResultUnreliable);
    }

    /// <summary>
    /// FIX B (contiguity branch): a dropped span whose output is NOT much shorter than the input - so the
    /// length backstop (signal a, output &lt; 90% of input) does NOT trip - must still be flagged via the
    /// contiguity check. The model output keeps the long opening and closing intact and omits a single
    /// contiguous mid-text clause, leaving overall length &gt; 90% of input but producing a long run of
    /// offset-adjacent pure-deletion suggestions.
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_ContiguousDeletionRun_ShortDrop_IsUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        // Long surrounding text (keeps the length ratio high so signal (a) does NOT trip) with ONE
        // contiguous clause dropped from the middle. The dropped clause is a run of consecutive words, so
        // the per-word deletion suggestions are offset-adjacent => long contiguous run. The before/after
        // sections are deliberately long (the dropped clause is < 10% of the total) so the output stays
        // > 90% of the input length and ONLY the contiguity branch can flag it.
        var before =
            "השמש שקעה מאחורי ההרים והאוויר התקרר במהירות בעוד הרוח נשבה בין העצים הגבוהים שלאורך הדרך. " +
            "דנה עמדה ליד החלון הגדול והביטה אל הרחוב הריק שמתחת אליה בזמן שהמחשבות רצו לה בראש ללא הפסקה. " +
            "היא ידעה שהיום הזה יהיה שונה מכל הימים שקדמו לו וכי שום דבר כבר לא יחזור להיות כפי שהיה פעם. " +
            "מחוץ לחלון, אורות הרחוב נדלקו אחד אחרי השני והאירו את המדרכות הרטובות מן הגשם שירד מוקדם יותר. " +
            "חתול שחור חצה את הכביש לאט, עצר לרגע באמצע, הביט סביבו בחשדנות ואז נעלם אל תוך הצללים שמעבר. " +
            "מישהו פתח חלון בבניין ממול, נשמעה מנגינה רחוקה של פסנתר, ואז הכול שב לשקט שאפף את כל השכונה. ";
        // This whole clause is OMITTED by the model => a contiguous block of pure deletions.
        var droppedClause =
            "הוא הביט בה בשתיקה ארוכה ומביכה ולא הצליח למצוא את המילים. ";
        var after =
            "עכשיו, כשהלילה ירד אט אט על העיר השקטה, נותר רק להמתין בסבלנות ולקוות שהבוקר יביא עמו בשורה טובה. " +
            "דנה נשמה עמוקות, עצמה את עיניה לרגע קל וניסתה להאמין בכל ליבה שהכול עוד יסתדר בסופו של דבר הטוב. " +
            "היא חשבה על כל הדרכים שהובילו אותה עד לכאן, על ההחלטות הקטנות שהצטברו לכדי גורל אחד שלם וכבד. " +
            "בחוץ המשיכה הרוח לנשוב, העלים המשיכו לרשרש, והעולם המשיך להסתובב כאילו דבר לא קרה באותו ערב. " +
            "ובכל זאת, עמוק בפנים, היא ידעה שמשהו השתנה לבלי שוב, ושמחר כבר לא יהיה דומה כלל לאתמול שחלף.";

        var inputText = before + droppedClause + after;
        var llmOutput = before + after; // only the contiguous middle clause vanished

        // Sanity: the output is NOT much shorter than the input (length backstop must NOT trip), so the
        // contiguity branch is what catches it. (Asserted indirectly; the ratio is comfortably > 0.9.)
        Assert.True(llmOutput.Length > inputText.Length * 0.9,
            $"output/input length ratio = {(double)llmOutput.Length / inputText.Length:F3} (expected > 0.9 so signal (a) does not trip)");

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.True(result.ProofreadResultUnreliable); // caught by the contiguity branch
    }

    /// <summary>
    /// FIX B control: SCATTERED legit deletions must NOT be flagged. The model removes several NON-adjacent
    /// single words (doubled words spread far apart). These are pure deletions but they are not
    /// offset-adjacent, so no long contiguous run forms and the result stays reliable. This is the
    /// false-positive the old count/ratio signal produced and the contiguity check fixes.
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_ScatteredDeletions_NotUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        // A clean draft sprinkled with DOUBLED words far apart. Each doubled word "מאוד מאוד", "ממש ממש"
        // etc. is a single legit deletion; they sit far apart in the text => scattered, not contiguous.
        var inputText =
            "הבית היה גדול מאוד מאוד ועמד בקצה הרחוב השקט. " +
            "ילדים שיחקו בחצר ממש ממש בשמחה רבה לאורך כל היום. " +
            "אמא הכינה ארוחה טעימה טעימה במטבח החמים והנעים. " +
            "אבא קרא ספר מעניין מעניין בכורסה שליד החלון הגדול. " +
            "הכלב נבח בקול רם רם כשמישהו התקרב אל השער הברזל. " +
            "השמש זרחה בהיר בהיר על הגגות האדומים של הבתים. " +
            "ציפורים צייצו יפה יפה בין הענפים של העץ הגבוה. " +
            "הרוח נשבה קל קל והניעה את הווילון הלבן בחלון. " +
            "ריח של פרחים מתוק מתוק עלה מן הגינה הקטנה שבחוץ. " +
            "כולם הרגישו טוב טוב באותו יום קיץ נעים ושליו.";
        // Output removes the SECOND copy of each doubled word (scattered single-word deletions).
        var llmOutput = inputText
            .Replace("מאוד מאוד", "מאוד")
            .Replace("ממש ממש", "ממש")
            .Replace("טעימה טעימה", "טעימה")
            .Replace("מעניין מעניין", "מעניין")
            .Replace("רם רם", "רם")
            .Replace("בהיר בהיר", "בהיר")
            .Replace("יפה יפה", "יפה")
            .Replace("קל קל", "קל")
            .Replace("מתוק מתוק", "מתוק")
            .Replace("טוב טוב", "טוב");

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        // 10 scattered single-word deletions, none contiguous => not a dropped span => reliable.
        var pureDeletions = result.Suggestions.Count(s => string.IsNullOrWhiteSpace(s.SuggestedText));
        Assert.False(result.ProofreadResultUnreliable);
        Assert.True(pureDeletions >= 8,
            $"expected ~10 scattered pure deletions, observed {pureDeletions} (control still exercises a real deletion flood)");
    }

    /// <summary>
    /// BUG 2 (signal a): scattered single-word deletions whose cumulative shrink EXCEEDS 10% must still be
    /// reliable. The old length backstop (output &lt; 90% of input) fired on this purely because the
    /// deletions summed past the ratio - the exact false positive signal (b) was designed to avoid - even
    /// though every removed word is a reviewable suggestion and no span was dropped. The fixed backstop fires
    /// only when the shrink is UNACCOUNTED for by suggestions, so this heavy-but-legitimate cleanup is not
    /// flagged. Short sentences make the doubled words a large fraction of the text, so the &gt;10% precondition
    /// (asserted below) genuinely exercises signal (a)'s gate - unlike the control above, which stays &lt;10%.
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_ManyScatteredDeletions_OverTenPercentShrink_NotUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        // SHORT distinct sentences, each with exactly one doubled word in/near the middle. Because the
        // surrounding text is short, removing one copy per sentence shrinks the whole text by well over 10%,
        // while the deletions sit far apart (one per sentence) so no contiguous run forms.
        var inputText = string.Join(" ",
            "הבית גדול מאוד מאוד.",
            "הילד רץ ממש ממש.",
            "הארוחה טעימה טעימה.",
            "הספר מעניין מעניין.",
            "הכלב נבח רם רם.",
            "השמש זרחה בהיר בהיר.",
            "הציפור צייצה יפה יפה.",
            "הרוח נשבה קל קל.",
            "הפרח מתוק מתוק.",
            "הילד טוב טוב.");
        var llmOutput = inputText
            .Replace("מאוד מאוד", "מאוד")
            .Replace("ממש ממש", "ממש")
            .Replace("טעימה טעימה", "טעימה")
            .Replace("מעניין מעניין", "מעניין")
            .Replace("רם רם", "רם")
            .Replace("בהיר בהיר", "בהיר")
            .Replace("יפה יפה", "יפה")
            .Replace("קל קל", "קל")
            .Replace("מתוק מתוק", "מתוק")
            .Replace("טוב טוב", "טוב");

        // Precondition: the cumulative scattered deletions push the output BELOW 90% of the input, so the old
        // length backstop WOULD have tripped. This is what makes the test a genuine regression guard.
        Assert.True(llmOutput.Length < inputText.Length * 0.9,
            $"output/input length ratio = {(double)llmOutput.Length / inputText.Length:F3} (expected < 0.9 so this exercises signal (a)'s gate)");

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        // Scattered deletions account for the shrink (each is a reviewable suggestion) => signal (a) must NOT
        // fire, and they are not contiguous => signal (b) must NOT fire => reliable.
        var pureDeletions = result.Suggestions.Count(s => string.IsNullOrWhiteSpace(s.SuggestedText));
        Assert.False(result.ProofreadResultUnreliable);
        Assert.True(pureDeletions >= 8,
            $"expected ~10 scattered pure deletions, observed {pureDeletions}");
    }

    /// <summary>
    /// BUG 1: a NON-blank model payload that SanitizeResponse strips to nothing (here CJK-only noise) means no
    /// real proofread output was produced, yet the old flag keyed off the RAW response being whitespace - which
    /// it is not - so the failure slipped through as "reliable". The fix keys off the SANITIZED content being
    /// blank. RunWithInputAsync has no echo-fallback, so the result is a genuinely empty ResultText that also
    /// escapes the unrelated check (empty/short results are never judged unrelated) and the dropped-content
    /// check (empty output is skipped) - so ONLY the sanitized-blank signal can catch it. The old code returned
    /// false here; the fix returns true.
    /// </summary>
    [Fact]
    public async Task RunWithInputAsync_Proofread_NonBlankPayloadThatSanitizesToBlank_IsUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה שמכיל כמה מילים בעברית פשוטה.";
        // Non-blank raw payload (so the OLD raw-whitespace check is false) that sanitizes to nothing: the CJK
        // stripper removes all of it, leaving no usable proofread text.
        var llmOutput = "你好世界你好世界你好世界你好世界";

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunWithInputAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            bookId,
            chapterId,
            sceneId: null,
            inputText,
            language: "he",
            ct: CancellationToken.None);

        // Sanitization left no usable output, and nothing echoed the input back in on this path.
        Assert.True(string.IsNullOrWhiteSpace(result.ResultText));
        Assert.True(result.ProofreadResultUnreliable);
    }

    /// <summary>
    /// A thinking-model Proofread that wraps its real correction AFTER a &lt;think&gt; block must be reliable.
    /// StripThinkBlock keeps the post-&lt;/think&gt; answer, so the first sanitize already yields real text and
    /// the result has valid suggestions. This is the realistic shape of the scenario the
    /// snapshot-before-fallback bug warned about ("unreliable on a successful proofread with valid
    /// suggestions"): it must NOT be flagged.
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_ThinkBlockThenRealCorrection_NotUnreliable_HasSuggestions()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText = "הילד הלך לגן עם חבריו הטובים. הם שיחקו בחצר עד שעת הערב.";
        // Thinking model: reasoning inside <think>, the actual corrected text AFTER </think> (one word fixed).
        var corrected = inputText.Replace("הטובים", "היקרים");
        var llmOutput = "<think>אני בודק איות ופיסוק בקטע הזה.</think>" + corrected;

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        // The <think> reasoning is stripped, the real correction is kept => reliable, with a suggestion.
        Assert.False(result.ProofreadResultUnreliable);
        Assert.DoesNotContain("<think>", result.ResultText);
        Assert.True(result.Suggestions.Count >= 1,
            $"expected at least one suggestion from the one-word correction, observed {result.Suggestions.Count}");
    }

    /// <summary>
    /// Guard for the re-evaluated blank signal: when the recovery fallback runs but only salvages JUNK the
    /// sanitizer still rejects (a &lt;think&gt; block whose inner text is CJK noise, nothing after), the run must
    /// stay unreliable. The fix re-checks the recovered content by re-sanitizing it, so junk recovery is NOT
    /// mistaken for a real proofread (the failure mode a naive "any non-blank recovery is fine" fix would add).
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_FallbackRecoversOnlyJunk_IsUnreliable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה שמכיל כמה מילים בעברית פשוטה.";
        // <think> with CJK-only inner and nothing after </think>: the first sanitize blanks (CJK stripped),
        // the fallback salvages the raw "<think>…</think>" which ALSO sanitizes to blank => no usable output.
        var llmOutput = "<think>你好世界你好世界你好世界</think>";

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.True(result.ProofreadResultUnreliable);
    }

    /// <summary>
    /// FIX A (chunked path): a genuinely-CLEAN long chapter that exceeds the chunk threshold
    /// (ProofreadChunkTargetWords) must NOT be flagged unreliable just because its merged output is nearly
    /// identical to the input. The chunked path is reachable from RunAsync by lowering
    /// AiOptions.ProofreadChunkTargetWords so a modest input is chunked. The mocked model echoes each
    /// chunk's text verbatim (clean), so the merged output equals the input => ProofreadNoChangesHint is
    /// true while ProofreadResultUnreliable must be false (the bug: it used to follow noChangesHint).
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_Chunked_CleanLongText_NotUnreliable_AndNoChangesHintTrue()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        // A clean multi-sentence Hebrew chapter. We force chunking by lowering the chunk-target word count
        // below this input's word count (rather than authoring 500+ words), so RunProofreadChunkedAsync runs.
        var inputText =
            "השמש שקעה מאחורי ההרים והאוויר התקרר במהירות. " +
            "דנה עמדה ליד החלון והביטה אל הרחוב הריק שמתחת. " +
            "היא נזכרה בכל מה שקרה באותו יום ארוך ומתיש. " +
            "הרוח נשבה בין העצים והעלים רשרשו בשקט. " +
            "אורות הרחוב נדלקו אחד אחרי השני לאורך הדרך. " +
            "עכשיו, כשהלילה ירד, נותר רק להמתין ולקוות שהבוקר יביא בשורה טובה.";

        var wordCount = inputText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        // The mock must echo EACH chunk's input verbatim so every chunk merges back to clean text. The chunked
        // path wraps each chunk in [TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT]; StripTextToCorrectMarkers strips the
        // markers on the way out, so echoing the wrapped input back yields the original chunk text after merge.
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) => new AiResponse
            {
                Content = req.InputText, // echo the (wrapped) chunk text verbatim => clean per chunk
                Provider = "test-provider",
                Model = "test-model"
            });

        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(),
                chapterId,
                AnalysisType.Proofread,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        // Lower the chunk target below the input's word count so RunProofreadChunkedAsync is selected.
        var aiOptions = new AiOptions { ProofreadChunkTargetWords = Math.Max(1, wordCount / 3) };
        Assert.True(wordCount > aiOptions.EffectiveProofreadChunkTargetWords,
            $"input has {wordCount} words; chunk target {aiOptions.EffectiveProofreadChunkTargetWords} must be lower so chunking triggers");

        var svc = new UnifiedAnalysisService(
            db,
            routerMock.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(aiOptions),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance));

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.Equal("chunked", result.ModelName);          // confirms the chunked path actually ran
        Assert.True(result.ProofreadNoChangesHint);          // merged output nearly identical => no-changes hint
        Assert.False(result.ProofreadResultUnreliable);      // FIX A: clean long chapter is NOT unreliable
    }

    /// <summary>
    /// Persistence round-trip: an unreliable Proofread run must store <c>ProofreadResultUnreliable = true</c>
    /// so that History (which reloads the entity from the DB) is consistent with the live run. We RELOAD
    /// the saved entity by id (AsNoTracking, so the value comes from the store, not the in-memory instance)
    /// and assert the flag survived — proving the column is mapped/persisted, not merely set in memory.
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_EmptyOutput_UnreliableFlag_PersistsAcrossReload()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה.";
        var llmOutput = "   "; // blank/whitespace output => genuine failure => unreliable

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.True(result.ProofreadResultUnreliable); // set on the live run

        var reloaded = await db.AnalysisResults
            .AsNoTracking()
            .FirstAsync(r => r.Id == result.Id);

        Assert.True(reloaded.ProofreadResultUnreliable); // survived the round-trip => persisted
    }

    /// <summary>
    /// Negative round-trip: a reliable (genuinely-clean) Proofread run must reload as
    /// <c>ProofreadResultUnreliable = false</c> — the persisted default, no false positives.
    /// </summary>
    [Fact]
    public async Task RunAsync_Proofread_CleanText_ReliableFlag_PersistsAsFalseAcrossReload()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה.";
        var llmOutput = inputText; // non-empty echo => genuinely clean => reliable

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var svc = BuildService(db, llmOutput, inputText, chapterId, bookId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        Assert.False(result.ProofreadResultUnreliable);

        var reloaded = await db.AnalysisResults
            .AsNoTracking()
            .FirstAsync(r => r.Id == result.Id);

        Assert.False(reloaded.ProofreadResultUnreliable); // persisted default holds
    }

    // ─── ProofreadDroppedContent: contiguity branch deleted-char gate ────────────────────────
    // Direct unit tests for the merged-level dropped-content guard. Input and output are equal-length so the
    // length backstop (signal a) never fires and ONLY the contiguity branch (b) is exercised.

    private static AnalysisSuggestion Deletion(int start, int end) =>
        new() { StartOffset = start, EndOffset = end, SuggestedText = string.Empty, OriginalText = new string('x', end - start) };

    [Fact]
    public void ProofreadDroppedContent_ManyTinyContiguousDeletions_NotFlagged()
    {
        // The live false positive: 8 offset-adjacent pure deletions that TOGETHER remove only ~17 chars
        // (ktiv-male + punctuation micro-edits bunched up). A run of >= 6 forms, but almost no text is
        // actually deleted, so it must NOT be treated as a dropped passage.
        var text = new string('א', 500);
        var deletions = new[]
        {
            Deletion(10, 12), Deletion(13, 15), Deletion(16, 18), Deletion(19, 22),
            Deletion(23, 25), Deletion(26, 28), Deletion(29, 31), Deletion(32, 34),
        };

        Assert.False(UnifiedAnalysisService.ProofreadDroppedContent(text, text, deletions));
    }

    [Fact]
    public void ProofreadDroppedContent_ContiguousRunDeletingSubstantialText_Flagged()
    {
        // A genuinely dropped clause: 8 offset-adjacent word-deletions that together remove ~48 chars.
        // High count AND substantial deleted-char total => a dropped passage => flagged.
        var text = new string('א', 500);
        var deletions = new[]
        {
            Deletion(10, 16), Deletion(17, 23), Deletion(24, 30), Deletion(31, 37),
            Deletion(38, 44), Deletion(45, 51), Deletion(52, 58), Deletion(59, 65),
        };

        Assert.True(UnifiedAnalysisService.ProofreadDroppedContent(text, text, deletions));
    }

    [Fact]
    public void ProofreadDroppedContent_SingleWideDeletionSpan_Flagged()
    {
        // A single pure deletion covering a wide span (>= 60 chars) is a dropped passage regardless of count.
        var text = new string('א', 500);
        var deletions = new[] { Deletion(100, 175) };

        Assert.True(UnifiedAnalysisService.ProofreadDroppedContent(text, text, deletions));
    }

    [Fact]
    public void ProofreadDroppedContent_SingleNarrowDeletion_NotFlagged()
    {
        // One short deletion is a normal in-place edit, not an omission.
        var text = new string('א', 500);
        var deletions = new[] { Deletion(100, 130) };

        Assert.False(UnifiedAnalysisService.ProofreadDroppedContent(text, text, deletions));
    }
}
