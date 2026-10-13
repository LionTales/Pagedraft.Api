using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Pagedraft.Api.Controllers;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Shared fixtures for the clipboard paste tests (<see cref="DocumentEditorClipboardTests"/>,
/// <see cref="RtfCodepagePrecedenceTests"/>): RTF builders, the real Word header fixture, and the read-back
/// of the SFDT the <c>SystemClipboard</c> endpoint returns, the way the editor receives it.
/// </summary>
internal static class RtfClipboardTestKit
{
    internal static readonly Encoding Cp1255 = CodePagesEncodingProvider.Instance.GetEncoding(1255)!;
    internal static readonly Encoding Cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
    internal static readonly Encoding Cp1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;

    /// <summary>The exact paragraph + run control words Word wrote for a Hebrew run in the captured clipboard.</summary>
    internal static string LangDrivenHebrewRun(string sentence) =>
        @"\pard\plain \rtlpar\qj \li0\ri0\sl276\slmult1\widctlpar\wrapdefault\aspalpha\aspnum\faauto\adjustright\rin0\lin0\itap0 "
        + @"\ltrch\fcs1 \af37\afs22\alang4096 \rtlch\fcs0 \f37\fs22\lang1037\cgrid\langnp4096\langfenp4096 "
        + @"{\ltrch\fcs1 \afs24\alang1033 \rtlch\fcs0 \f45\fs24\lang1037\langnp1033\langfenp1033 "
        + Escapes(Cp1255, sentence)
        + @"}\par";

    /// <summary>The REAL header and font table Word wrote for the gibberish paste (no prose in it) + <paramref name="body"/>.</summary>
    internal static string RealHeaderDocument(string body)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "word-clipboard-header.rtf");
        var header = File.ReadAllText(path, Encoding.ASCII);
        // Non-vacuity: a missing or swapped fixture must fail here, not read as a pass.
        Assert.StartsWith(@"{\rtf1\adeflang1037\ansi\ansicpg0", header);
        Assert.Contains(@"{\f45\fbidi \froman\fcharset0", header);
        Assert.Contains("Calibri (Hebrew)", header);
        Assert.EndsWith("}}", header);
        return header + body + "}";
    }

    internal static string SmallDocument(string header, string fonts, string body) =>
        @"{\rtf1" + header + @"{\fonttbl" + fonts + @"}\pard\plain " + body + "}";

    /// <summary><paramref name="text"/> in <paramref name="encoding"/>, every byte &gt;= 0x80 written as <c>\'xx</c>.</summary>
    internal static string Escapes(Encoding encoding, string text)
    {
        var sb = new StringBuilder();
        foreach (var b in encoding.GetBytes(text))
        {
            if (b < 0x80) sb.Append((char)b);
            else sb.Append(@"\'").Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    internal static string PasteThroughEndpoint(string rtf) =>
        PasteThroughEndpoint(new DocumentEditorController(NullLogger<DocumentEditorController>.Instance), rtf);

    internal static string PasteThroughEndpoint(DocumentEditorController controller, string rtf)
    {
        var result = controller.SystemClipboard(new SystemClipboardRequest { Content = rtf, Type = ".rtf" });
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        return TextOf(content.Content!);
    }

    internal static string TextOf(Syncfusion.EJ2.DocumentEditor.WordDocument document)
    {
        try
        {
            return TextOf(Newtonsoft.Json.JsonConvert.SerializeObject(document));
        }
        finally
        {
            document.Dispose();
        }
    }

    /// <summary>
    /// Inflates the base64 zip the endpoint returns and joins every text run of the SFDT (body, text boxes and
    /// comment bodies alike: all of them carry their text in <c>tlp</c>).
    /// </summary>
    internal static string TextOf(string endpointJson)
    {
        var runs = new List<string>();
        void Walk(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if ((property.Name == "text" || property.Name == "tlp") && property.Value.Type == JTokenType.String)
                    {
                        var value = (string)property.Value!;
                        if (!value.Contains("Syncfusion", StringComparison.Ordinal) && !value.Contains("valid key", StringComparison.Ordinal))
                            runs.Add(value);
                    }

                    Walk(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array) Walk(item);
            }
        }

        Walk(SfdtOf(endpointJson));
        return string.Concat(runs);
    }

    internal static JToken SfdtOf(string endpointJson)
    {
        var wrapper = JObject.Parse(endpointJson);
        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String((string)wrapper["sfdt"]!)));
        using var reader = new StreamReader(zip.Entries[0].Open());
        return JToken.Parse(reader.ReadToEnd());
    }

    /// <summary>"U+05D0 U+05D1": code points of the text without the SFDT's control characters (footnote marks).</summary>
    internal static string CodePoints(string text) =>
        string.Join(" ", text.Where(c => !char.IsControl(c)).Select(c => $"U+{(int)c:X4}"));

    internal static bool HasHebrew(string text) => text.Any(c => c is >= 'א' and <= 'ת');

    internal static bool HasLatin1Accented(string text) => text.Any(c => c is >= 'à' and <= 'ú');

    internal static void AssertCleanHebrew(string text)
    {
        Assert.True(HasHebrew(text), $"expected Hebrew letters, got [{text}]");
        Assert.False(HasLatin1Accented(text), $"expected no Latin-1 mojibake, got [{text}]");
    }
}
