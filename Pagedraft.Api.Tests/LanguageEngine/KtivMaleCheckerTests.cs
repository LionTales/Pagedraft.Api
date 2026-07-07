using System.Linq;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// Deterministic tests for the Hebrew ktiv-male (full-spelling) copyedit check. No LLM/Ollama/GPU
/// dependency: the checker is a pure dictionary/rule lookup, so every case here runs offline and fast.
/// </summary>
public class KtivMaleCheckerTests
{
    private static KtivMaleChecker MakeChecker(bool enforce = true) =>
        new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = enforce });

    // ── POSITIVE: haser → male should be flagged with the male form ──────────────

    [Theory]
    // vav-for-/o/ family
    [InlineData("הגשתי תכנית לעבודה.", "תכנית", "תוכנית")]
    [InlineData("שלחתי את המכתב דרך דאר רשום.", "דאר", "דואר")]
    [InlineData("התקנו תכנה חדשה.", "תכנה", "תוכנה")]
    // yod-for-/i,e/ family
    [InlineData("זה סיפור אמתי.", "אמתי", "אמיתי")]
    [InlineData("קראתי עתון הבוקר.", "עתון", "עיתון")]
    [InlineData("צבר נסיון רב.", "נסיון", "ניסיון")]
    public void FindSuggestions_HaserForm_FlagsMaleSpelling(string text, string expectedOriginal, string expectedMale)
    {
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions(text, "he");

        var match = Assert.Single(suggestions, s => s.OriginalText == expectedOriginal);
        Assert.Equal(expectedMale, match.SuggestedText);
        Assert.Equal("ktiv-male", match.Category);
        // Offset must point at the actual word in the (normalized) text.
        Assert.True(match.StartOffset.HasValue && match.EndOffset.HasValue);
        Assert.Equal(expectedOriginal, text.Substring(match.StartOffset!.Value, match.EndOffset!.Value - match.StartOffset.Value));
    }

    [Theory]
    // The classic "לעיתים" case: ל prefix + עתים → לעיתים. Prefix is preserved on the suggestion.
    [InlineData("לעתים הוא מאחר.", "לעתים", "לעיתים")]
    [InlineData("התכנית אושרה.", "התכנית", "התוכנית")]
    [InlineData("בתכנה החדשה יש באג.", "בתכנה", "בתוכנה")]
    public void FindSuggestions_PrefixedHaserForm_FlagsMaleWithPrefix(string text, string expectedOriginal, string expectedMale)
    {
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions(text, "he");

        var match = Assert.Single(suggestions, s => s.OriginalText == expectedOriginal);
        Assert.Equal(expectedMale, match.SuggestedText);
    }

    // ── NEGATIVE: already-male / off-list words must NOT be flagged ───────────────

    [Theory]
    [InlineData("הגשתי תוכנית לעבודה.")]   // already male
    [InlineData("הוא הפגין עוצמה רבה.")]    // already male
    [InlineData("זה סיפור אמיתי.")]          // already male
    [InlineData("לעיתים הוא מאחר.")]          // already male, prefixed
    [InlineData("הילד רץ הביתה מהר.")]       // ordinary words, none on the list
    [InlineData("שלום עולם, זהו טקסט לבדיקה.")] // off-list
    public void FindSuggestions_AlreadyMaleOrOffList_ReturnsNoSuggestions(string text)
    {
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions(text, "he");

        Assert.Empty(suggestions);
    }

    [Fact]
    public void FindSuggestions_AlreadyMaleSentinel_IsNeverFlagged()
    {
        // Sentinel entries (key == value, e.g. גרסה/דיוק/מאוזן) exist in the list only to make the
        // "already-correct words are never flagged" guarantee explicit. They must produce nothing.
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions("הגרסה מאוזן בדיוק רב.", "he");

        Assert.Empty(suggestions);
    }

    [Fact]
    public void FindSuggestions_ShvaNachExceptionWords_AreNeverFlagged()
    {
        // SHVA-NACH EXCEPTION: when a letter carrying a shva nach follows the /i/ vowel, the /i/ is
        // NOT marked with a yod. So דמיון and צמצום are normative ktiv-male AS-IS (no added yod) -
        // דמיון is the Academy's own cited example. The checker must NEVER suggest דימיון/צימצום;
        // doing so would tell the author to introduce a spelling error. Guards against re-adding the
        // wrong haser→male pairs to the seed list.
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions("הדמיון שלו והצמצום בהוצאות הפתיעו את כולם.", "he");

        Assert.Empty(suggestions);
    }

    [Fact]
    public void FindSuggestions_GaluiAdjectiveHomograph_IsNeverFlagged()
    {
        // HOMOGRAPH GUARD: גָּלוּי is a very common ADJECTIVE ("visible/open/revealed", שם תואר) and is
        // NOT a haser form of the unrelated NOUN גילוי ("revelation/discovery"). A גלוי→גילוי pair
        // would be a meaning-changing miscorrection on ordinary prose, so the seed list deliberately
        // OMITS it (edit-r03). This asserts a sentence using the adjective yields zero ktiv-male
        // suggestions, including behind a common prefix (וגלוי). Guards against re-adding the pair.
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions("הסוד היה גלוי לכולם וגלוי לב.", "he");

        Assert.Empty(suggestions);
    }

    [Fact]
    public void FindSuggestions_AtsmaReflexiveHomograph_IsNeverFlagged()
    {
        // ROOT-CAUSE REGRESSION GUARD: עַצְמָהּ = "herself / its own / by itself" (reflexive/possessive)
        // is overwhelmingly the sense of עצמה in ordinary prose - NOT the haser form of עוצמה "power".
        // A context-blind עצמה→עוצמה auto-flag deterministically produced a MEANING-CHANGING wrong
        // suggestion ("makes plans with herself" → "...with power"), violating the checker's conservative
        // contract. עצמה is therefore excluded (AmbiguousHomographPairsExcluded). This asserts the
        // canonical sentence yields zero suggestions, including behind a common prefix (בעצמה).
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions("היא קובעת תוכניות עם עצמה בעצמה.", "he");

        Assert.Empty(suggestions);
    }

    [Fact]
    public void FindSuggestions_MalonAndTzurHomographs_AreNeverFlagged()
    {
        // HOMOGRAPH GUARD (be-c01): מָלוֹן = "hotel / melon" and צוּר = "rock / Tyre / besiege!" are
        // common everyday words - in ordinary prose they are NOT the haser of מילון "dictionary" /
        // ציור "drawing". A context-blind מלון→מילון or צור→ציור auto-flag would deterministically
        // produce a meaning-changing wrong suggestion, so both are excluded (moved out of HaserToMale
        // into AmbiguousHomographPairsExcluded), the same class as עצמה/חכמה/גלוי. This asserts they
        // yield zero suggestions bare AND behind a single common prefix (המלון "the hotel", במלון
        // "in the hotel"). Guards against re-adding the pairs to HaserToMale.
        var checker = MakeChecker();

        Assert.Empty(checker.FindSuggestions("לנו יש מלון גדול על החוף.", "he"));   // hotel
        Assert.Empty(checker.FindSuggestions("המלון היה מלא בקיץ.", "he"));         // "the hotel", ה prefix
        Assert.Empty(checker.FindSuggestions("נפגשנו במלון ליד הים.", "he"));       // "in the hotel", ב prefix
        Assert.Empty(checker.FindSuggestions("הם בנו את חומת צור.", "he"));         // "Tyre / rock"
    }

    [Fact]
    public void FindSuggestions_AmbiguousHomographKeys_AreNeverFlagged()
    {
        // Every key in AmbiguousHomographPairsExcluded is a common standalone word whose male form has a
        // DIFFERENT meaning, so the context-blind checker must never flag any of them (bare or behind a
        // single common prefix letter). This is the documented, auditable exclusion set; if any of these
        // keys were (re-)added to HaserToMale, this test fails.
        var checker = MakeChecker();

        foreach (var key in KtivMaleWordList.AmbiguousHomographPairsExcluded.Keys)
        {
            var bare = checker.FindSuggestions(key, "he");
            Assert.Empty(bare);

            // Also assert it is not reachable behind a single common Hebrew prefix (ו/ה/ב/כ/ל/מ/ש).
            var prefixed = checker.FindSuggestions("ו" + key, "he");
            Assert.Empty(prefixed);
        }
    }

    // ── INTENTIONAL-STYLE: dialogue/colloquial spelling not on the list is left alone ─

    [Fact]
    public void FindSuggestions_IntentionalColloquialDialogue_NotOnList_IsLeftAlone()
    {
        // A character speaking colloquially ("יאללה", "וואלה", a clipped form) uses spellings that
        // are NOT on the closed ktiv-male list. The conservative checker only touches vetted haser
        // forms, so it must leave intentional dialogue untouched.
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions("\"יאללה, בוא נלך\", אמר. \"וואלה, אין לי כוח.\"", "he");

        Assert.Empty(suggestions);
    }

    // ── CONFIG: house-style toggle gates the check ───────────────────────────────

    [Fact]
    public void FindSuggestions_ToggleOff_SuppressesAllSuggestions()
    {
        var off = MakeChecker(enforce: false);

        var suggestions = off.FindSuggestions("הגשתי תכנית בעצמה רבה.", "he");

        Assert.Empty(suggestions);
    }

    [Fact]
    public void FindSuggestions_ToggleOnByDefault_ProducesSuggestions()
    {
        var on = MakeChecker(enforce: true);

        var suggestions = on.FindSuggestions("הגשתי תכנית לעבודה.", "he");

        Assert.NotEmpty(suggestions);
    }

    [Fact]
    public void DefaultOptions_EnforceKtivMale_IsOn()
    {
        Assert.True(new HebrewStyleOptions().EnforceKtivMale);
    }

    [Fact]
    public void IOptionsConstructor_BindsValue()
    {
        // The production DI path injects IOptions<HebrewStyleOptions>; verify that overload works.
        var checker = new KtivMaleChecker(Options.Create(new HebrewStyleOptions { EnforceKtivMale = true }));

        var suggestions = checker.FindSuggestions("הגשתי תכנית.", "he");

        Assert.NotEmpty(suggestions);
    }

    // ── LANGUAGE GATING: English/non-Hebrew is never touched ─────────────────────

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("")]
    public void FindSuggestions_NonHebrewLanguage_ReturnsNoSuggestions(string language)
    {
        var checker = MakeChecker();

        // Even if the text happened to contain a haser Hebrew word, a non-Hebrew language gate
        // suppresses the check so the English path's behavior is never altered.
        var suggestions = checker.FindSuggestions("submitted a תכנית", language);

        Assert.Empty(suggestions);
    }

    // ── MULTIPLE OCCURRENCES: each is flagged independently ──────────────────────

    [Fact]
    public void FindSuggestions_MultipleHaserWords_FlagsEach()
    {
        var checker = MakeChecker();

        var suggestions = checker.FindSuggestions("התכנית עם התכנה שיפרו את הנסיון.", "he");

        Assert.Contains(suggestions, s => s.OriginalText == "התכנית" && s.SuggestedText == "התוכנית");
        Assert.Contains(suggestions, s => s.OriginalText == "התכנה" && s.SuggestedText == "התוכנה");
        Assert.Contains(suggestions, s => s.OriginalText == "הנסיון" && s.SuggestedText == "הניסיון");
    }
}
