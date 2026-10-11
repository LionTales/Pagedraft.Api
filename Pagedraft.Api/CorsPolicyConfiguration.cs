using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Pagedraft.Api;

/// <summary>
/// The app's single CORS policy, extracted out of Program.cs's top-level statements so a test can
/// build the real policy (not a hand-copied duplicate) and assert on it directly, without needing a
/// running host or a database connection.
/// </summary>
internal static class CorsPolicyConfiguration
{
    /// <summary>
    /// Configures the default policy applied unconditionally by <c>app.UseCors()</c> - there is no
    /// per-environment branch, this is the one policy the app ever applies.
    /// </summary>
    public static void ConfigureDefault(CorsPolicyBuilder policy)
    {
        policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            // AllowAnyHeader() above governs REQUEST headers. A response header the browser will let script
            // read must be named HERE, and none of these is CORS-safelisted. Every one of them is read by the
            // client's export code (pagedraft-client src/app/core/services/export.service.ts and
            // core/models/export.ts), and the whole export screen is built on those reads: the filename the
            // author sees, whether the file they just downloaded is missing any chapters, and on the
            // proofread-annotated export how many suggestions it carries and how many could not be placed.
            // Dev runs through proxy.conf.json and is therefore same-origin, so a missing entry here CANNOT
            // be caught by driving the app locally.
            .WithExposedHeaders(
                "Content-Disposition",
                Pagedraft.Api.Services.BookExportService.SkippedCountHeader,
                Pagedraft.Api.Services.BookExportService.SkippedChaptersHeader,
                Pagedraft.Api.Services.BookExportService.AnnotationCountHeader,
                Pagedraft.Api.Services.BookExportService.UnanchoredCountHeader);
    }
}
