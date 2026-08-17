namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// The conversation title, DERIVED AND NEVER GENERATED (Show C1, d1 section (2)).
///
/// <para>A model-authored title would add a GPU cost and a brand new prompt surface to a plan whose one
/// architectural rule is that the composed prompt does not change by a byte. So the title is the first
/// user message, trimmed, cut to <see cref="MaxLength"/> characters at a word boundary when one exists
/// inside that budget, with a plain three-dot ellipsis appended only when the cut actually removed
/// something. No em-dash: this string is rendered to the author.</para>
///
/// <para>Pure and static, so it is pinned by tests without a database.</para>
/// </summary>
public static class ConversationTitle
{
    /// <summary>
    /// The derivation budget. The column is wider (200) so a user's own rename has headroom; this bounds
    /// only what the derivation itself produces.
    /// </summary>
    public const int MaxLength = 80;

    /// <summary>The suffix appended when, and only when, the first message was actually cut.</summary>
    public const string Ellipsis = "...";

    /// <summary>
    /// The title for a conversation whose first message is blank. This is a FLOOR, not an expected state:
    /// <c>ProductChatController.Ask</c> rejects a blank <c>req.Question</c> with a 400
    /// (<c>string.IsNullOrWhiteSpace(req?.Question)</c>, checked before the store is ever reached), so
    /// <see cref="FromFirstMessage"/> can only receive a string with at least one non-whitespace
    /// character on the real chat path - confirmed against that guard on 2026-08-16, not merely assumed.
    /// Left as an English literal rather than built out into an i18n path: a value that can never render
    /// does not earn one. <c>ConversationTitleTests</c> (Pagedraft.Api.Tests) pins the boundary so a
    /// future change to the guard's condition is caught here instead of surfacing as an English title in
    /// a Hebrew product.
    /// </summary>
    public const string Untitled = "Untitled conversation";

    public static string FromFirstMessage(string? firstUserMessage)
    {
        var text = CollapseWhitespace(firstUserMessage ?? string.Empty);
        if (text.Length == 0) return Untitled;
        if (text.Length <= MaxLength) return text;

        var cut = text[..MaxLength];

        // Prefer a word boundary, but only one that leaves a title worth reading. A question whose first
        // 80 characters hold no space at all (a pasted URL, an unbroken Hebrew compound) gets the hard cut
        // rather than an empty title.
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace >= MaxLength / 2) cut = cut[..lastSpace];

        return cut.TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// Trims and collapses interior runs of whitespace (including the newlines a pasted question carries)
    /// to single spaces, because the title is rendered on one line of a list.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }

        return sb.ToString();
    }
}
