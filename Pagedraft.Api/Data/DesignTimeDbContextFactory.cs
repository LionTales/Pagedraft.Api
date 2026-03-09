using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Pagedraft.Api.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Resolve project directory so we use the same fixed DB location as at runtime (project dir + pagedraft.db).
        var projectDir = ResolveProjectDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(projectDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();
        var connectionString = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(connectionString) || connectionString.TrimStart().StartsWith("Data Source=pagedraft.db", StringComparison.OrdinalIgnoreCase))
        {
            var dbPath = Path.Combine(projectDir, "pagedraft.db");
            connectionString = $"Data Source={dbPath};Cache=Shared";
        }
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new AppDbContext(options);
    }

    private static string ResolveProjectDirectory()
    {
        // When run from "dotnet ef", base directory is typically bin/Debug/net8.0; go up to project root.
        var baseDir = AppContext.BaseDirectory;
        for (var d = baseDir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            if (File.Exists(Path.Combine(d, "Pagedraft.Api.csproj")))
                return d;
        }
        return Directory.GetCurrentDirectory();
    }
}
