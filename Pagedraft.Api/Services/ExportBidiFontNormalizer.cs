using Syncfusion.DocIO.DLS;

namespace Pagedraft.Api.Services;

/// <summary>
/// Cleans the complex-script font data of a DocIO document on its way out as an export DOCX: every
/// <see cref="PseudoBidiFontNames">charset-variant font name</see> (<c>"Calibri (Hebrew)"</c>) becomes the real
/// face (<c>"Calibri"</c>), and on a Hebrew book every run that has no complex-script language of its own is
/// stamped <c>he-IL</c> (<see cref="HebrewLocaleId"/>).
/// <para>
/// <b>Why.</b> An exported chapter carrying <c>w:cs="Calibri (Hebrew)"</c> names a font no machine has, so when
/// the author copies it out of desktop Word, Word declares that font <c>\fcharset0</c> in the clipboard RTF and
/// the paste back into the editor decoded the Hebrew as Latin-1 (plan
/// whole-book-proofread-and-annotated-docx-2026-10-11, todos c1 and c2). The paste side is fixed by
/// <see cref="RtfCodepageNormalizer"/>; this is the export side, so the file itself is correct in Word and in
/// anything else that reads it.
/// </para>
/// <para>
/// <b>Walked:</b> every style's character format (Normal included), list-level formats, and in every section the
/// body and all six headers/footers, recursively through paragraphs (their paragraph-mark format too), tables,
/// content controls, text boxes, shapes, comments and footnotes. Each text range, field, symbol and field mark
/// is cleaned. DocIO's public surface exposes <c>FontName</c> and <c>FontNameBidi</c>; setting
/// <c>FontName</c> rewrites the ASCII, high-ANSI and East Asian slots together.
/// </para>
/// </summary>
internal static class ExportBidiFontNormalizer
{
    /// <summary>he-IL.</summary>
    internal const short HebrewLocaleId = 1037;

    /// <summary>
    /// What DocIO 32.1.24 reports for a complex-script language nothing set (ar-SA): a fresh run and the
    /// built-in Normal style both read 1025. It is indistinguishable from an explicit ar-SA through the public
    /// API, which on a Hebrew book is the safe reading.
    /// </summary>
    private const short DocIoUnsetBidiLocaleId = 1025;

    /// <summary>
    /// Cleans <paramref name="document"/> in place and returns how many character formats changed.
    /// <paramref name="stampHebrewLocale"/> is true for a Hebrew book; the font-name cleanup runs either way.
    /// </summary>
    internal static int Apply(WordDocument document, bool stampHebrewLocale)
    {
        ArgumentNullException.ThrowIfNull(document);
        var walker = new Walker(stampHebrewLocale);

        foreach (var style in document.Styles)
        {
            if (style is Style s) walker.Format(s.CharacterFormat);
        }

        foreach (ListStyle listStyle in document.ListStyles)
        {
            foreach (WListLevel level in listStyle.Levels) walker.Format(level.CharacterFormat);
        }

        foreach (WSection section in document.Sections)
        {
            walker.Entity(section.Body);
            var hf = section.HeadersFooters;
            foreach (var part in new[] { hf.OddHeader, hf.OddFooter, hf.EvenHeader, hf.EvenFooter, hf.FirstPageHeader, hf.FirstPageFooter })
            {
                if (part != null) walker.Entity(part);
            }
        }

        return walker.Changed;
    }

    private sealed class Walker
    {
        private readonly bool _stampHebrewLocale;

        public Walker(bool stampHebrewLocale) => _stampHebrewLocale = stampHebrewLocale;

        public int Changed { get; private set; }

        public void Entity(IEntity? entity)
        {
            switch (entity)
            {
                case null:
                    return;
                case WParagraph paragraph:
                    Format(paragraph.BreakCharacterFormat);
                    Children(paragraph.ChildEntities);
                    return;
                case WTextRange textRange: // WField too
                    Format(textRange.CharacterFormat);
                    return;
                case WSymbol symbol:
                    Format(symbol.CharacterFormat);
                    return;
                case WFieldMark fieldMark:
                    Format(fieldMark.CharacterFormat);
                    return;
                case WFootnote footnote:
                    Format(footnote.MarkerCharacterFormat);
                    Entity(footnote.TextBody);
                    return;
                case WTextBox textBox:
                    Entity(textBox.TextBoxBody);
                    return;
                case WComment comment:
                    Entity(comment.TextBody);
                    return;
                case Shape shape:
                    Entity(shape.TextBody);
                    return;
                case InlineContentControl inline:
                    Format(inline.BreakCharacterFormat);
                    foreach (var item in inline.ParagraphItems) Entity(item as IEntity);
                    return;
                case BlockContentControl block:
                    Format(block.BreakCharacterFormat);
                    Entity(block.TextBody);
                    return;
                case ICompositeEntity composite: // text bodies, tables, rows, cells, headers/footers
                    Children(composite.ChildEntities);
                    return;
            }
        }

        public void Format(WCharacterFormat? format)
        {
            if (format == null) return;
            var changed = false;

            var name = format.FontName;
            if (PseudoBidiFontNames.IsPseudo(name))
            {
                format.FontName = PseudoBidiFontNames.Strip(name)!;
                changed = true;
            }

            var bidiName = format.FontNameBidi;
            if (PseudoBidiFontNames.IsPseudo(bidiName))
            {
                format.FontNameBidi = PseudoBidiFontNames.Strip(bidiName)!;
                changed = true;
            }

            if (_stampHebrewLocale && format.LocaleIdBidi is 0 or DocIoUnsetBidiLocaleId)
            {
                format.LocaleIdBidi = HebrewLocaleId;
                changed = true;
            }

            if (changed) Changed++;
        }

        private void Children(EntityCollection? children)
        {
            if (children == null) return;
            foreach (var child in children) Entity(child as IEntity);
        }
    }
}
