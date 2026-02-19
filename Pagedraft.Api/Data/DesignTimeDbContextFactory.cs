using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Pagedraft.Api.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(config.GetConnectionString("DefaultConnection") ?? "Data Source=pagedraft.db;Cache=Shared")
            .Options;
        return new AppDbContext(options);
    }
}
