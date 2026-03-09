using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<AnalysisResult> AnalysisResults => Set<AnalysisResult>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<ChunkSummary> ChunkSummaries => Set<ChunkSummary>();
    public DbSet<BookProfile> BookProfiles => Set<BookProfile>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<SuggestionOutcomeRecord> SuggestionOutcomeRecords => Set<SuggestionOutcomeRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Author).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(10);
            e.HasMany(x => x.Chapters).WithOne(x => x.Book).HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chapter>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PartName).HasMaxLength(500);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.BookId, x.Order }).IsUnique();
            e.HasMany(x => x.Scenes).WithOne(x => x.Chapter).HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.AnalysisResults).WithOne(x => x.Chapter).HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Scene>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.HasIndex(x => new { x.ChapterId, x.Order }).IsUnique();
        });

        modelBuilder.Entity<AnalysisResult>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Template).WithMany(x => x.Results).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AnalysisType).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Language).HasMaxLength(10).HasDefaultValue("he");
            e.HasIndex(x => new { x.BookId, x.Scope, x.AnalysisType });
        });

        modelBuilder.Entity<ChunkSummary>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Chapter).WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BookId, x.ChapterId }).IsUnique();
        });

        modelBuilder.Entity<BookProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.BookId).IsUnique();
        });

        modelBuilder.Entity<PromptTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasMaxLength(50);
            e.Property(x => x.Language).HasMaxLength(10);
            e.HasData(
                new PromptTemplate
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111101"),
                    Name = "הגהה",
                    Type = "Proofreading",
                    TemplateText = "בדוק את הטקסט הבא ומצא שגיאות כתיב, דקדוק, ופיסוק:\n\n{chapter_text}",
                    IsBuiltIn = true,
                    Language = "he"
                },
                new PromptTemplate
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111102"),
                    Name = "ניתוח ספרותי",
                    Type = "Literary",
                    TemplateText = "נתח את הפרק הבא מבחינה ספרותית (דמויות, עלילה, מוטיבים, שפה):\n\n{chapter_text}",
                    IsBuiltIn = true,
                    Language = "he"
                },
                new PromptTemplate
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111103"),
                    Name = "ניתוח לשוני",
                    Type = "Linguistic",
                    TemplateText = "נתח את הפרק הבא מבחינה לשונית (דקדוק, סגנון, אוצר מילים):\n\n{chapter_text}",
                    IsBuiltIn = true,
                    Language = "he"
                },
                new PromptTemplate
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111104"),
                    Name = "מותאם אישית",
                    Type = "Custom",
                    TemplateText = "{chapter_text}",
                    IsBuiltIn = true,
                    Language = "he"
                }
            );
        });

        modelBuilder.Entity<DocumentVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).HasMaxLength(200);
            e.HasIndex(x => new { x.BookId, x.ChapterId, x.SceneId });
        });

        modelBuilder.Entity<SuggestionOutcomeRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OriginalText).HasMaxLength(400).IsRequired();
            e.Property(x => x.SuggestedText).HasMaxLength(400).IsRequired();
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.AnalysisResult).WithMany().HasForeignKey(x => x.AnalysisResultId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AnalysisResultId, x.OriginalText, x.SuggestedText }).IsUnique();
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is Book b)
            {
                if (entry.State == EntityState.Added) b.CreatedAt = b.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) b.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is Chapter c)
            {
                if (entry.State == EntityState.Added) c.CreatedAt = c.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) c.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is Scene s)
            {
                if (entry.State == EntityState.Added) s.CreatedAt = s.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) s.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is BookProfile bp)
            {
                if (entry.State == EntityState.Added) bp.CreatedAt = bp.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) bp.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is ChunkSummary cs)
            {
                if (entry.State == EntityState.Added) cs.CreatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is AnalysisResult ar)
            {
                if (entry.State == EntityState.Added) ar.CreatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is DocumentVersion dv)
            {
                if (entry.State == EntityState.Added) dv.CreatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is SuggestionOutcomeRecord so)
            {
                if (entry.State == EntityState.Added) so.CreatedAt = DateTimeOffset.UtcNow;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
