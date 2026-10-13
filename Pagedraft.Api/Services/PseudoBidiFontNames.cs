using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Pagedraft.Api.Services;

/// <summary>
/// Word's charset-variant font names, such as <c>"Calibri (Hebrew)"</c> or <c>"Times New Roman (Arabic)"</c>, and
/// the three places PageDraft removes them: the RTF font table of a paste
/// (<see cref="StripFromRtfFontTable"/>), the body of an imported DOCX (<see cref="StripFromRunFonts"/>, called by
/// <see cref="SfdtConversionService.ConvertToSfdt"/>), and the DOCX it exports (<see cref="ExportBidiFontNormalizer"/>:
/// every character format and the document's font substitution table).
/// <para>
/// <b>What they are.</b> When desktop Word writes RTF (every copy to the clipboard) its font table lists each
/// font once per character set, for example <c>{\f1407\fbidi \fswiss\fcharset177\fprq2 Calibri (Hebrew);}</c>,
/// and the Hebrew runs reference that entry. The suffix is Word's label for "this face in charset 177"; no
/// installed font carries the name.
/// </para>
/// <para>
/// <b>How they reached our documents.</b> Syncfusion's RTF reader stores the entry's name verbatim, so a paste
/// from Word saved <c>"Calibri (Hebrew)"</c> into the chapter's SFDT (measured 2026-10-11: 19 of 300 dev-DB
/// chapters, every one of them client-saved; 0 in the bulk-imported books and 0 in the source manuscripts).
/// The export then wrote it as <c>w:cs="Calibri (Hebrew)"</c>; on the next copy Word could not resolve that
/// name, declared it <c>\fcharset0</c>, and the paste decoded the Hebrew as Latin-1. Stripping the suffix
/// returns the face the author actually used, which Word resolves and declares with the right charset.
/// </para>
/// <para>
/// Only the right-to-left parenthesised suffixes (<c>" (Hebrew)"</c>, <c>" (Arabic)"</c>) are stripped. Word's
/// other variants are left alone: <c>" CE"</c>, <c>" Cyr"</c>, <c>" Greek"</c>, <c>" Tur"</c>, <c>" Baltic"</c>
/// are bare words that real font families also end with, the parenthesised <c>" (Vietnamese)"</c> is kept
/// deliberately (pinned by the tests), and none of them carries a right-to-left script.
/// </para>
/// </summary>
internal static class PseudoBidiFontNames
{
    private static readonly string[] Suffixes = { " (Hebrew)", " (Arabic)" };

    // A font-table entry's name ends at ';', optionally after Word's {\*\falt <name>} alternate-name group.
    private static readonly Regex RtfEntryNameSuffix = new(
        @" \((?:Hebrew|Arabic)\)(?=(?:\{\\\*\\falt [^{}]*\})?;)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The real face name behind a Word charset-variant name (<c>"Calibri (Hebrew)"</c> -&gt; <c>"Calibri"</c>);
    /// any other name, null and empty included, is returned unchanged.
    /// </summary>
    internal static string? Strip(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName)) return fontName;
        foreach (var suffix in Suffixes)
        {
            if (fontName.Length > suffix.Length && fontName.EndsWith(suffix, StringComparison.Ordinal))
                return fontName[..^suffix.Length];
        }

        return fontName;
    }

    /// <summary>True when <paramref name="fontName"/> carries a suffix <see cref="Strip"/> would remove.</summary>
    internal static bool IsPseudo(string? fontName) => !ReferenceEquals(Strip(fontName), fontName);

    /// <summary>
    /// Returns <paramref name="rtf"/> with the charset-variant suffix removed from every font-table entry name,
    /// and how many entries were renamed (0 means the returned string is the input). Only the <c>\fonttbl</c>
    /// group is touched; each entry keeps its <c>\fN</c> number and its <c>\fcharsetN</c>, so runs still reference
    /// the same entry and any byte escape still decodes with the charset Word declared.
    /// </summary>
    internal static string StripFromRtfFontTable(string rtf, out int renamedEntries)
    {
        renamedEntries = 0;
        if (string.IsNullOrEmpty(rtf)) return rtf;

        var start = rtf.IndexOf(@"{\fonttbl", StringComparison.Ordinal);
        if (start < 0) return rtf;

        var end = EndOfGroup(rtf, start);
        if (end < 0) return rtf;

        var table = rtf.Substring(start, end - start + 1);
        var count = 0;
        var renamed = RtfEntryNameSuffix.Replace(table, _ =>
        {
            count++;
            return string.Empty;
        });
        if (count == 0) return rtf;

        renamedEntries = count;
        return string.Concat(rtf.AsSpan(0, start), renamed, rtf.AsSpan(end + 1));
    }

    /// <summary>
    /// Renames, in place, every charset-variant name in the <c>w:rFonts</c> elements under (and including)
    /// <paramref name="root"/>: the ASCII, high-ANSI, East Asian and complex-script slots of run properties,
    /// paragraph-mark run properties and tracked formatting changes alike. Returns how many attributes changed
    /// (0 means nothing was touched). Theme-font attributes (<c>w:cstheme</c> and the like) name a theme slot,
    /// not a face, and are left alone.
    /// </summary>
    internal static int StripFromRunFonts(OpenXmlElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var renamed = 0;
        var fonts = root is RunFonts self ? new[] { self } : root.Descendants<RunFonts>();
        foreach (var runFonts in fonts)
        {
            renamed += Rename(runFonts.Ascii);
            renamed += Rename(runFonts.HighAnsi);
            renamed += Rename(runFonts.EastAsia);
            renamed += Rename(runFonts.ComplexScript);
        }

        return renamed;

        static int Rename(StringValue? slot)
        {
            if (slot?.Value is not { } name || !IsPseudo(name)) return 0;
            slot.Value = Strip(name);
            return 1;
        }
    }

    /// <summary>Index of the '}' closing the group that opens at <paramref name="openBrace"/>, or -1.</summary>
    private static int EndOfGroup(string rtf, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < rtf.Length; i++)
        {
            switch (rtf[i])
            {
                case '\\':
                    i++; // an escaped '{', '}' or '\' is text, not structure
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}
