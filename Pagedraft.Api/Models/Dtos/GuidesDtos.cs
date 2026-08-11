namespace Pagedraft.Api.Models.Dtos;

// ─── The guides SERVING contract (chatbot phase A.2, c1) ─────────────────────────────────────────
//
// These DTOs are the read-only, user-facing view of the SAME corpus the product chat is grounded in
// (Pagedraft.Api/Content/guides/**/*.md, loaded once by GuidesCorpusReader). One corpus, one loader,
// two consumers: the chat cites a guide id, and now a reader page can actually open that id. A third
// consumer (Wave 3's first-run orientation) is the reason this is a general guides endpoint family
// rather than an FAQ-shaped one-off.
//
// JSON casing is the System.Text.Json default this API already uses everywhere (camelCase); nothing
// here is an enum, matching this folder's standing convention.
//
// WHAT IS DELIBERATELY ABSENT: the file name and the resolved directory. The client never needs
// either, and the endpoint's whole path-safety argument is that a request names a guide by its
// frontmatter ID and the server answers by LOOKING THAT ID UP IN MEMORY. Putting a filename on the
// wire would invite a caller to start composing paths out of it.

/// <summary>
/// One guide as the INDEX sees it: everything needed to render a list, and no body.
///
/// <para>JSON (camelCase): <c>{ "id", "stage", "audience", "language", "title", "updated", "order" }</c>.</para>
///
/// <para><paramref name="Title"/> IS DERIVED, NOT A FRONTMATTER FIELD. The guides' frontmatter carries
/// exactly <c>id</c>, <c>stage</c>, <c>audience</c>, <c>updated</c> and <c>lang</c> - there is no
/// <c>title</c> key - so the title served here is the guide's FIRST H1, i.e.
/// <c>GuideDocument.Headings[0]</c>, falling back to the id when a document somehow has no heading. A
/// title field was deliberately NOT added to the guide files: those headings are also the retrieval
/// index (<c>GuideSelector</c> scores question tokens against H1/H2 at weight 3.0 and reads no body
/// prose at all), so editing the guides to feed this endpoint would silently re-rank which guides
/// reach the chatbot.</para>
///
/// <para><paramref name="Language"/> is the guide's own <c>lang</c>. An en/he PAIR shares one
/// <paramref name="Id"/> and differs only here, which is what makes a language toggle in the reader a
/// re-fetch of the SIBLING FILE rather than a translation of anything.</para>
///
/// <para><paramref name="Order"/> is the guide's numeric filename prefix (00, 10, 20 ...), so a client
/// can present the corpus in its authored workflow order without knowing the filenames. An unnumbered
/// document (the README index page) gets <c>int.MaxValue</c> and therefore sorts last.</para>
/// </summary>
public record GuideSummaryDto(
    string Id,
    string Stage,
    string Audience,
    string Language,
    string Title,
    string Updated,
    int Order);

/// <summary>
/// Response for <c>GET /api/guides</c>. JSON (camelCase):
/// <c>{ "guides": [ ... ], "count": 15, "fault": null }</c>.
///
/// <para><paramref name="Fault"/> is null on success and one of <c>ProductChatFaults</c>
/// (<c>guides-unavailable</c> / <c>guides-empty</c>) when the corpus could not be read, in which case
/// the endpoint answers 503 rather than 200-with-an-empty-list. The distinction is the same one the
/// chat's grounding contract rests on: "no guides" and "no matching guides" are different facts, and a
/// help page that renders an empty index because a deployment lost its Content folder would look like
/// a product with no documentation.</para>
///
/// <para><paramref name="Count"/> is redundant with <c>guides.length</c> on purpose: it is the field a
/// smoke check reads, and it makes an empty payload obviously empty in a log or a network panel.</para>
/// </summary>
public record GuideListResponseDto(
    IReadOnlyList<GuideSummaryDto> Guides,
    int Count,
    string? Fault);

/// <summary>
/// Response for <c>GET /api/guides/{id}</c>. The summary fields plus the markdown
/// <paramref name="Body"/> (the file with its frontmatter block removed).
///
/// <para>The body is MARKDOWN, not HTML: rendering is the client's job, through the one markdown
/// component it already ships. The server sending HTML would put an injection surface on a path that
/// currently has none.</para>
/// </summary>
public record GuideContentDto(
    string Id,
    string Stage,
    string Audience,
    string Language,
    string Title,
    string Updated,
    int Order,
    string Body);

/// <summary>
/// 404 body for <c>GET /api/guides/{id}</c>. JSON (camelCase):
/// <c>{ "error": "guideNotFound" | "guideLanguageUnavailable", "availableLanguages": ["en"] }</c>.
///
/// <para>NOTHING FROM THE FILESYSTEM IS ECHOED - not the requested id, not a path, not a directory.
/// The id a caller sent is already known to the caller, and a 404 that quotes it back is the shape
/// that turns a scanner's probe into a reflected string somewhere downstream.</para>
///
/// <para>The two codes are a real distinction rather than decoration: an id that exists only in the
/// other language (the README index page ships English-only) is a guide the reader CAN offer, so the
/// client can say "read it in English" instead of "not found". <paramref name="AvailableLanguages"/>
/// is empty for a genuinely unknown id.</para>
/// </summary>
public record GuideNotFoundDto(
    string Error,
    IReadOnlyList<string> AvailableLanguages);
