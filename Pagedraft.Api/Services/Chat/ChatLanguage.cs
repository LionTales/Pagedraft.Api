using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// Which language a product-chat turn is in (chatbot phase A, d1 item 3).
///
/// <para>Hebrew is the app default, matching the app-level-chrome convention, so a question with no
/// script signal at all (digits, punctuation, an empty string) resolves Hebrew rather than English.
/// The assistant always answers in the QUESTION's language, never in the source guide's - a Hebrew
/// question grounded in an English guide is still answered in Hebrew.</para>
///
/// <para>Detection is by SCRIPT, not by a language model or a word list: the two languages this app
/// serves use disjoint ALPHABETS, so one Hebrew letter is taken as decisive. What that buys is
/// stability, and only stability - the rule cannot drift with a model, a word list, or a tuning
/// change, and the same question always resolves the same way. It is NOT a claim that the answer is
/// always right: it is a rule about alphabets, applied to sentences, and the paragraph below names
/// the sentence it gets wrong. It reuses <see cref="ScriptTokenPredicates"/> rather than carrying a
/// private copy of "is this a Hebrew letter", which is the trap that class exists to close.</para>
///
/// <para>THE CASE IT DOES NOT COVER. A question can QUOTE a Hebrew product term while being English
/// throughout: the guides name every editing pass in Hebrew, so an English-speaking author asking
/// "what does the ספרותי pass do?" is detected Hebrew and gets an entirely Hebrew answer. That case
/// is reachable, not theoretical, and it is ACCEPTED rather than fixed. The alternative, a
/// majority-of-letters rule, trades it for a worse one: Hebrew is the app default and this app's
/// authors are overwhelmingly Hebrew speakers, and on a SHORT Hebrew question carrying an English
/// product noun ("PageDraft", "DOCX", "Export") a majority rule flips a genuinely Hebrew author into
/// English. That input is both more common and the more damaging miss - one quoted term costs a
/// re-ask, while a native question answered in the wrong language does not announce itself.</para>
/// </summary>
public static class ChatLanguage
{
    public const string Hebrew = "he";
    public const string English = "en";

    /// <summary>The app default when a question carries no letters in either script.</summary>
    public const string Default = Hebrew;

    /// <summary>
    /// Detects the language of <paramref name="question"/>. A single Hebrew letter anywhere makes the
    /// turn Hebrew; otherwise a Latin letter makes it English; otherwise <see cref="Default"/>.
    ///
    /// <para>SINGLE-LETTER DECISIVENESS IS A CHOICE, AND IT HAS A BLIND SPOT: an otherwise English
    /// question that quotes one Hebrew product term (a pass name such as ספרותי, all of which the
    /// guides name in Hebrew) is answered in Hebrew. Reviewed and KEPT on 2026-08-06 rather than
    /// replaced with a majority-of-letters rule, for the reasons in the class remarks, and pinned by
    /// <c>TheAnswerLanguage_IsTheQuestionsOwnScript_NotTheClientsClaim</c>. Do not "fix" it by
    /// counting letters without re-reading them.</para>
    ///
    /// <para><paramref name="clientHint"/> is consulted ONLY when the question has no script signal.
    /// The question is the authority: a client that mislabels its locale must not be able to make the
    /// assistant answer a Hebrew question in English.</para>
    /// </summary>
    public static string Detect(string? question, string? clientHint = null)
    {
        if (!string.IsNullOrEmpty(question))
        {
            var sawLatin = false;
            foreach (var c in question)
            {
                // FIRST Hebrew letter wins, deliberately, including when it is a quoted product term
                // inside an English question. See the remarks: the alternative misfires more often.
                if (ScriptTokenPredicates.IsHebrewLetter(c)) return Hebrew;
                if (ScriptTokenPredicates.IsLatinLetter(c)) sawLatin = true;
            }

            if (sawLatin) return English;
        }

        return Normalize(clientHint) ?? Default;
    }

    /// <summary>Maps a language tag ("he", "he-IL", "EN-GB") to this module's two values, or null.</summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (tag.StartsWith("he", StringComparison.OrdinalIgnoreCase)) return Hebrew;
        if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;
        return null;
    }

    /// <summary>True when <paramref name="language"/> is Hebrew.</summary>
    public static bool IsHebrew(string language)
        => string.Equals(language, Hebrew, StringComparison.OrdinalIgnoreCase);
}
