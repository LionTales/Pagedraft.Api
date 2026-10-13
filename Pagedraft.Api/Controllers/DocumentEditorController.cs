using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Pagedraft.Api.Services;
using Syncfusion.EJ2.DocumentEditor;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// Serves Syncfusion Document Editor web API (e.g. SystemClipboard for paste from Word/external sources).
/// The Angular client sends paste content here to convert HTML/RTF to SFDT instead of the default Syncfusion demo URL.
/// </summary>
[ApiController]
[Route("api/documenteditor")]
public class DocumentEditorController : ControllerBase
{
    private readonly ILogger<DocumentEditorController> _logger;
    private readonly Func<string, string>? _rtfPrepassOverride;

    public DocumentEditorController(ILogger<DocumentEditorController> logger)
    {
        _logger = logger;
    }

    /// <summary>Test seam: replaces <see cref="PrepareRtf"/> so a throwing pre-pass can be exercised.</summary>
    internal DocumentEditorController(ILogger<DocumentEditorController> logger, Func<string, string> rtfPrepass)
        : this(logger)
    {
        _rtfPrepassOverride = rtfPrepass;
    }

    /// <summary>
    /// Converts clipboard content (HTML, RTF, or plain text) to SFDT JSON for the Document Editor paste.
    /// Called when the user pastes from Word or other external sources with formatting.
    /// RTF goes through <see cref="RtfCodepageNormalizer"/> first: Word writes Hebrew as code-page byte escapes
    /// that Syncfusion's RTF reader decodes by the run font's charset alone, which turned pasted Hebrew into
    /// Latin-1 gibberish whenever the font was one Word could not resolve. Then
    /// <see cref="PseudoBidiFontNames.StripFromRtfFontTable"/> renames Word's charset-variant font entries
    /// ("Calibri (Hebrew)") to the real face so the pasted runs never store a font no machine has.
    /// If either pre-pass step throws, the ORIGINAL content is converted (a warning logs the exception type and
    /// the content length, never the text): the pre-pass improves a paste, it must never cost one.
    /// </summary>
    [HttpPost("SystemClipboard")]
    public IActionResult SystemClipboard([FromBody] SystemClipboardRequest request)
    {
        if (request == null || string.IsNullOrEmpty(request.Content))
        {
            return BadRequest("Missing 'content' in request body.");
        }

        var type = (request.Type ?? "html").Trim().TrimStart('.');
        // "sfdt" is already JSON — return as-is; no FormatType.Sfdt in Syncfusion.EJ2.DocumentEditor.
        if (type.Equals("sfdt", StringComparison.OrdinalIgnoreCase))
        {
            return Content(request.Content, "application/json");
        }

        if (!TryGetFormatType(type, out var formatType))
        {
            return BadRequest($"Unsupported clipboard type: '{type}'. Use 'html', 'rtf', 'sfdt', or 'text'.");
        }

        try
        {
            var content = request.Content;
            if (formatType == FormatType.Rtf)
            {
                try
                {
                    content = (_rtfPrepassOverride ?? PrepareRtf)(content);
                }
                catch (Exception ex)
                {
                    // The pre-pass only improves how DocIO decodes content it parses without it, so a fault in it
                    // must never cost the author the paste: convert the ORIGINAL content. The exception object is not logged
                    // (its message could quote the author's text); its type and the content size are enough to
                    // find the shape.
                    _logger.LogWarning(
                        "SystemClipboard RTF pre-pass threw {ExceptionType} on {ContentLength} chars; converting the original content",
                        ex.GetType().FullName,
                        request.Content.Length);
                    content = request.Content;
                }
            }

            var sfdtDocument = WordDocument.LoadString(content, formatType);
            try
            {
                var json = JsonConvert.SerializeObject(sfdtDocument);
                return Content(json, "application/json");
            }
            finally
            {
                sfdtDocument?.Dispose();
            }
        }
        catch (Exception ex)
        {
            // The content is the author's own text: log its type and size, never the content itself.
            _logger.LogWarning(
                ex,
                "SystemClipboard failed to convert clipboard content of type {ClipboardType} ({ContentLength} chars)",
                type,
                request.Content.Length);
            // Return a generic error to avoid leaking internal details (paths, stack traces) to clients.
            return StatusCode(500, new { error = "Failed to convert clipboard content to document format." });
        }
    }

    /// <summary>The RTF pre-pass: code-page re-encoding, then the charset-variant font-name strip.</summary>
    private string PrepareRtf(string content)
    {
        content = RtfCodepageNormalizer.Normalize(content, out var rewrittenEscapes);
        if (rewrittenEscapes > 0)
        {
            _logger.LogDebug(
                "SystemClipboard re-encoded {RewrittenEscapes} RTF byte escapes as Unicode before conversion",
                rewrittenEscapes);
        }

        // Word's font table names each charset variant "Calibri (Hebrew)" and the reader stores that
        // name verbatim, which is how the pseudo font reached saved chapters and then every export.
        content = PseudoBidiFontNames.StripFromRtfFontTable(content, out var renamedFonts);
        if (renamedFonts > 0)
        {
            _logger.LogDebug(
                "SystemClipboard renamed {RenamedFonts} charset-variant RTF font entries to their face name",
                renamedFonts);
        }

        return content;
    }

    private static bool TryGetFormatType(string type, out FormatType formatType)
    {
        switch (type.ToLowerInvariant())
        {
            case "html":
                formatType = FormatType.Html;
                return true;
            case "rtf":
                formatType = FormatType.Rtf;
                return true;
            case "text":
            case "txt":
                formatType = FormatType.Txt;
                return true;
            default:
                formatType = FormatType.Html;
                return false;
        }
    }
}

/// <summary>Request body for SystemClipboard: clipboard content and format type (html, rtf, text, sfdt).</summary>
public class SystemClipboardRequest
{
    /// <summary>Raw clipboard content (HTML, RTF, or plain text).</summary>
    public string? Content { get; set; }

    /// <summary>Format: "html", "rtf", "text", or "sfdt". Defaults to "html".</summary>
    public string? Type { get; set; }
}
