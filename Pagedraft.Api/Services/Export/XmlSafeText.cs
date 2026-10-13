using System.Text;
using System.Xml;

namespace Pagedraft.Api.Services.Export;

/// <summary>
/// The ONE gate model text passes before the proofread annotator writes it into an OOXML part (be-c06). The
/// Open XML SDK's writer throws on a character XML 1.0 cannot carry, at <c>Save</c>, which turned one bad
/// character in one explanation into a 500 for the whole export (observed: <c>U+FFFF</c>, a lone
/// <c>U+D800</c> and <c>U+0001</c> each threw "hexadecimal value ..., is an invalid character").
///
/// <para>Removes every character <see cref="XmlConvert.IsXmlChar"/> rejects (C0 controls other than tab, line
/// feed and carriage return; <c>U+FFFE</c>/<c>U+FFFF</c>) and every UNPAIRED surrogate; a valid surrogate pair
/// (<see cref="XmlConvert.IsXmlSurrogatePair"/>) is kept whole. Everything else, bidi marks included, passes
/// unchanged. Apply it to what is WRITTEN only: an original is matched against the document's own (already
/// legal) text, so cleaning it would only move the anchor.</para>
/// </summary>
internal static class XmlSafeText
{
    /// <summary><paramref name="value"/> with every character an XML part cannot carry removed; null stays null.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsClean(value)) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
                {
                    builder.Append(c).Append(value[i + 1]);
                    i++;
                }
                continue;
            }
            if (XmlConvert.IsXmlChar(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>True when <paramref name="value"/> needs no cleaning (the common case, so no copy is made).</summary>
    private static bool IsClean(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !XmlConvert.IsXmlSurrogatePair(value[i + 1], c)) return false;
                i++;
                continue;
            }
            if (!XmlConvert.IsXmlChar(c)) return false;
        }
        return true;
    }
}
