using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
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
    /// <summary>
    /// Converts clipboard content (HTML, RTF, or plain text) to SFDT JSON for the Document Editor paste.
    /// Called when the user pastes from Word or other external sources with formatting.
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
            var sfdtDocument = WordDocument.LoadString(request.Content, formatType);
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
            // Return a generic error to avoid leaking internal details (paths, stack traces) to clients.
            return StatusCode(500, new { error = "Failed to convert clipboard content to document format." });
        }
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
