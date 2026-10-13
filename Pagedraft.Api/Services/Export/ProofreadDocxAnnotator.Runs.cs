using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Pagedraft.Api.Services.Export;

/// <summary>
/// The raw-text model of the body and the run surgery that turns a resolved span into <c>w:del</c> /
/// <c>w:ins</c>, wrapped in <c>w:commentRangeStart</c> / <c>w:commentRangeEnd</c> / comment reference only
/// when the suggestion gets a comment.
/// </summary>
public sealed partial class ProofreadDocxAnnotator
{
    /// <summary>
    /// One raw character of the body. <see cref="Node"/> is the <c>w:t</c> it lives in (null for a
    /// paragraph separator, a tab or a break). <see cref="Editable"/> is true only for a <c>w:t</c> in a plain
    /// run directly under the paragraph or under a hyperlink, outside any complex field: the only places a
    /// <c>w:del</c>/<c>w:ins</c> pair can be written without nesting it inside someone else's markup.
    /// </summary>
    private readonly record struct CharRef(int Paragraph, W.Text? Node, int Offset, bool Editable);

    /// <summary>
    /// The body's text as the reader sees it: every paragraph (tables included, nested text-box paragraphs as
    /// their own) joined by <c>\n</c>, which normalizes to the single space the snapshot has at a paragraph
    /// boundary. Deleted text (<c>w:delText</c>) and field instructions (<c>w:instrText</c>) are not text a
    /// reader sees and are skipped; a field's RESULT is visible, so it is kept but marked not editable.
    /// </summary>
    private sealed class DocumentText
    {
        private DocumentText(List<CharRef> chars, NormalizedTextMap map)
        {
            Chars = chars;
            Map = map;
        }

        public List<CharRef> Chars { get; }
        public NormalizedTextMap Map { get; }

        private readonly Dictionary<string, List<int>> _occurrences = new(StringComparer.Ordinal);

        /// <summary>
        /// Every start of <paramref name="needle"/> in <see cref="Map"/>'s text, ascending. Memoized for the document
        /// (plan c12): a chapter's suggestions repeat the same one-character originals hundreds of times, so each
        /// distinct original is searched once. Callers only read the list.
        /// </summary>
        public List<int> Occurrences(string needle)
        {
            if (!_occurrences.TryGetValue(needle, out var hits))
                _occurrences[needle] = hits = FindAll(Map.Text, needle);
            return hits;
        }

        public static DocumentText Build(W.Body body)
        {
            var raw = new StringBuilder();
            var chars = new List<CharRef>();
            var fieldDepth = 0;
            var paragraphIndex = 0;

            foreach (var paragraph in body.Descendants<W.Paragraph>())
            {
                if (paragraphIndex > 0)
                {
                    raw.Append('\n');
                    chars.Add(new CharRef(-1, null, 0, false));
                }

                foreach (var element in paragraph.Descendants())
                {
                    if (!ReferenceEquals(element.Ancestors<W.Paragraph>().FirstOrDefault(), paragraph)) continue;
                    switch (element)
                    {
                        case W.FieldChar fieldChar:
                            var type = fieldChar.FieldCharType?.Value;
                            if (type == W.FieldCharValues.Begin) fieldDepth++;
                            else if (type == W.FieldCharValues.End) fieldDepth = Math.Max(0, fieldDepth - 1);
                            break;
                        case W.Text t:
                            if (t.Ancestors<W.DeletedRun>().Any() || t.Ancestors<W.MoveFromRun>().Any()) break;
                            var editable = fieldDepth == 0 && IsPlainRun(t.Parent);
                            var value = t.Text ?? string.Empty;
                            for (var i = 0; i < value.Length; i++)
                            {
                                raw.Append(value[i]);
                                chars.Add(new CharRef(paragraphIndex, t, i, editable));
                            }
                            break;
                        case W.TabChar:
                            raw.Append('\t');
                            chars.Add(new CharRef(paragraphIndex, null, 0, false));
                            break;
                        case W.Break:
                        case W.CarriageReturn:
                            raw.Append('\n');
                            chars.Add(new CharRef(paragraphIndex, null, 0, false));
                            break;
                    }
                }
                paragraphIndex++;
            }

            return new DocumentText(chars, NormalizedTextMap.Build(raw.ToString()));
        }

        private static bool IsPlainRun(OpenXmlElement? parent) =>
            parent is W.Run run
            && (run.Parent is W.Paragraph || (run.Parent is W.Hyperlink link && link.Parent is W.Paragraph));

        /// <summary>
        /// True when <c>[rawStart, rawEnd)</c> is editable text inside one paragraph and one run container,
        /// and nothing BETWEEN its first and last character would be swallowed by the <c>w:del</c> except
        /// range markup (bookmarks, comment ranges, proofing marks, permissions) and rendered page breaks.
        /// A footnote reference, a drawing or a field char between the ends makes the span unplaceable.
        /// </summary>
        public bool IsPlaceableSpan(int rawStart, int rawEnd)
        {
            if (rawStart < 0 || rawEnd > Chars.Count || rawStart >= rawEnd) return false;
            if (SplitsSurrogatePair(rawStart) || SplitsSurrogatePair(rawEnd)) return false;
            var paragraph = Chars[rawStart].Paragraph;
            var spanNodes = new HashSet<W.Text>();
            for (var i = rawStart; i < rawEnd; i++)
            {
                var c = Chars[i];
                if (!c.Editable || c.Paragraph != paragraph || c.Node == null) return false;
                spanNodes.Add(c.Node);
            }

            var firstNode = Chars[rawStart].Node!;
            var lastNode = Chars[rawEnd - 1].Node!;
            var firstRun = (W.Run)firstNode.Parent!;
            var lastRun = (W.Run)lastNode.Parent!;
            if (!ReferenceEquals(firstRun.Parent, lastRun.Parent)) return false;

            var sequence = new List<OpenXmlElement>();
            OpenXmlElement? cursor = firstRun;
            while (cursor != null)
            {
                if (cursor is W.Run run) sequence.AddRange(run.ChildElements.Where(c => c is not W.RunProperties));
                else sequence.Add(cursor);
                if (ReferenceEquals(cursor, lastRun)) break;
                cursor = cursor.NextSibling();
            }
            if (cursor == null) return false;

            var from = sequence.IndexOf(firstNode);
            var to = sequence.IndexOf(lastNode);
            for (var k = from + 1; k < to; k++)
            {
                var element = sequence[k];
                if (element is W.Text t && spanNodes.Contains(t)) continue;
                if (!IsSwallowableMarkup(element)) return false;
            }
            return true;
        }

        /// <summary>A pure insertion needs an editable neighbour in a paragraph to attach to.</summary>
        public bool IsPlaceablePoint(int rawPoint) => !SplitsSurrogatePair(rawPoint) && InsertionAnchor(rawPoint) != null;

        /// <summary>
        /// True when <paramref name="rawPoint"/> falls between the two halves of one surrogate pair in one
        /// <c>w:t</c> (an original or a context that begins or ends with a lone half can match there). A cut
        /// there leaves an unpaired surrogate in each fragment, which the XML writer refuses at <c>Save</c>
        /// (be-c06), so such a span or point is not placeable: counted unanchored, the author's text untouched.
        /// </summary>
        private bool SplitsSurrogatePair(int rawPoint)
        {
            if (rawPoint <= 0 || rawPoint >= Chars.Count) return false;
            var (before, at) = (Chars[rawPoint - 1], Chars[rawPoint]);
            return at.Node != null && ReferenceEquals(before.Node, at.Node)
                && char.IsHighSurrogate(at.Node.Text[before.Offset]) && char.IsLowSurrogate(at.Node.Text[at.Offset]);
        }

        /// <summary>
        /// The character an insertion at <paramref name="rawPoint"/> attaches to: the one before it when that
        /// is editable (the insertion follows its context), else the one at it.
        /// </summary>
        public (CharRef Char, bool After)? InsertionAnchor(int rawPoint)
        {
            if (rawPoint > 0 && rawPoint - 1 < Chars.Count && Chars[rawPoint - 1].Editable)
                return (Chars[rawPoint - 1], true);
            if (rawPoint >= 0 && rawPoint < Chars.Count && Chars[rawPoint].Editable)
                return (Chars[rawPoint], false);
            return null;
        }

        private static bool IsSwallowableMarkup(OpenXmlElement element) => element is W.LastRenderedPageBreak
            or W.BookmarkStart or W.BookmarkEnd or W.ProofError
            or W.CommentRangeStart or W.CommentRangeEnd or W.PermStart or W.PermEnd;
    }

    // ---------------------------------------------------------------- placement

    private static void Place(DocumentText text, Edit edit, string author, DateTime stamp, WordIds ids)
    {
        OpenXmlElement first;
        OpenXmlElement last;

        if (edit.IsInsertion)
        {
            var (anchor, after) = text.InsertionAnchor(edit.RawStart)!.Value;
            var node = anchor.Node!;
            var run = Detach(node, ids);
            var properties = run.RunProperties;
            var insertion = BuildInsertion(properties, edit.Suggested, edit.InsertionId, author, stamp, ids);
            if (after)
            {
                if (anchor.Offset + 1 < node.Text.Length) SplitTextRun(node, anchor.Offset + 1, ids);
                run.InsertAfterSelf(insertion);
            }
            else
            {
                var target = anchor.Offset > 0 ? SplitTextRun(node, anchor.Offset, ids) : run;
                target.InsertBeforeSelf(insertion);
            }
            first = last = insertion;
        }
        else
        {
            var spanRuns = new List<W.Run>();
            foreach (var (node, from, to) in Segments(text, edit.RawStart, edit.RawEnd))
                spanRuns.Add(Isolate(node, from, to, ids));

            var firstRun = spanRuns[0];
            var lastRun = spanRuns[^1];
            // The insertion's formatting template, only read (BuildInsertion writes a renumbered clone of it).
            var properties = firstRun.RunProperties;

            var swallowed = new List<OpenXmlElement>();
            for (OpenXmlElement? cursor = firstRun; cursor != null; cursor = cursor.NextSibling())
            {
                swallowed.Add(cursor);
                if (ReferenceEquals(cursor, lastRun)) break;
            }

            var deletion = new W.DeletedRun
            {
                Author = author,
                Date = stamp,
                Id = edit.DeletionId.ToString(),
            };
            firstRun.InsertBeforeSelf(deletion);
            foreach (var element in swallowed)
            {
                element.Remove();
                deletion.Append(element);
            }
            foreach (var t in deletion.Descendants<W.Text>().ToList())
            {
                t.InsertBeforeSelf(new W.DeletedText(t.Text) { Space = SpaceProcessingModeValues.Preserve });
                t.Remove();
            }

            first = last = deletion;
            if (edit.Suggested.Length > 0)
            {
                var insertion = BuildInsertion(properties, edit.Suggested, edit.InsertionId, author, stamp, ids);
                deletion.InsertAfterSelf(insertion);
                last = insertion;
            }
        }

        // A plain suggestion is its tracked change alone; only one with something to add is ranged by a comment.
        if (edit.CommentId is not { } id) return;
        var commentId = id.ToString();
        first.InsertBeforeSelf(new W.CommentRangeStart { Id = commentId });
        var rangeEnd = last.InsertAfterSelf(new W.CommentRangeEnd { Id = commentId });
        rangeEnd.InsertAfterSelf(new W.Run(new W.CommentReference { Id = commentId }));
    }

    private static W.InsertedRun BuildInsertion(
        W.RunProperties? properties, string value, int id, string author, DateTime stamp, WordIds ids)
    {
        var run = new W.Run();
        if (properties != null) run.Append(CloneRunProperties(properties, ids));
        run.Append(new W.Text(value) { Space = SpaceProcessingModeValues.Preserve });
        return new W.InsertedRun(run)
        {
            Author = author,
            Date = stamp,
            Id = id.ToString(),
        };
    }

    /// <summary>The span's characters grouped by <c>w:t</c> node, as (node, from, to) offset ranges.</summary>
    private static List<(W.Text Node, int From, int To)> Segments(DocumentText text, int rawStart, int rawEnd)
    {
        var segments = new List<(W.Text Node, int From, int To)>();
        for (var i = rawStart; i < rawEnd; i++)
        {
            var c = text.Chars[i];
            if (segments.Count > 0 && ReferenceEquals(segments[^1].Node, c.Node))
                segments[^1] = (c.Node!, segments[^1].From, c.Offset + 1);
            else
                segments.Add((c.Node!, c.Offset, c.Offset + 1));
        }
        return segments;
    }

    /// <summary>
    /// Leaves <paramref name="node"/>'s characters <c>[from, to)</c> as the only content of a run and returns
    /// that run. The ORIGINAL node object always stays the leftmost fragment (it keeps <c>[0, from)</c> when
    /// <paramref name="from"/> is past 0), which is what makes right-to-left placement safe.
    /// </summary>
    private static W.Run Isolate(W.Text node, int from, int to, WordIds ids)
    {
        var run = Detach(node, ids);
        if (to < node.Text.Length) SplitTextRun(node, to, ids);
        return from > 0 ? SplitTextRun(node, from, ids) : run;
    }

    /// <summary>
    /// Moves every other content child of <paramref name="node"/>'s run into sibling runs (before and after,
    /// same <c>w:rPr</c>), so the run holds only this <c>w:t</c>. Children are MOVED, not cloned, so character
    /// references into them stay valid.
    /// </summary>
    private static W.Run Detach(W.Text node, WordIds ids)
    {
        var run = (W.Run)node.Parent!;
        var content = run.ChildElements.Where(c => c is not W.RunProperties).ToList();
        var index = content.IndexOf(node);
        if (index > 0)
        {
            var left = NewRunLike(run, ids);
            foreach (var child in content.Take(index))
            {
                child.Remove();
                left.Append(child);
            }
            run.InsertBeforeSelf(left);
        }
        if (index < content.Count - 1)
        {
            var right = NewRunLike(run, ids);
            foreach (var child in content.Skip(index + 1))
            {
                child.Remove();
                right.Append(child);
            }
            run.InsertAfterSelf(right);
        }
        return run;
    }

    /// <summary>
    /// Splits a run that holds only <paramref name="node"/> at <paramref name="offset"/>: the node keeps
    /// <c>[0, offset)</c>, a new run right after it holds the rest. Returns the new run.
    /// </summary>
    private static W.Run SplitTextRun(W.Text node, int offset, WordIds ids)
    {
        var run = (W.Run)node.Parent!;
        var value = node.Text;
        node.Text = value[..offset];
        node.Space = SpaceProcessingModeValues.Preserve;
        var right = NewRunLike(run, ids);
        right.Append(new W.Text(value[offset..]) { Space = SpaceProcessingModeValues.Preserve });
        run.InsertAfterSelf(right);
        return right;
    }

    private static W.Run NewRunLike(W.Run template, WordIds ids)
    {
        var run = new W.Run();
        if (template.RunProperties != null) run.Append(CloneRunProperties(template.RunProperties, ids));
        return run;
    }

    /// <summary>
    /// A deep copy of <paramref name="properties"/> with every <c>w:id</c> inside it RENUMBERED from
    /// <paramref name="ids"/> (be-c06). A run's <c>w:rPr</c> can hold a tracked formatting change
    /// (<c>w:rPrChange w:id</c>); a plain clone copied that id into every split fragment and into the insertion,
    /// and OpenXmlValidator reported "w:id should have unique value". Renumbered rather than stripped: each
    /// fragment is text the author's formatting change covered, so it keeps the change (Word does the same
    /// when it splits such a run), and the inserted replacement keeps it too, so rejecting the formatting
    /// change leaves the replacement formatted like its neighbours. Every <c>w:rPr</c> clone the annotator
    /// writes goes through here.
    /// </summary>
    private static W.RunProperties CloneRunProperties(W.RunProperties properties, WordIds ids)
    {
        var clone = (W.RunProperties)properties.CloneNode(true);
        foreach (var element in clone.Descendants().ToList())
        {
            if (element.GetAttributes().Any(a => a.LocalName == "id" && a.NamespaceUri == WordNamespace))
                element.SetAttribute(new OpenXmlAttribute("w", "id", WordNamespace, ids.Next().ToString()));
        }
        return clone;
    }
}
