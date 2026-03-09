using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Pagedraft.Api.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var projectDir = ResolveProjectDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(projectDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var dbProvider = config.GetValue<string>("DatabaseProvider") ?? "SqlServer";
        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection for design-time context.");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        if (dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            optionsBuilder.UseSqlite(connectionString);
        }
        else
        {
            optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
                sqlOptions.CommandTimeout(60);
            });
        }

        return new AppDbContext(optionsBuilder.Options);
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
