using System.Text;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// The per-request scratchpad the grounding snapshot is assembled in (Show C1, d1 section (1)).
///
/// <para>WHY IT EXISTS AT ALL. d1's snapshot carries a <c>selectionSummary</c>, and the facts it is made
/// of are already stated in TWO log lines the chat path writes: the retrieval/escalation line in
/// <c>BookChatContextReader.ReadAsync</c> (retrieval vs. answer language, selected chapter orders, ambient
/// resolution and match state, the clarify flag, escalated-whole vs. excerpted chapters, block and fault
/// counts) and the citation/trim line in <c>ProductChatService.AnswerAsync</c> (cited vs. selected guide
/// ids, cited vs. carried artifact refs, forwarded vs. received history turns). The snapshot is captured
/// AT THOSE TWO CALL SITES rather than re-derived from a third summary or parsed back out of log text, so
/// the stored snapshot and the log can never independently drift from one event.</para>
///
/// <para>WHY NOT A FIELD ON THE RESPONSE DTO. The summary is an internal diagnostic, not something the
/// drawer renders; putting it on the wire would make an implementation detail a client contract. A scoped
/// side channel keeps the persisted record complete without widening the public shape.</para>
///
/// <para>SCOPED, AND THEREFORE ONE PER REQUEST. Each capture OVERWRITES its half rather than appending, so
/// a scope that somehow answered twice records the last answer rather than a concatenation of two. It is
/// deliberately not thread-safe and deliberately does nothing on its own: an unset half simply does not
/// appear in the composed line, which is the normal state for a book-less turn (the book reader never
/// ran) and for a fail-safe (the citation line was never reached).</para>
/// </summary>
public sealed class ProductChatGroundingCapture
{
    /// <summary>The book-retrieval half, or null when no book context was read this request.</summary>
    public string? RetrievalSummary { get; private set; }

    /// <summary>The citation/budget half, or null when the turn never reached the citation line.</summary>
    public string? CitationSummary { get; private set; }

    /// <summary>
    /// Called at <c>BookChatContextReader.ReadAsync</c>'s selection log line, from the same values that
    /// line formats.
    /// </summary>
    public void CaptureRetrieval(
        Guid bookId,
        string retrievalLanguage,
        string answerLanguage,
        IEnumerable<int> selectedChapterOrders,
        int characterCount,
        IEnumerable<string> dimensions,
        int? ambientOrder,
        string ambientMatch,
        int? ambientUsedOrder,
        bool needsChapterClarification,
        IEnumerable<int> escalatedWhole,
        IEnumerable<int> excerpted,
        int blockCount,
        IEnumerable<string> faults)
    {
        RetrievalSummary =
            $"book={bookId}; retrieval={retrievalLanguage}; answer={answerLanguage}; " +
            $"chapters=[{Join(selectedChapterOrders)}]; characters={characterCount}; " +
            $"dimensions=[{Join(dimensions)}]; ambient={ambientOrder?.ToString() ?? "none"}/{ambientMatch}" +
            $"(used {ambientUsedOrder?.ToString() ?? "none"}); clarify={needsChapterClarification}; " +
            $"whole=[{Join(escalatedWhole)}]; excerpted=[{Join(excerpted)}]; blocks={blockCount}; " +
            $"faults=[{Join(faults)}]";
    }

    /// <summary>
    /// Called at <c>ProductChatService.AnswerAsync</c>'s answered log line, from the same values that line
    /// formats.
    /// </summary>
    public void CaptureCitation(
        string language,
        IEnumerable<string> citedGuideIds,
        IEnumerable<string> selectedGuideIds,
        IEnumerable<string> citedArtifactRefs,
        IEnumerable<string> carriedArtifactRefs,
        string? provider,
        string? model,
        int forwardedTurns,
        int receivedTurns,
        int instructionChars,
        int estimatedTokens,
        int budgetTokens)
    {
        CitationSummary =
            $"language={language}; citedGuides=[{Join(citedGuideIds)}]; selectedGuides=[{Join(selectedGuideIds)}]; " +
            $"citedArtifacts=[{Join(citedArtifactRefs)}]; carriedArtifacts=[{Join(carriedArtifactRefs)}]; " +
            $"via={provider ?? "unknown"}/{model ?? "unknown"}; history={forwardedTurns}/{receivedTurns}; " +
            $"instructionChars={instructionChars}; tokens={estimatedTokens}/{budgetTokens}";
    }

    /// <summary>
    /// The one line stored as the snapshot's <c>selectionSummary</c>, or null when neither half was
    /// captured (which is what a turn that never got as far as an answer looks like).
    /// </summary>
    public string? Compose()
    {
        if (RetrievalSummary == null && CitationSummary == null) return null;

        var sb = new StringBuilder();
        if (RetrievalSummary != null) sb.Append("retrieval: ").Append(RetrievalSummary);
        if (CitationSummary != null)
        {
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append("answer: ").Append(CitationSummary);
        }
        return sb.ToString();
    }

    private static string Join<T>(IEnumerable<T>? values)
        => values == null ? string.Empty : string.Join(", ", values);
}
