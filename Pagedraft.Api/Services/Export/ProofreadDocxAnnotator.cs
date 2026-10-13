using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Pagedraft.Api.Services.Export;

/// <summary>Output of <see cref="ProofreadDocxAnnotator.Annotate"/>.</summary>
/// <param name="Docx">The annotated package; the INPUT array itself when nothing was placed.</param>
/// <param name="Placed">
/// Pending suggestions written as tracked change(s), whether or not they also got a comment (only those with
/// a category label or an explanation do; see <see cref="ProofreadAnnotationStrings.CommentLines"/>).
/// </param>
/// <param name="Unanchored">
/// Pending suggestions that could not be placed: no match, an ambiguous match the contexts do not settle,
/// a span crossing a paragraph or touching text that is not plain body text (a field result, an existing
/// tracked insertion, a tab or break), a span or point that would cut a surrogate pair in two, a pure insertion
/// whose text is empty once made legal for XML (<see cref="XmlSafeText"/>), or a span overlapping one placed
/// before it.
/// </param>
/// <param name="WhitespaceSkipped">
/// Pending suggestions that change whitespace only (or nothing), by the client's own hide rule
/// (<see cref="ProofreadWhitespace.IsWhitespaceOnlyChange"/>): never written, and neither placed nor unanchored.
/// <c>Placed + Unanchored + WhitespaceSkipped</c> is always the number of pending suggestions.
/// </param>
public sealed record ProofreadAnnotationResult(byte[] Docx, int Placed, int Unanchored, int WhitespaceSkipped);

/// <summary>
/// Writes pending proofread suggestions into a DOCX as Word tracked changes (<c>w:del</c> / <c>w:ins</c>),
/// using the Open XML SDK only. A suggestion gets a ranged Word comment ONLY when it carries information the
/// change does not show (the Academy full-spelling label, or an explanation that says something); a plain
/// suggestion is its tracked change alone, so the author reviews it with one Accept or Reject on the
/// selection (owner decision 2026-10-12: a comment restating the change cost a third click per suggestion,
/// and Word's Accept never clears a comment).
///
/// <para>WHY HAND-WRITTEN XML: DocIO's <c>TrackChanges = true</c> records NOTHING for API edits on the shipped
/// 32.1.24 (five operations measured, 0 revisions each; plan whole-book-proofread-and-annotated-docx-2026-10-11).
/// Mixing DocIO comments with Open XML revisions on one package is avoided, so this writes both.</para>
///
/// <para>ANCHORING: a suggestion's <c>OriginalText</c> and contexts are on the NORMALIZED axis of
/// <c>SourceTextSnapshot</c> while the DOCX is raw, so the body's paragraphs are joined with a line break,
/// normalized through <see cref="NormalizedTextMap"/> (the server's own rules, mirrored), and searched there.
/// The rule is the client's <c>suggestion-anchor.service.ts</c> <c>relocateOne</c>: a unique match wins, several
/// matches are settled by <c>ContextBefore</c>/<c>ContextAfter</c> inside a 60-character window, anything
/// else is counted as unanchored rather than guessed. Offsets are not trusted: the export's paragraphs and
/// the snapshot need not share a coordinate system.</para>
///
/// <para>Only pending suggestions (<c>Outcome == null</c>) are written: an Accepted one is already in the
/// text, Dismissed/Reverted/Superseded ones are not proposals any more. They are taken in
/// <c>StartOffset</c> order and one that overlaps an earlier placed one is dropped (counted unanchored).</para>
///
/// <para>WHITESPACE (plan c16; c15 measured 86% of a 4,676-suggestion book changing whitespace only). A
/// suggestion that changes nothing but whitespace is skipped and counted apart, under the client's own hide
/// rule. A real edit is located with the edge whitespace both sides share taken off; when that form cannot be
/// placed, it is tried again with all of the original's edge whitespace off, so an edit fused with a paragraph
/// boundary (<c>" כול" -&gt; "כל"</c>, the space being the separator) lands on the word and not on the paragraph
/// mark, while a word deleted with its space inside a paragraph still takes the space with it. The rules are
/// <see cref="ProofreadWhitespace"/>'s.</para>
/// </summary>
public sealed partial class ProofreadDocxAnnotator
{
    /// <summary>Same window as the client's <c>CONTEXT_WINDOW</c>.</summary>
    internal const int ContextWindow = 60;

    private readonly ILogger<ProofreadDocxAnnotator> _logger;

    public ProofreadDocxAnnotator(ILogger<ProofreadDocxAnnotator> logger)
    {
        _logger = logger;
    }

    public ProofreadAnnotationResult Annotate(
        byte[] docx, IReadOnlyList<AnalysisSuggestion> suggestions, string language, DateTime stamp)
    {
        ArgumentNullException.ThrowIfNull(docx);

        var whitespaceSkipped = 0;
        var candidates = new List<Candidate>();
        foreach (var s in suggestions ?? Array.Empty<AnalysisSuggestion>())
        {
            if (s.Outcome != null) continue;
            if (ProofreadWhitespace.IsWhitespaceOnlyChange(s.OriginalText, s.SuggestedText))
            {
                whitespaceSkipped++;
                continue;
            }
            candidates.Add(Candidate.Of(s));
        }
        if (whitespaceSkipped > 0)
            _logger.LogDebug("Proofread annotation: {Count} whitespace-only suggestion(s) skipped", whitespaceSkipped);

        // Ordered by where the edit's own text starts, past the original's leading whitespace.
        var pending = candidates.OrderBy(c => c.Start).ThenBy(c => c.Suggestion.OrderIndex).ToList();
        if (pending.Count == 0)
            return new ProofreadAnnotationResult(docx, 0, 0, whitespaceSkipped);

        using var stream = new MemoryStream();
        stream.Write(docx, 0, docx.Length);
        stream.Position = 0;

        var placed = new List<Edit>();
        var unanchored = 0;
        using (var package = WordprocessingDocument.Open(stream, true))
        {
            var main = package.MainDocumentPart;
            var body = main?.Document?.Body;
            if (main == null || body == null)
                return new ProofreadAnnotationResult(docx, 0, pending.Count, whitespaceSkipped);

            var text = DocumentText.Build(body);
            foreach (var candidate in pending)
            {
                var edit = Resolve(candidate, text, out var reason);
                if (edit != null && placed.Any(p => p.Conflicts(edit)))
                {
                    edit = null;
                    reason = "overlaps-placed";
                }
                if (edit == null)
                {
                    unanchored++;
                    _logger.LogDebug(
                        "Proofread annotation: suggestion {SuggestionId} not placed ({Reason})", candidate.Suggestion.Id, reason);
                    continue;
                }
                placed.Add(edit);
            }

            // Nothing to write: hand back the caller's own bytes, untouched.
            if (placed.Count == 0)
                return new ProofreadAnnotationResult(docx, 0, unanchored, whitespaceSkipped);

            WriteEdits(main, text, placed, language, stamp);
        }

        return new ProofreadAnnotationResult(stream.ToArray(), placed.Count, unanchored, whitespaceSkipped);
    }

    // ---------------------------------------------------------------- resolution

    /// <summary>
    /// A pending, not whitespace-only suggestion as it is located, its original on the normalized axis and its
    /// suggested text through <see cref="XmlSafeText.Clean"/>, in up to
    /// two forms: <see cref="Original"/>/<see cref="Suggested"/> with the edge whitespace both sides share taken off
    /// (<see cref="ProofreadWhitespace.TrimCommonEdges"/>), tried first; and, when it differs, a fallback with ALL of
    /// the original's edge whitespace off (<see cref="ProofreadWhitespace.TrimBoundaryEdges"/>), tried only when the
    /// first cannot be placed. <see cref="Start"/> is the stored start shifted past the original's leading
    /// whitespace (where the edit's own text begins) and orders the candidates.
    /// </summary>
    private sealed record Candidate(
        AnalysisSuggestion Suggestion, string Original, string Suggested, int Start,
        string? FallbackOriginal, string? FallbackSuggested)
    {
        public static Candidate Of(AnalysisSuggestion s)
        {
            var normalized = TextNormalization.NormalizeTextForAnalysis(s.OriginalText ?? string.Empty) ?? string.Empty;
            // The suggested text is model output that is only ever WRITTEN (never matched), so it is made legal for
            // the XML part here, where it enters (be-c06): every later decision (an insertion that cleans to nothing
            // is "empty", unanchored) sees exactly what would be written. The original is matched, so it is not.
            var raw = XmlSafeText.Clean(s.SuggestedText) ?? string.Empty;
            var (original, suggested, _) = ProofreadWhitespace.TrimCommonEdges(normalized, raw);
            var (boundaryOriginal, boundarySuggested, lead) = ProofreadWhitespace.TrimBoundaryEdges(normalized, raw);
            var start = s.StartOffset is int at ? at + lead : int.MaxValue;
            var same = boundaryOriginal == original && boundarySuggested == suggested;
            return new Candidate(
                s, original, suggested, start, same ? null : boundaryOriginal, same ? null : boundarySuggested);
        }
    }

    /// <summary>
    /// Locates <paramref name="candidate"/> in its first form and, when that cannot be placed, in its boundary
    /// fallback (the reason reported is the last form's).
    /// </summary>
    private static Edit? Resolve(Candidate candidate, DocumentText text, out string reason)
    {
        var edit = Resolve(candidate.Suggestion, candidate.Original, candidate.Suggested, text, out reason);
        if (edit == null && candidate.FallbackOriginal != null)
            edit = Resolve(candidate.Suggestion, candidate.FallbackOriginal, candidate.FallbackSuggested!, text, out reason);
        return edit;
    }

    private static Edit? Resolve(
        AnalysisSuggestion s, string original, string suggested, DocumentText text, out string reason)
    {
        var map = text.Map;

        if (original.Length == 0)
            return ResolveInsertion(s, text, suggested, out reason);

        var matches = text.Occurrences(original);
        int? at = matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => string.IsNullOrEmpty(s.ContextBefore) && string.IsNullOrEmpty(s.ContextAfter)
                ? null
                : PickBestMatch(matches, original.Length, map.Text, s),
        };
        if (at == null)
        {
            reason = matches.Count == 0 ? "no-match" : "ambiguous";
            return null;
        }

        var (rawStart, rawEnd) = map.ToRawSpan(at.Value, original.Length);
        if (!text.IsPlaceableSpan(rawStart, rawEnd))
        {
            reason = "not-plain-body-text";
            return null;
        }

        reason = string.Empty;
        return new Edit(s, rawStart, rawEnd, original, suggested);
    }

    /// <summary>
    /// A pure insertion has no text to find, so it is anchored by its contexts: the concatenation
    /// <c>ContextBefore + ContextAfter</c> must occur exactly once, and the insertion goes between them.
    /// </summary>
    private static Edit? ResolveInsertion(AnalysisSuggestion s, DocumentText text, string suggested, out string reason)
    {
        var before = s.ContextBefore ?? string.Empty;
        var probe = before + (s.ContextAfter ?? string.Empty);
        if (suggested.Length == 0 || probe.Length == 0)
        {
            reason = "empty";
            return null;
        }

        var hits = FindAll(text.Map.Text, probe);
        if (hits.Count != 1)
        {
            reason = hits.Count == 0 ? "no-match" : "ambiguous";
            return null;
        }

        var point = text.Map.ToRawInsertionPoint(hits[0] + before.Length);
        if (!text.IsPlaceablePoint(point))
        {
            reason = "not-plain-body-text";
            return null;
        }

        reason = string.Empty;
        return new Edit(s, point, point, string.Empty, suggested);
    }

    private static List<int> FindAll(string haystack, string needle)
    {
        var positions = new List<int>();
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0)
        {
            positions.Add(idx);
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }
        return positions;
    }

    /// <summary>
    /// The client's <c>pickBestMatch</c>: +2 when <c>ContextBefore</c> occurs in the 60 characters before
    /// the hit, +2 when <c>ContextAfter</c> occurs in the 60 after; the FIRST highest-scoring hit wins, and
    /// no hit scoring above zero means ambiguous (null), never an arbitrary pick.
    ///
    /// <para>COST (plan c12). A one-character original (a no-break space, a comma) occurs thousands of times in a
    /// chapter: 10.4 million hits over the 1,476 suggestions of the 38,209-word chapter that made an annotated
    /// book export take minutes, where slicing both windows per hit was 1.7 s of that chapter's 2.0 s resolve.
    /// So the hits are not walked. Each context is located once in the whole text, and only a hit inside the
    /// reach of one of its occurrences can score at all: <c>ContextBefore</c> at <c>q</c> lies in the window
    /// before a hit at <c>pos</c> exactly when <c>q + |before| &lt;= pos &lt;= q + 60</c>, and
    /// <c>ContextAfter</c> at <c>q</c> lies in the window after it exactly when
    /// <c>q + |after| - |needle| - 60 &lt;= pos &lt;= q - |needle|</c> (an ordinal occurrence inside a slice is
    /// a whole-text occurrence lying entirely inside it). Those candidates are scored in ascending order with the
    /// same rule; every other hit scores 0, so the first highest-scoring candidate is the first highest-scoring
    /// hit, and none above 0 is still null.</para>
    /// </summary>
    internal static int? PickBestMatch(IReadOnlyList<int> positions, int needleLength, string text, AnalysisSuggestion s)
    {
        var before = string.IsNullOrEmpty(s.ContextBefore) ? null : FindAll(text, s.ContextBefore);
        var after = string.IsNullOrEmpty(s.ContextAfter) ? null : FindAll(text, s.ContextAfter);
        var maxScore = (before != null ? 2 : 0) + (after != null ? 2 : 0);

        var candidates = new SortedSet<int>();
        foreach (var q in before ?? Enumerable.Empty<int>())
            AddHitsBetween(positions, q + s.ContextBefore!.Length, q + ContextWindow, candidates);
        foreach (var q in after ?? Enumerable.Empty<int>())
            AddHitsBetween(positions, q + s.ContextAfter!.Length - needleLength - ContextWindow, q - needleLength, candidates);

        int? best = null;
        var bestScore = 0;
        foreach (var pos in candidates)
        {
            var score = 0;
            if (before != null && OccursWithin(before, s.ContextBefore!.Length, Math.Max(0, pos - ContextWindow), pos))
                score += 2;
            if (after != null)
            {
                var afterStart = pos + needleLength;
                var afterEnd = Math.Min(text.Length, afterStart + ContextWindow);
                if (OccursWithin(after, s.ContextAfter!.Length, afterStart, afterEnd)) score += 2;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = pos;
                if (bestScore == maxScore) break; // a later candidate can only tie, and the first highest wins
            }
        }
        return best;
    }

    /// <summary>Adds every hit in <paramref name="positions"/> (ascending) that lies in <c>[from, to]</c>.</summary>
    private static void AddHitsBetween(IReadOnlyList<int> positions, int from, int to, SortedSet<int> into)
    {
        for (var i = LowerBound(positions, from); i < positions.Count && positions[i] <= to; i++)
            into.Add(positions[i]);
    }

    /// <summary>
    /// True when some occurrence in <paramref name="starts"/> (ascending start positions of a string of
    /// <paramref name="length"/> characters) lies entirely inside <c>[from, to)</c>. Only the first occurrence
    /// starting at or after <paramref name="from"/> can: every later one ends later still.
    /// </summary>
    private static bool OccursWithin(IReadOnlyList<int> starts, int length, int from, int to)
    {
        var i = LowerBound(starts, from);
        return i < starts.Count && starts[i] + length <= to;
    }

    /// <summary>Index of the first element of the ascending <paramref name="sorted"/> that is at least <paramref name="value"/>.</summary>
    private static int LowerBound(IReadOnlyList<int> sorted, int value)
    {
        var lo = 0;
        var hi = sorted.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ---------------------------------------------------------------- writing

    private static void WriteEdits(
        MainDocumentPart main, DocumentText text, List<Edit> edits, string language, DateTime stamp)
    {
        var author = ProofreadAnnotationStrings.Author(language);
        foreach (var edit in edits)
            edit.CommentLines = ProofreadAnnotationStrings.CommentLines(
                language, edit.Suggestion.Category, edit.Suggestion.Explanation);

        // The comments part is created only when some suggestion needs a comment: a document whose
        // suggestions are all plain replacements carries tracked changes and no comments part at all.
        var commentsPart = main.WordprocessingCommentsPart;
        if (commentsPart == null && edits.Any(e => e.CommentLines.Count > 0))
            commentsPart = main.AddNewPart<WordprocessingCommentsPart>();
        if (commentsPart != null) commentsPart.Comments ??= new W.Comments();

        var existingCommentMax = commentsPart != null ? MaxWordId(commentsPart.Comments!) : -1;
        var nextCommentId = existingCommentMax + 1;
        var revisionIds = new WordIds(MaxWordId(main, commentsPart) + 1);

        // Ids are handed out in document order so comment 1 is the first one the author meets.
        foreach (var edit in edits.OrderBy(e => e.RawStart))
        {
            if (edit.Original.Length > 0) edit.DeletionId = revisionIds.Next();
            if (edit.Suggested.Length > 0) edit.InsertionId = revisionIds.Next();
            if (edit.CommentLines.Count == 0) continue;
            edit.CommentId = nextCommentId++;
            commentsPart!.Comments!.Append(BuildComment(edit, language, author, stamp));
        }

        // Placed right to left: splitting a w:t keeps the ORIGINAL node as the left fragment, so every
        // character reference held by an edit further left is still valid when its turn comes. The same
        // counter renumbers any w:id inside a cloned w:rPr (a w:rPrChange), see CloneRunProperties.
        foreach (var edit in edits.OrderByDescending(e => e.RawStart))
            Place(text, edit, author, stamp, revisionIds);

        main.Document!.Save();
        commentsPart?.Comments!.Save();
    }

    private static W.Comment BuildComment(Edit edit, string language, string author, DateTime stamp)
    {
        var lines = edit.CommentLines;
        var rtl = LanguageTags.IsHebrew(language);
        var comment = new W.Comment
        {
            Id = edit.CommentId!.Value.ToString(),
            Author = author,
            Initials = ProofreadAnnotationStrings.Initials,
            Date = stamp,
        };
        for (var i = 0; i < lines.Count; i++)
        {
            var paragraph = new W.Paragraph();
            if (rtl) paragraph.Append(new W.ParagraphProperties(new W.BiDi()));
            if (i == 0) paragraph.Append(new W.Run(new W.AnnotationReferenceMark()));
            var run = new W.Run();
            if (rtl) run.Append(new W.RunProperties(new W.RightToLeftText()));
            run.Append(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            paragraph.Append(run);
            comment.Append(paragraph);
        }
        return comment;
    }

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Highest numeric <c>w:id</c> in EVERY XML part of the package, or -1 (be-c06). A revision or annotation id
    /// must be unique across the document, and the parts that can hold one are not only the body and the
    /// comments: footnotes, endnotes, headers and footers carry their own <c>w:ins</c>/<c>w:del</c>, and styles or
    /// numbering a <c>w:rPrChange</c>. Scanning only document.xml and comments.xml handed a footnote's revision
    /// ids 3 and 4 to the body's new revisions (probe, 2026-10-13). The body and the comments are read from
    /// their loaded DOM; every other part is STREAMED (never loaded, so the SDK does not re-serialize a part
    /// this pass does not change).
    /// </summary>
    private static int MaxWordId(MainDocumentPart main, WordprocessingCommentsPart? commentsPart)
    {
        var max = MaxWordId(main.Document!);
        if (commentsPart?.Comments != null) max = Math.Max(max, MaxWordId(commentsPart.Comments));
        foreach (var part in main.OpenXmlPackage.GetAllParts())
        {
            if (ReferenceEquals(part, main) || ReferenceEquals(part, commentsPart)) continue;
            if (!IsXmlContentType(part.ContentType)) continue;
            try
            {
                max = Math.Max(max, MaxWordIdStreamed(part));
            }
            catch (XmlException)
            {
                // A part that does not parse cannot hold an id this pass could collide with: skip it, as the
                // pre-be-c06 scan (body and comments only) did, rather than lose the unit's annotation.
            }
        }
        return max;
    }

    /// <summary>
    /// An XML part by its content type: <c>+xml</c> (every WordprocessingML/DrawingML/package part) or plain
    /// <c>application/xml</c> / <c>text/xml</c> (custom XML). NOT a bare "contains xml": every OOXML type does
    /// ("openxmlformats"), binary ones included (an embedded workbook, an OLE object, an obfuscated font).
    /// </summary>
    private static bool IsXmlContentType(string contentType)
    {
        var type = contentType.Split(';')[0].Trim();
        return type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || type.Equals("text/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static int MaxWordIdStreamed(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        if (stream.Length == 0) return -1;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var max = -1;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            var value = reader.GetAttribute("id", WordNamespace);
            if (value != null && int.TryParse(value, out var id) && id > max) max = id;
        }
        return max;
    }

    /// <summary>Highest numeric <c>w:id</c> anywhere under <paramref name="root"/>, or -1.</summary>
    private static int MaxWordId(OpenXmlElement root)
    {
        var max = -1;
        foreach (var element in root.Descendants())
        {
            foreach (var attribute in element.GetAttributes())
            {
                if (attribute.LocalName == "id" && attribute.NamespaceUri == WordNamespace
                    && int.TryParse(attribute.Value, out var id) && id > max)
                    max = id;
            }
        }
        return max;
    }

    /// <summary>Hands out <c>w:id</c> values upward from the first one past every id the package held.</summary>
    private sealed class WordIds
    {
        private int _next;

        public WordIds(int first) => _next = first;

        public int Next() => _next++;
    }

    /// <summary>One suggestion resolved to a raw span (or, for a pure insertion, a raw point).</summary>
    private sealed class Edit
    {
        public Edit(AnalysisSuggestion suggestion, int rawStart, int rawEnd, string original, string suggested)
        {
            Suggestion = suggestion;
            RawStart = rawStart;
            RawEnd = rawEnd;
            Original = original;
            Suggested = suggested;
        }

        public AnalysisSuggestion Suggestion { get; }
        public int RawStart { get; }
        public int RawEnd { get; }
        public string Original { get; }
        public string Suggested { get; }
        /// <summary>Null when the suggestion gets no comment (<see cref="CommentLines"/> is empty).</summary>
        public int? CommentId { get; set; }
        public IReadOnlyList<string> CommentLines { get; set; } = Array.Empty<string>();
        public int DeletionId { get; set; }
        public int InsertionId { get; set; }

        public bool IsInsertion => RawStart == RawEnd;

        /// <summary>
        /// Spans conflict when they share a character; a point conflicts with a span it touches at either
        /// edge or lies inside, and with another point at the same place. Touching spans do not conflict.
        /// </summary>
        public bool Conflicts(Edit other)
        {
            if (!IsInsertion && !other.IsInsertion)
                return RawStart < other.RawEnd && other.RawStart < RawEnd;
            if (IsInsertion && other.IsInsertion)
                return RawStart == other.RawStart;
            var (point, span) = IsInsertion ? (this, other) : (other, this);
            return span.RawStart <= point.RawStart && point.RawStart <= span.RawEnd;
        }
    }
}
