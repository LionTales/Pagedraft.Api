using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Pagedraft.Api.Services;

/// <summary>
/// Rewrites every RTF 8-bit hex escape (<c>\'xx</c>, xx &gt;= 0x80) in visible text as a Unicode escape
/// (<c>\uN?</c>) decoded with the code page the escape was actually written in, before the RTF reaches
/// Syncfusion's reader.
/// <para>
/// <b>Why.</b> DocIO's RTF reader (32.1.24) decodes <c>\'xx</c> by the run font's <c>\fcharset</c> ONLY: it
/// ignores the run's <c>\langN</c>, the header's <c>\adeflangN</c> and <c>\ansicpgN</c>. Desktop Word writes
/// Hebrew as cp1255 <c>\'xx</c> bytes, and when the run's font is one Word cannot resolve (the pseudo name
/// "Calibri (Hebrew)" our own DOCX export carries) it declares that font <c>\fcharset0</c>, so DocIO reads
/// the bytes as cp1252 and the author sees <c>äééúé</c> for <c>הייתי</c>. <c>\uN</c> escapes decode correctly
/// in every shape measured, so re-encoding the bytes here fixes the paste for any Word document, not only
/// our exports. Evidence: <c>src/docs/measurements/paste-gibberish-2026-10-11/</c> (outside git).
/// </para>
/// <para>
/// <b>Code page precedence</b>, first match wins, for an escape in visible text:
/// <list type="number">
/// <item>The current font declares <c>\fcharset2</c> (symbol): left untouched, the byte is a glyph index.</item>
/// <item>The current <c>\langN</c> names a non-Western script (1037 Hebrew -&gt; 1255, Arabic -&gt; 1256,
/// Cyrillic -&gt; 1251, 1032 Greek -&gt; 1253, Turkish -&gt; 1254, Central European -&gt; 1250, Baltic -&gt; 1257,
/// 1066 Vietnamese -&gt; 1258, 1054 Thai -&gt; 874, ...). This is the step that fixes the bug.</item>
/// <item>The current font's <c>\fcharsetN</c> when it names a code page (177 -&gt; 1255, 178 -&gt; 1256,
/// 204 -&gt; 1251, ...). The current font is the group's <c>\fN</c>, else the header's <c>\deffN</c>.</item>
/// <item>Any other current <c>\langN</c> (a Western language such as 1033 or 1036) -&gt; 1252. It ranks BELOW the
/// font charset on purpose: a Western language tag on a run whose font declares a Hebrew charset is a shape
/// DocIO already decodes correctly today, and the pre-pass must never turn a working paste into a broken one.</item>
/// <item>The header's <c>\ansicpgN</c> when N &gt; 0 (Word writes <c>\ansicpg0</c> when it has no ANSI page).</item>
/// <item>The header's <c>\adeflangN</c>, then <c>\deflangN</c>, mapped as in step 2.</item>
/// <item>1252.</item>
/// </list>
/// A double-byte code page (932, 936, 949, 950) or a byte the chosen code page leaves undefined keeps the
/// original escape, so DocIO's own behaviour is unchanged for those.
/// </para>
/// <para>
/// <b>Left byte-identical:</b> everything that is not an 8-bit escape in visible text. Escapes inside ignorable
/// <c>\*</c> destinations and inside the <see cref="SkippedDestinations"/> (font table names, pictures,
/// field instructions, ...) are copied as-is, <c>\binN</c> payloads are copied raw, and the fallback characters
/// that follow an existing <c>\uN</c> (the next <c>\ucN</c> characters) are copied as-is so the reader still
/// skips them. An RTF with no 8-bit escape at all is returned unchanged. Every emitted <c>\uN</c> is a signed
/// 16-bit value followed by as many <c>?</c> fallbacks as the current <c>\ucN</c> declares (a delimiting space
/// when <c>\uc0</c>).
/// </para>
/// </summary>
internal static class RtfCodepageNormalizer
{
    /// <summary>Destinations whose content is not run text; an escape inside one is never rewritten.</summary>
    internal static readonly IReadOnlySet<string> SkippedDestinations = new HashSet<string>(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "listtable", "listoverridetable", "rsidtbl",
        "generator", "themedata", "colorschememapping", "latentstyles", "datastore", "xmlnstbl", "objdata",
        "fldinst", "mmathPr", "wgrffmtfilter", "pgdsctbl", "revtbl", "protusertbl", "filetbl", "bkmkstart",
        "bkmkend",
    };

    private const int SymbolCharset = 2;
    private const int Western = 1252;

    private static readonly IReadOnlyDictionary<int, int> NonWesternCodePageByLang = BuildLangMap();

    private static readonly IReadOnlyDictionary<int, int> CodePageByCharset = new Dictionary<int, int>
    {
        [128] = 932, [129] = 949, [134] = 936, [136] = 950, [161] = 1253, [162] = 1254, [163] = 1258,
        [177] = 1255, [178] = 1256, [186] = 1257, [204] = 1251, [222] = 874, [238] = 1250,
    };

    private static readonly ConcurrentDictionary<int, Encoding?> EncodingCache = new();

    /// <summary>Returns <paramref name="rtf"/> with its 8-bit escapes re-encoded as Unicode escapes.</summary>
    internal static string Normalize(string rtf) => Normalize(rtf, out _);

    /// <summary>
    /// Returns <paramref name="rtf"/> with its 8-bit escapes re-encoded as Unicode escapes, and how many
    /// escapes were rewritten (0 means the returned string is the input).
    /// </summary>
    internal static string Normalize(string rtf, out int rewrittenEscapes)
    {
        rewrittenEscapes = 0;
        if (string.IsNullOrEmpty(rtf) || !rtf.Contains("\\'", StringComparison.Ordinal))
            return rtf;

        var output = new StringBuilder(rtf.Length + rtf.Length / 4);
        var header = new HeaderDefaults();
        var fontCharsets = new Dictionary<int, int>();
        var stack = new Stack<GroupState>();
        var state = GroupState.Initial;
        var groupJustOpened = false;
        var fallbackCharsToCopy = 0;
        var i = 0;

        while (i < rtf.Length)
        {
            var ch = rtf[i];

            if (ch == '{')
            {
                stack.Push(state);
                output.Append(ch);
                i++;
                groupJustOpened = true;
                fallbackCharsToCopy = 0;
                continue;
            }

            if (ch == '}')
            {
                if (stack.Count > 0) state = stack.Pop();
                output.Append(ch);
                i++;
                groupJustOpened = false;
                fallbackCharsToCopy = 0;
                continue;
            }

            if (ch != '\\' || i + 1 >= rtf.Length)
            {
                output.Append(ch);
                i++;
                if (ch is '\r' or '\n') continue;
                if (fallbackCharsToCopy > 0) fallbackCharsToCopy--;
                if (!char.IsWhiteSpace(ch)) groupJustOpened = false;
                continue;
            }

            var next = rtf[i + 1];

            if (next == '\'')
            {
                var escapeLength = TryReadHexByte(rtf, i + 2, out var value) ? 4 : 2;
                if (escapeLength == 4
                    && value >= 0x80
                    && fallbackCharsToCopy == 0
                    && !state.IsSkipped
                    && TryDecode(value, state, header, fontCharsets, out var decoded))
                {
                    AppendUnicodeEscape(output, decoded, state.UnicodeFallbackCount);
                    rewrittenEscapes++;
                }
                else
                {
                    output.Append(rtf, i, escapeLength);
                }

                if (fallbackCharsToCopy > 0) fallbackCharsToCopy--;
                i += escapeLength;
                groupJustOpened = false;
                continue;
            }

            if (next == '*')
            {
                if (groupJustOpened) state.IsSkipped = true;
                output.Append("\\*");
                i += 2;
                continue;
            }

            if (!IsAsciiLetter(next))
            {
                // Control symbol (\\, \{, \}, \~, \-, \_, \:, \|, \<newline>): two characters, copied through.
                output.Append(ch).Append(next);
                i += 2;
                if (fallbackCharsToCopy > 0) fallbackCharsToCopy--;
                groupJustOpened = false;
                continue;
            }

            var end = ReadControlWord(rtf, i, out var word, out var parameter);
            output.Append(rtf, i, end - i);
            i = end;

            if (word == "bin" && parameter is > 0)
            {
                var payload = (int)Math.Min(parameter.Value, rtf.Length - i);
                output.Append(rtf, i, payload);
                i += payload;
                groupJustOpened = false;
                continue;
            }

            if (groupJustOpened && SkippedDestinations.Contains(word)) state.IsSkipped = true;
            groupJustOpened = false;

            if (fallbackCharsToCopy > 0)
            {
                fallbackCharsToCopy--;
                continue;
            }

            ApplyControlWord(word, parameter, ref state, header, fontCharsets);
            if (word == "u" && parameter.HasValue) fallbackCharsToCopy = state.UnicodeFallbackCount;
        }

        return rewrittenEscapes == 0 ? rtf : output.ToString();
    }

    private static void ApplyControlWord(
        string word, int? parameter, ref GroupState state, HeaderDefaults header, Dictionary<int, int> fontCharsets)
    {
        switch (word)
        {
            case "f" when parameter.HasValue:
                state.FontId = parameter.Value;
                break;
            case "fcharset" when parameter.HasValue && state.InFontTable && state.FontId >= 0:
                fontCharsets[state.FontId] = parameter.Value;
                break;
            case "fonttbl":
                state.InFontTable = true;
                break;
            case "lang" when parameter.HasValue:
                state.Lang = parameter.Value;
                break;
            case "plain":
                state.FontId = -1;
                state.Lang = 0;
                break;
            case "uc" when parameter is >= 0:
                state.UnicodeFallbackCount = parameter.Value;
                break;
            case "deff" when parameter.HasValue:
                header.DefaultFontId = parameter.Value;
                break;
            case "ansicpg" when parameter.HasValue:
                header.AnsiCodePage = parameter.Value;
                break;
            case "adeflang" when parameter.HasValue:
                header.AssociatedDefaultLang = parameter.Value;
                break;
            case "deflang" when parameter.HasValue:
                header.DefaultLang = parameter.Value;
                break;
        }
    }

    private static bool TryDecode(
        int value, GroupState state, HeaderDefaults header, Dictionary<int, int> fontCharsets, out char decoded)
    {
        decoded = '\0';
        var fontId = state.FontId >= 0 ? state.FontId : header.DefaultFontId;
        var fontCharset = fontId >= 0 && fontCharsets.TryGetValue(fontId, out var cs) ? cs : -1;
        if (fontCharset == SymbolCharset) return false;

        var codePage = ResolveCodePage(state.Lang, fontCharset, header);
        if (codePage is 932 or 936 or 949 or 950) return false;

        var encoding = GetEncoding(codePage);
        if (encoding == null) return false;

        var text = encoding.GetString(new[] { (byte)value });
        if (text.Length != 1 || text[0] == '\uFFFD') return false;

        decoded = text[0];
        return true;
    }

    private static int ResolveCodePage(int lang, int fontCharset, HeaderDefaults header)
    {
        if (NonWesternCodePageByLang.TryGetValue(lang, out var byLang)) return byLang;
        if (CodePageByCharset.TryGetValue(fontCharset, out var byCharset)) return byCharset;
        if (IsRealLanguage(lang)) return Western;
        if (header.AnsiCodePage > 0) return header.AnsiCodePage;
        if (NonWesternCodePageByLang.TryGetValue(header.AssociatedDefaultLang, out var byAdefLang)) return byAdefLang;
        if (NonWesternCodePageByLang.TryGetValue(header.DefaultLang, out var byDefLang)) return byDefLang;
        return Western;
    }

    /// <summary>0, 1024 (LANG_NEUTRAL / "no proofing") and 4096 (LOCALE_CUSTOM_UNSPECIFIED) carry no script.</summary>
    private static bool IsRealLanguage(int lang) => lang > 0 && lang != 1024 && lang != 4096;

    private static Encoding? GetEncoding(int codePage) => EncodingCache.GetOrAdd(codePage, CreateEncoding);

    private static Encoding? CreateEncoding(int codePage)
    {
        // The provider instance is used directly so the normalizer never depends on startup having called
        // Encoding.RegisterProvider (unit tests construct no host); Program.cs registers it for everything else.
        // Undefined bytes decode to U+FFFD (not the default '?') so TryDecode can leave them untouched.
        var encoderFallback = EncoderFallback.ReplacementFallback;
        var decoderFallback = new DecoderReplacementFallback("\uFFFD");
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage, encoderFallback, decoderFallback)
                   ?? Encoding.GetEncoding(codePage, encoderFallback, decoderFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }


    private static void AppendUnicodeEscape(StringBuilder output, char value, int fallbackCount)
    {
        output.Append("\\u").Append(((short)value).ToString(CultureInfo.InvariantCulture));
        if (fallbackCount <= 0)
        {
            output.Append(' ');
            return;
        }

        output.Append('?', fallbackCount);
    }

    private static bool TryReadHexByte(string rtf, int start, out int value)
    {
        value = 0;
        if (start + 2 > rtf.Length) return false;
        return int.TryParse(rtf.AsSpan(start, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Reads <c>\word[-]digits[ ]</c> starting at the backslash; returns the index after the control word
    /// including its single delimiting space.
    /// </summary>
    private static int ReadControlWord(string rtf, int backslash, out string word, out int? parameter)
    {
        var j = backslash + 1;
        while (j < rtf.Length && IsAsciiLetter(rtf[j])) j++;
        word = rtf.Substring(backslash + 1, j - backslash - 1);

        var k = j;
        var negative = k < rtf.Length && rtf[k] == '-';
        if (negative) k++;
        var digitsStart = k;
        while (k < rtf.Length && char.IsAsciiDigit(rtf[k])) k++;

        parameter = null;
        if (k > digitsStart && long.TryParse(rtf.AsSpan(digitsStart, k - digitsStart), NumberStyles.None,
                CultureInfo.InvariantCulture, out var magnitude))
        {
            var signed = negative ? -magnitude : magnitude;
            if (signed is >= int.MinValue and <= int.MaxValue) parameter = (int)signed;
        }
        else if (negative)
        {
            k--; // a lone '-' is not part of the control word
        }

        if (k < rtf.Length && rtf[k] == ' ') k++;
        return k;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static IReadOnlyDictionary<int, int> BuildLangMap()
    {
        var map = new Dictionary<int, int> { [1037] = 1255, [1066] = 1258, [1054] = 874, [1032] = 1253 };
        void Add(int codePage, params int[] langs)
        {
            foreach (var lang in langs) map[lang] = codePage;
        }

        // Arabic (all regional variants), Urdu, Persian
        Add(1256, 1025, 2049, 3073, 4097, 5121, 6145, 7169, 8193, 9217, 10241, 11265, 12289, 13313, 14337,
            15361, 16385, 1056, 1065);
        // Russian, Ukrainian, Bulgarian, Belarusian, Serbian Cyrillic, Macedonian, Kazakh
        Add(1251, 1049, 1058, 1026, 1059, 3098, 1071, 1087);
        // Turkish, Azerbaijani Latin, Uzbek Latin
        Add(1254, 1055, 1068, 1091);
        // Polish, Czech, Hungarian, Croatian, Slovak, Slovenian, Romanian, Albanian, Serbian Latin
        Add(1250, 1045, 1029, 1038, 1050, 1051, 1060, 1048, 1052, 2074);
        // Estonian, Latvian, Lithuanian
        Add(1257, 1061, 1062, 1063);
        // Double-byte pages; resolved so the escape is deliberately left untouched (see TryDecode)
        Add(932, 1041);
        Add(936, 2052, 4100);
        Add(949, 1042);
        Add(950, 1028, 3076, 5124);
        return map;
    }

    private struct GroupState
    {
        public int FontId;
        public int Lang;
        public int UnicodeFallbackCount;
        public bool IsSkipped;
        public bool InFontTable;

        public static GroupState Initial => new() { FontId = -1, UnicodeFallbackCount = 1 };
    }

    private sealed class HeaderDefaults
    {
        public int DefaultFontId = -1;
        public int AnsiCodePage;
        public int AssociatedDefaultLang;
        public int DefaultLang;
    }
}
