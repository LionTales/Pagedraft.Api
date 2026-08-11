using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// The shipped product guides, served READ-ONLY so the app can actually show them (chatbot phase A.2,
/// c1).
///
/// <para>WHY THIS EXISTS. The product assistant has cited guide ids at authors since phase A, at an
/// author who had no way to open one: the corpus lived on the server and no surface could display it.
/// This controller is the missing half. It is deliberately a GENERAL guides endpoint family rather
/// than an FAQ-shaped one-off, because a second consumer is already scheduled (Wave 3's first-run
/// orientation reads the same corpus) and an FAQ-only endpoint would have been rebuilt within a
/// wave.</para>
///
/// <para>ONE CORPUS, ONE LOADER. This reads <see cref="GuidesCorpusReader"/> - the very same
/// singleton, with the very same process-lifetime cache, that grounds
/// <see cref="ProductChatService"/>. A second reader over the same directory would be a second answer
/// to "what is in the corpus", and the two would drift the first time one of them grew a filter.
/// <see cref="GuidesCatalog"/> does the projection and is pure.</para>
///
/// <para>PATH SAFETY, stated because it is the design and not a mitigation. The content endpoint takes
/// a caller-supplied id, and that id is NEVER turned into a path: it is looked up by ordinal string
/// comparison against the ids of documents already parsed into memory
/// (<see cref="GuidesCatalog.Find"/>). There is no <c>Path.Combine</c>, no <c>File.Open</c> and no
/// directory access anywhere on the request path, so a traversal-shaped id is simply a string that
/// matches nothing and gets the ordinary 404. The 404 body carries no id, no path and no directory
/// name.</para>
///
/// <para>NO AUTH, ON PURPOSE. The guides are shipped documentation about the product, identical for
/// every author, and this API has no user model to scope them to. Read-only GETs with no
/// personalization are the whole surface.</para>
///
/// <para>CACHING. The corpus changes only at deploy, so both endpoints emit a strong content-derived
/// <c>ETag</c> plus <c>Cache-Control: public, max-age=300</c>, and honour <c>If-None-Match</c> with a
/// 304. The short max-age with a validator is the right trade for content that is stable for weeks and
/// then changes in a deploy: a reader who has the page open when a deploy lands revalidates within
/// five minutes instead of holding a stale guide for an hour. FAILURE responses are
/// <c>no-store</c>: a 503 from a deployment that lost its Content folder must not be cached into
/// looking like a product with no documentation.</para>
/// </summary>
[ApiController]
[Route("api/guides")]
public class GuidesController : ControllerBase
{
    /// <summary>Seconds a client may reuse a guides response before revalidating. See the class doc.</summary>
    public const int CacheMaxAgeSeconds = 300;

    private readonly GuidesCorpusReader _reader;
    private readonly ILogger<GuidesController> _logger;

    public GuidesController(GuidesCorpusReader reader, ILogger<GuidesController> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    /// <summary>
    /// <c>GET /api/guides?language=he</c> - the INDEX: every guide's frontmatter plus its derived
    /// title, and no bodies, so a client can build a grouped list in one request.
    ///
    /// <para><c>language</c> is optional. Omitted, the response carries BOTH languages (the corpus is
    /// bilingual and a caller may want to know a Hebrew sibling exists before asking for it); supplied,
    /// it narrows to that one. An unrecognized tag narrows to nothing rather than falling back to
    /// English.</para>
    ///
    /// <para>200 with the index, or 503 with a <c>fault</c> when the corpus could not be read at all.
    /// A missing corpus is deliberately NOT a 200 with an empty list: those are different facts, and
    /// the empty-list rendering of a broken deployment is a product that looks undocumented.</para>
    /// </summary>
    [HttpGet]
    public ActionResult<GuideListResponseDto> List([FromQuery] string? language)
    {
        var corpus = _reader.Load();
        if (!corpus.CanGround)
        {
            _logger.LogWarning(
                "Guides index requested but the corpus is unavailable ({Fault}). The reader page will show " +
                "an honest failure. See the GuidesCorpusReader warnings above for the directory and reason.",
                corpus.Fault);
            NoStore();
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new GuideListResponseDto(Array.Empty<GuideSummaryDto>(), 0, corpus.Fault ?? ProductChatFaults.GuidesEmpty));
        }

        var guides = GuidesCatalog.Index(corpus, GuidesCatalog.NormalizeLanguage(language));
        var etag = GuidesCatalog.ETagForIndex(guides);
        if (NotModified(etag)) return StatusCode(StatusCodes.Status304NotModified);

        return Ok(new GuideListResponseDto(guides, guides.Count, null));
    }

    /// <summary>
    /// <c>GET /api/guides/{id}?language=he</c> - one guide's markdown body plus its metadata.
    ///
    /// <para><c>language</c> defaults to English (the corpus's own default), and asking for Hebrew
    /// returns the <c>.he.md</c> SIBLING FILE - a separately authored document with the same id, never
    /// a translation of the English one. That is why the language is part of the lookup rather than a
    /// rendering hint.</para>
    ///
    /// <para>200 with the guide; 404 when no document has that id in that language; 503 when the
    /// corpus itself is unavailable. The 404 distinguishes <c>guideNotFound</c> from
    /// <c>guideLanguageUnavailable</c> and, in the latter case, lists the languages the id DOES ship
    /// in, so a reader can offer "read it in English" instead of a dead end. Neither body echoes the
    /// requested id or names anything on disk.</para>
    /// </summary>
    [HttpGet("{id}")]
    public ActionResult<GuideContentDto> Get(string id, [FromQuery] string? language)
    {
        var corpus = _reader.Load();
        if (!corpus.CanGround)
        {
            _logger.LogWarning(
                "Guide content requested but the corpus is unavailable ({Fault}).", corpus.Fault);
            NoStore();
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new GuideListResponseDto(Array.Empty<GuideSummaryDto>(), 0, corpus.Fault ?? ProductChatFaults.GuidesEmpty));
        }

        var lang = GuidesCatalog.NormalizeLanguage(language) ?? GuideFrontmatter.DefaultLang;
        var document = GuidesCatalog.Find(corpus, id, lang);
        if (document == null)
        {
            var available = GuidesCatalog.LanguagesFor(corpus, id);
            // OBSERVABILITY: the only producers of guide ids in this product are our own citation chips
            // and our own index, so a miss usually means one of OUR links is broken, not that a user
            // typed something. The requested id is logged (TRUNCATED, since it is caller-supplied and a
            // log line is not a place to accept unbounded input) with the language, and nothing else -
            // no body, no path.
            _logger.LogWarning(
                "Guide not found: id '{GuideId}' in language '{Language}'. Available languages for that id: " +
                "[{AvailableLanguages}]. A citation chip or an index link may be pointing at a guide that no " +
                "longer ships under that id.",
                Truncate(id), lang, string.Join(", ", available));

            NoStore();
            return NotFound(available.Count > 0
                ? new GuideNotFoundDto("guideLanguageUnavailable", available)
                : new GuideNotFoundDto("guideNotFound", Array.Empty<string>()));
        }

        var content = GuidesCatalog.ToContent(document);
        var etag = GuidesCatalog.ETagForContent(content);
        if (NotModified(etag)) return StatusCode(StatusCodes.Status304NotModified);

        return Ok(content);
    }

    // ── Cache plumbing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps the validator and the freshness lifetime, and reports whether the caller already holds
    /// this exact payload. The headers are set BEFORE the comparison so a 304 carries them too: a 304
    /// that dropped its own ETag would make the next request unconditional again.
    /// </summary>
    private bool NotModified(string etag)
    {
        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers[HeaderNames.CacheControl] = $"public, max-age={CacheMaxAgeSeconds}";

        foreach (var candidate in Request.Headers[HeaderNames.IfNoneMatch])
        {
            if (candidate == null) continue;
            foreach (var part in candidate.Split(','))
            {
                var trimmed = part.Trim();
                // WEAK comparison. RFC 9110 section 8.8.3.2 DEFINES the two comparison functions;
                // section 13.1.2 is where If-None-Match is specified to use the WEAK one (only the
                // opaque tag has to match; a leading W/ on either side is a strength marker, not part
                // of the identity). If-Match, section 13.1.1, uses STRONG comparison and would stay
                // ordinal - do not fold that case into this one. An intermediary
                // is explicitly allowed to weaken a strong tag it forwards (a CDN that transforms the
                // body, for instance), so comparing this controller's strong emitted tag ordinally
                // against a weakened If-None-Match value rejects a client that is holding the exact
                // content we already emitted, and the "no change" case degrades into a full 200 with
                // the whole markdown body every time.
                if (trimmed == "*" || string.Equals(StripWeakPrefix(trimmed), StripWeakPrefix(etag), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Strips a leading weak-validator marker (<c>W/</c>) so two tags can be compared by
    /// their opaque quoted string alone, per RFC 9110's weak comparison function.</summary>
    private static string StripWeakPrefix(string value) =>
        value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;

    private void NoStore() => Response.Headers[HeaderNames.CacheControl] = "no-store";

    /// <summary>Caller-supplied text, bounded before it reaches a log line.</summary>
    private static string Truncate(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length <= 80 ? text : text[..80] + "...";
    }
}
