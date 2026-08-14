using System.Text;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE punctuation repair on the answer text, for the one workspace rule that authored strings can
/// honour and MODEL OUTPUT cannot: no em-dash in user-facing text (chatbot phase A, review finding 17).
///
/// <para>WHY A LAYER AT ALL. Every authored string in this feature is em-dash free and a test pins it
/// (<c>NoUserFacingStringContainsAnEmDash</c>), but the answer the user reads is written by the model,
/// which no authored-string discipline reaches. Two live measurements caught it leaking anyway: 1 of
/// g1's 72 answers ("out of date or fails[em-dash]but") and 1 of g2's 102 ("no separate scope
/// control[em-dash]the pass runs"). Both were GLUED between two words, which is what decides the
/// replacement below.</para>
///
/// <para>AND THE MODEL'S ACTUAL RATE IS HIGHER THAN THOSE TWO SUGGEST. g1 and g2 ran without this
/// layer, so they counted only the em-dashes that survived into an answer someone then read; g3 ran
/// WITH it and its repair log fired on 5 of 108 answers (4.6%, all English), which is the first
/// measurement of what the model produces rather than of what a reader happened to notice. The
/// service's warning line quotes g3's figure for that reason.</para>
///
/// <para>THE REPLACEMENT IS A COMMA, because that is what this product already answers when it is
/// asked the same question: <c>PromptFactory</c> tells the model, four times over, "Do not use an
/// em-dash ...; use a comma, a period, or parentheses instead", and the shipped guides carry zero
/// em-dashes and zero en-dashes, writing the same break as a comma or a colon. It is not a guess.</para>
///
/// <para>THE EN-DASH (U+2013) IS DELIBERATELY LEFT ALONE. The convention names the em-dash and the
/// test that pins it checks the em-dash; extending a silent rewrite to the en-dash would buy nothing
/// measured (neither g1's 72 answers nor g2's 102 recorded one, and the corpus contains none) and
/// would cost meaning, because the en-dash's ordinary job is a RANGE - "chapters 3-8", "2024-2026" -
/// where a comma turns a span into a list. A style miss is cosmetic; silently rewriting a range is
/// content damage, and this layer runs on every answer with no human in the loop.</para>
///
/// <para>SHAPE GUARDS, in the spirit of the other fail-safe repairs here.</para>
/// <list type="bullet">
///   <item>Only U+2014 is ever consumed, plus the spaces/tabs immediately around it. A guide id is an
///   ASCII slug written with HYPHEN-MINUS (<c>chapter-editing-passes</c>), so no id can be rewritten by
///   construction, in the prose or on a citation line this turn's parser refused.</item>
///   <item>Text inside backticks is copied verbatim: a code span is content, not prose. An unbalanced
///   backtick therefore leaves the rest of the answer untouched, which fails in the safe direction (an
///   em-dash survives) rather than by mangling a span. <c>ProductChatInternalLabels</c> (review finding
///   A14) agrees with this policy for a FENCED block, but DELIBERATELY DISAGREES for a bare inline span: it
///   still removes an internal token found inside one, because a wire ref in backticks is styling for a
///   leak, not a real code example, and leaving it would risk a stray unmatched backtick reaching THIS
///   layer's own parity toggle above. See that class's A14 note for the full reasoning.</item>
///   <item>The result is bounded, and it never grows by more than ONE character per em-dash removed:
///   a spaced dash gives back a character, a dash that opens or ends a line is dropped outright, a
///   dash following punctuation that already ends a clause is length-neutral, and only the glued case
///   (the two measured ones) costs one character, because "fails,but" would trade the style defect for
///   a worse one. The count is returned so the caller can log it.</item>
/// </list>
/// </summary>
public static class ProductChatPunctuation
{
    /// <summary>U+2014, written as an escape so this rule is unambiguous in the source it governs.</summary>
    internal const char EmDash = '\u2014';

    /// <summary>Punctuation that already ends a clause: a comma after it would double up.</summary>
    private static readonly char[] ClauseEnders = { ',', ';', ':', '.', '!', '?' };

    /// <summary>
    /// Replaces every em-dash outside a code span, returning the repaired text and HOW MANY were
    /// replaced. The count is the observability half of this layer: a silent rewrite of model output
    /// that says nothing is the shape of failure this codebase has shipped before, and the count is
    /// the only thing that would say so if a prompt or corpus change started producing em-dashes at
    /// scale. It is a count, never the text.
    /// </summary>
    public static (string Text, int Replacements) Repair(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(EmDash) < 0) return (text ?? string.Empty, 0);

        var sb = new StringBuilder(text.Length + 8);
        var replaced = 0;
        var inCodeSpan = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            // A run of backticks opens or closes a code span / fenced block. Copied verbatim, and it
            // flips the protection: nothing inside is prose.
            if (c == '`')
            {
                var start = i;
                while (i < text.Length && text[i] == '`') i++;
                sb.Append(text, start, i - start);
                inCodeSpan = !inCodeSpan;
                continue;
            }

            if (c != EmDash || inCodeSpan)
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Consume the dash run plus the horizontal whitespace on both sides, so " x " collapses
            // rather than leaving a stranded space behind the comma.
            while (i < text.Length && (text[i] == EmDash || IsHorizontalSpace(text[i])))
            {
                if (text[i] == EmDash) replaced++;
                i++;
            }

            while (sb.Length > 0 && IsHorizontalSpace(sb[^1])) sb.Length--;

            var opensTheLine = sb.Length == 0 || sb[^1] == '\n';
            var endsTheLine = i >= text.Length || text[i] == '\n' || text[i] == '\r';

            // A dash that opens a line is a bullet or a speech dash and a dash that ends one is a
            // trailing flourish: neither joins two clauses, so neither earns a comma.
            if (opensTheLine || endsTheLine) continue;

            if (Array.IndexOf(ClauseEnders, sb[^1]) >= 0) sb.Append(' ');
            else sb.Append(", ");
        }

        return (sb.ToString(), replaced);
    }

    private static bool IsHorizontalSpace(char c) => c == ' ' || c == '\t';
}
