using System.Security.Cryptography;
using System.Text;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE projection of the loaded guides corpus onto the serving DTOs (chatbot phase A.2, c1).
///
/// <para>Static and side-effect free, exactly like <see cref="GuideFrontmatter"/>: it reads
/// <see cref="GuideDocument"/> records that are already in memory and touches no filesystem, no clock
/// and no configuration. That is what lets <c>GuidesController</c> stay a lookup plus a status code,
/// and it is also the security property the endpoint rests on - see
/// <see cref="Find"/>.</para>
/// </summary>
public static class GuidesCatalog
{
    /// <summary>
    /// A guide's display TITLE: its first H1.
    ///
    /// <para>THERE IS NO <c>title</c> FRONTMATTER FIELD, and one was deliberately not added. The
    /// guides' H1/H2 headings are the chatbot's retrieval index (<see cref="GuideSelector"/> scores a
    /// question's tokens against them at weight 3.0 and reads no body prose at all), so editing guide
    /// headings - or adding a competing title field that would drift from them - changes which guides
    /// reach the model. Deriving the title from the heading the document already carries keeps one
    /// source of truth for what a guide is called.</para>
    ///
    /// <para><see cref="GuideFrontmatter"/> collects H1 AND H2 text in document order, so
    /// <c>Headings[0]</c> is the first heading in the body, which every shipped guide makes its H1
    /// title. Five Hebrew files open with a "# DRAFT Hebrew stage vocabulary..." banner INSIDE the
    /// frontmatter fence, which is not part of the body and never reaches <c>Headings</c>.</para>
    ///
    /// <para>Falls back to the id rather than to an empty string: an index row with no name is worse
    /// than one named a little technically, and the id is guaranteed non-empty by the parser.</para>
    /// </summary>
    public static string TitleOf(GuideDocument doc)
        => doc.Headings.Count > 0 && !string.IsNullOrWhiteSpace(doc.Headings[0])
            ? doc.Headings[0].Trim()
            : doc.Id;

    /// <summary>
    /// Normalize a requested language tag to the corpus's own two-letter form, or null when the caller
    /// asked for nothing.
    ///
    /// <para>Prefix matching (<c>he-IL</c> -&gt; <c>he</c>) so a browser locale works, and ANY other
    /// value normalizes to itself lowercased rather than being coerced to a default: the lookup then
    /// simply finds no document and answers 404, which is more honest than silently serving English to
    /// someone who asked for French.</para>
    /// </summary>
    public static string? NormalizeLanguage(string? requested)
    {
        var value = (requested ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return null;
        if (value.StartsWith("he", StringComparison.Ordinal)) return "he";
        if (value.StartsWith("en", StringComparison.Ordinal)) return "en";
        return value;
    }

    /// <summary>
    /// THE ONLY WAY A REQUEST REACHES A DOCUMENT, and the reason path traversal is not a risk on this
    /// endpoint: a caller-supplied id is compared against the ids of documents ALREADY LOADED IN
    /// MEMORY. No part of the request is ever concatenated into a path, passed to
    /// <c>Path.Combine</c>, or used to open a file. <c>../../appsettings</c> is not a path here, it is
    /// a string that matches no id, so it produces the same 404 as <c>banana</c>.
    /// </summary>
    public static GuideDocument? Find(GuidesCorpus corpus, string? id, string language)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var wanted = id.Trim();
        foreach (var doc in corpus.Documents)
        {
            if (string.Equals(doc.Id, wanted, StringComparison.Ordinal) &&
                string.Equals(doc.Lang, language, StringComparison.OrdinalIgnoreCase))
                return doc;
        }

        return null;
    }

    /// <summary>Every language a given id ships in, ordinal-sorted. Drives the 404 that can say "this
    /// guide exists, just not in that language" instead of "no such guide".</summary>
    public static IReadOnlyList<string> LanguagesFor(GuidesCorpus corpus, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<string>();
        var wanted = id.Trim();
        return corpus.Documents
            .Where(d => string.Equals(d.Id, wanted, StringComparison.Ordinal))
            .Select(d => d.Lang)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The index projection: frontmatter plus the derived title, no body.</summary>
    public static GuideSummaryDto ToSummary(GuideDocument doc)
        => new(doc.Id, doc.Stage, doc.Audience, doc.Lang, TitleOf(doc), doc.Updated, doc.NumericPrefix);

    /// <summary>The reader projection: the summary fields plus the markdown body.</summary>
    public static GuideContentDto ToContent(GuideDocument doc)
        => new(doc.Id, doc.Stage, doc.Audience, doc.Lang, TitleOf(doc), doc.Updated, doc.NumericPrefix, doc.Body);

    /// <summary>
    /// The index, in the corpus's authored order (numeric filename prefix, then id, then language) and
    /// optionally narrowed to one language.
    ///
    /// <para>Ordering is done HERE rather than left to the client because it is a property of the
    /// corpus (the numbers are the workflow sequence the guides were written in), and because a stable
    /// order is what makes the ETag below stable.</para>
    /// </summary>
    public static IReadOnlyList<GuideSummaryDto> Index(GuidesCorpus corpus, string? language)
        => corpus.Documents
            .Where(d => language == null || string.Equals(d.Lang, language, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.NumericPrefix)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Lang, StringComparer.Ordinal)
            .Select(ToSummary)
            .ToList();

    /// <summary>
    /// A strong ETag over the exact bytes a response would carry.
    ///
    /// <para>The guides change only at DEPLOY, so a content hash is a better validator than any date:
    /// it is stable across restarts and across two servers behind a load balancer (a
    /// <c>Last-Modified</c> read off the filesystem is neither), and it changes the moment a guide is
    /// edited. Truncated to 128 bits, which is a validator, not a signature.</para>
    /// </summary>
    public static string ETagFor(params string[] parts)
    {
        // Unit separator between parts, so two different splits of the same characters ("ab"+"c" and
        // "a"+"bc") cannot hash alike.
        var canonical = string.Join('\u001f', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "\"" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"";
    }

    /// <summary>The ETag of an index response: every row's identity and freshness, in order.</summary>
    public static string ETagForIndex(IReadOnlyList<GuideSummaryDto> guides)
        => ETagFor(guides.Select(g => $"{g.Id}|{g.Language}|{g.Updated}|{g.Title}").Prepend("guides-index-v1").ToArray());

    /// <summary>The ETag of one guide: its metadata AND its body, so a body-only edit invalidates it.</summary>
    public static string ETagForContent(GuideContentDto guide)
        => ETagFor("guide-v1", guide.Id, guide.Language, guide.Updated, guide.Title, guide.Body);
}
