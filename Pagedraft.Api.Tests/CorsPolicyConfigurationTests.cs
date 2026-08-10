using Microsoft.AspNetCore.Cors.Infrastructure;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Program.cs's CORS policy is the ONLY thing between the export screen and a browser that silently
/// treats every response header as absent. `AllowAnyHeader()` governs REQUEST headers only;
/// `Content-Disposition` and the two skipped-chapter headers are not CORS-safelisted, so a client
/// reading them cross-origin gets null unless they are named in `WithExposedHeaders`. Dev runs through
/// proxy.conf.json (same-origin), so driving the app locally cannot catch a regression here - this
/// test builds the REAL policy Program.cs registers (via CorsPolicyConfiguration.ConfigureDefault, not
/// a hand-copied duplicate) and asserts on it directly, with no running host and no database.
/// </summary>
public class CorsPolicyConfigurationTests
{
    [Fact]
    public void ConfigureDefault_ExposesTheThreeExportHeaders()
    {
        var builder = new CorsPolicyBuilder();

        CorsPolicyConfiguration.ConfigureDefault(builder);
        var policy = builder.Build();

        Assert.Contains("Content-Disposition", policy.ExposedHeaders);
        Assert.Contains(BookExportService.SkippedCountHeader, policy.ExposedHeaders);
        Assert.Contains(BookExportService.SkippedChaptersHeader, policy.ExposedHeaders);
    }
}
