namespace Pagedraft.Api.Services.Chat;

// ─── The product-guides retrieval corpus (chatbot phase A, c1) ───────────────────────────────────
//
// These types are the shipped guide files (Pagedraft.Api/Content/guides/**/*.md) as the retrieval
// layer sees them. Nothing here talks to a model: the corpus is read from disk, the frontmatter is
// parsed, and GuideSelector ranks over these records deterministically. That separation is the
// point - the selector is the component most likely to regress silently and the only one that can
// be pinned without a GPU.

/// <summary>
/// One shipped guide file, parsed. Both members of an en/he PAIR carry the SAME
/// <paramref name="Id"/> (e.g. <c>50-export.md</c> and <c>50-export.he.md</c> are both
/// <c>id: export</c>) and differ only in <paramref name="Lang"/>. That is deliberate and is why the
/// citation the chat endpoint returns is language-neutral: an author who asked in Hebrew and an
/// author who asked in English are told they were answered from the same guide.
///
/// <para><paramref name="Lang"/> is the AUTHORITATIVE language signal, taken from the explicit
/// <c>lang:</c> frontmatter field (d1 item 1). The <c>.he.md</c> filename suffix is a CROSS-CHECK
/// only: when the two disagree the frontmatter wins and the reader logs a warning, because a file
/// renamed without its frontmatter being updated is a content bug someone has to see, not a reason
/// to guess.</para>
///
/// <para><paramref name="NumericPrefix"/> is the leading number in the filename (00, 10, 20 ...),
/// used as the selector's deterministic tie-break. <c>README.md</c> has no numeric prefix and gets
/// <see cref="int.MaxValue"/> so it sorts last among equally-scored documents: it is an index page,
/// not a stage guide, and it is the one file with NO Hebrew sibling.</para>
///
/// <para><paramref name="Body"/> is the whole file with the frontmatter block removed. Whole files
/// are passed to the model rather than fragments (d1 item 1 step 4): these documents are short and
/// workflow-anchored, so a fragment loses the dependency reasoning ("review needs briefs first")
/// that makes an answer correct.</para>
/// </summary>
public sealed record GuideDocument(
    string Id,
    string Stage,
    string Audience,
    string Updated,
    string Lang,
    string FileName,
    int NumericPrefix,
    IReadOnlyList<string> Headings,
    string Body);

/// <summary>
/// The whole corpus as one load attempt: either documents, or a <paramref name="Fault"/> saying why
/// there are none.
///
/// <para>THE FAULT IS THE FEATURE. The failure this plan exists to prevent is a confident answer
/// assembled from the model's own priors when grounding was unavailable, so "no corpus" must be a
/// value the caller has to look at rather than an empty list that reads exactly like a corpus with
/// nothing relevant in it. <paramref name="Fault"/> is null on success and one of
/// <see cref="ProductChatFaults"/> otherwise.</para>
///
/// <para><paramref name="UnparseableFileCount"/> is reported even on a SUCCESSFUL load: a corpus
/// that loaded 14 of 15 files answers questions while silently missing a guide, which is exactly the
/// kind of partial failure that ships invisibly. The reader logs each one by name.</para>
/// </summary>
public sealed record GuidesCorpus(
    IReadOnlyList<GuideDocument> Documents,
    string? Fault,
    string ResolvedDirectory,
    int UnparseableFileCount)
{
    /// <summary>True when the corpus can ground an answer: no fault AND at least one document.</summary>
    public bool CanGround => Fault == null && Documents.Count > 0;
}

/// <summary>
/// MACHINE-READABLE fault reasons, carried on the wire so the client can render an honest failure
/// state distinctly from an answer (and so a log line names a cause rather than a sentence).
/// Deliberately strings, not an enum: this API registers no <c>JsonStringEnumConverter</c>, so an
/// enum would go out as an integer and the client would have to special-case it.
/// </summary>
public static class ProductChatFaults
{
    /// <summary>The guides directory does not exist, or could not be enumerated.</summary>
    public const string GuidesUnavailable = "guides-unavailable";

    /// <summary>The directory exists but no file in it parsed into a guide.</summary>
    public const string GuidesEmpty = "guides-empty";

    /// <summary>The corpus loaded, but the routed model could not be reached or threw.</summary>
    public const string ModelUnavailable = "model-unavailable";

    /// <summary>The corpus loaded and the model answered, but the answer was blank.</summary>
    public const string EmptyAnswer = "empty-answer";
}
