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
    public DbSet<BookBible> BookBibles => Set<BookBible>();
    public DbSet<SceneEmbedding> SceneEmbeddings => Set<SceneEmbedding>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<AnalysisSuggestion> AnalysisSuggestions => Set<AnalysisSuggestion>();
    public DbSet<SuggestionOutcomeRecord> SuggestionOutcomeRecords => Set<SuggestionOutcomeRecord>();
    public DbSet<AnalysisRunLog> AnalysisRunLogs => Set<AnalysisRunLog>();
    public DbSet<ChapterStyleProfile> ChapterStyleProfiles => Set<ChapterStyleProfile>();
    public DbSet<BookStyleBaseline> BookStyleBaselines => Set<BookStyleBaseline>();
    public DbSet<BookSummaryBaseline> BookSummaryBaselines => Set<BookSummaryBaseline>();
    public DbSet<BookFinding> BookFindings => Set<BookFinding>();
    public DbSet<BookReviewCoverage> BookReviewCoverages => Set<BookReviewCoverage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Author).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(10);
            // Model tier (p3-2). Nullable = fast; a stored value is parsed defensively by AiTierPolicy.Parse,
            // so widening the enum later cannot break an existing row.
            e.Property(x => x.AiTier).HasMaxLength(20);
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
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Language).HasMaxLength(10).HasDefaultValue("he");
            e.HasIndex(x => new { x.BookId, x.Scope, x.AnalysisType });
        });

        modelBuilder.Entity<ChunkSummary>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.StructuredJson).IsRequired(false);
            // wb1-r02: structured-brief build timestamp, separate from the shared CreatedAt the flat
            // re-summary path also bumps. Nullable so legacy rows self-heal (null = stale = rebuild).
            e.Property(x => x.StructuredBuiltAt).IsRequired(false);
            // wb3-c04: user-edit clobber guard for the flat SummaryText surface + its own freshness stamp,
            // independent of CreatedAt/StructuredBuiltAt so neither surface masks the other (dual-surface).
            e.Property(x => x.SummaryUserEdited).HasDefaultValue(false);
            e.Property(x => x.SummaryUserEditedAt).IsRequired(false);
            e.Property(x => x.BuiltWithModel).HasMaxLength(200);
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

        modelBuilder.Entity<BookBible>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StyleProfileJson).IsRequired(false);
            e.Property(x => x.CharacterRegisterJson).IsRequired(false);
            e.Property(x => x.ThemesJson).IsRequired(false);
            e.Property(x => x.TimelineJson).IsRequired(false);
            e.Property(x => x.WorldBuildingJson).IsRequired(false);

            e.HasOne(x => x.Book)
                .WithOne()
                .HasForeignKey<BookBible>(x => x.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.BookId).IsUnique();
        });

        modelBuilder.Entity<SceneEmbedding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EmbeddingVector)
                .IsRequired()
                .HasColumnType("varbinary(max)");
            e.Property(x => x.ModelName).HasMaxLength(200);

            e.HasOne(x => x.Scene)
                .WithMany()
                .HasForeignKey(x => x.SceneId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Chapter)
                .WithMany()
                .HasForeignKey(x => x.ChapterId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Book)
                .WithMany()
                .HasForeignKey(x => x.BookId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.BookId);
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

        modelBuilder.Entity<AnalysisSuggestion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OriginalText).IsRequired();
            e.Property(x => x.SuggestedText).IsRequired();
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.Category).HasMaxLength(100);
            e.HasOne(x => x.AnalysisResult)
                .WithMany(r => r.Suggestions)
                .HasForeignKey(x => x.AnalysisResultId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AnalysisResultId);
        });

        modelBuilder.Entity<ChapterStyleProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.MetricsJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.BuiltWithModel).HasMaxLength(200);
            // Chapter FK = Cascade so deleting a chapter drops its cached style profile automatically.
            // Book FK = Restrict (deliberately, to avoid SQL Server's "multiple cascade paths" error);
            // BooksController.Delete removes ChapterStyleProfiles for the book explicitly before delete.
            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Chapter).WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ChapterId, x.Language }).IsUnique();
        });

        modelBuilder.Entity<BookStyleBaseline>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.MetricsJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.BuiltWithModel).HasMaxLength(200);
            // Book FK = Restrict (deliberately, to avoid SQL Server's "multiple cascade paths" error,
            // mirroring ChapterStyleProfile); BooksController.Delete removes baselines for the book
            // explicitly before deleting the book.
            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);
            // One cached average per (BookId, Language) - the cache key.
            e.HasIndex(x => new { x.BookId, x.Language }).IsUnique();
        });

        modelBuilder.Entity<BookSummaryBaseline>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.BookBriefJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.BuiltWithModel).HasMaxLength(200);
            // Book FK = Restrict (deliberately, to avoid SQL Server's "multiple cascade paths" error,
            // mirroring BookStyleBaseline); BooksController.Delete removes summaries for the book
            // explicitly before deleting the book.
            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);
            // One cached rollup per (BookId, Language) - the cache key.
            e.HasIndex(x => new { x.BookId, x.Language }).IsUnique();
        });

        modelBuilder.Entity<BookFinding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.Dimension).HasMaxLength(50);
            e.Property(x => x.Verdict).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("open");
            e.Property(x => x.DedupKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.BuiltWithModel).HasMaxLength(200);
            e.Property(x => x.EvidenceJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.ChapterAnchorsJson).HasColumnType("nvarchar(max)");
            // Book FK = Restrict (same multiple-cascade-paths guard as BookStyleBaseline and
            // BookSummaryBaseline); BooksController.Delete removes BookFindings for the book
            // explicitly before deleting the book.
            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);
            // One finding per (BookId, Language, DedupKey) -- used for rebuild status-preservation.
            // Language is part of the key because ComputeDedupKey does NOT hash Language and every
            // query scopes BookFinding by (BookId, Language): a he/en pair whose (dimension, order,
            // rationale) collide produce the SAME DedupKey, and omitting Language here would throw a
            // unique-constraint violation on the cross-language collision instead of letting both coexist.
            e.HasIndex(x => new { x.BookId, x.Language, x.DedupKey }).IsUnique();
        });

        modelBuilder.Entity<BookReviewCoverage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Language).HasMaxLength(10);
            // Book FK = Restrict (same multiple-cascade-paths guard as BookStyleBaseline / BookSummaryBaseline /
            // BookFinding); BooksController.Delete removes BookReviewCoverages for the book explicitly before
            // deleting the book.
            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Restrict);
            // One persisted coverage row per (BookId, Language) - the cache/upsert key.
            e.HasIndex(x => new { x.BookId, x.Language }).IsUnique();
        });

        modelBuilder.Entity<AnalysisRunLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("AnalysisRunLogs");
            e.Property(x => x.Scope).HasMaxLength(20);
            e.Property(x => x.AnalysisType).HasMaxLength(30);
            e.Property(x => x.ModelName).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.ChunkDetailsJson).HasColumnType("nvarchar(max)");

            e.HasOne(x => x.AnalysisResult)
                .WithMany()
                .HasForeignKey(x => x.AnalysisResultId)
                .IsRequired(false)
                // When a chapter is deleted, AnalysisResults are cascaded (Cascade from Chapter->AnalysisResult).
                // The run-log FK must therefore be SET NULL so deletion of analyzed rows doesn't get blocked.
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.PromptTemplate)
                .WithMany()
                .HasForeignKey(x => x.PromptTemplateId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.AnalysisResultId);
            e.HasIndex(x => x.PromptTemplateId);
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
                // be-c01: stamp on Add ONLY when the writer did not supply one. CreatedAt is the FLAT
                // surface's freshness stamp, and the batched re-summary path anchors it to the chapter's own
                // summarize time (BookIntelligenceService phase 1), which is minutes before this save on a
                // real book. Overwriting it here with the persist time is what would let a chapter edited
                // mid-pass be classified fresh forever. Every other writer (ChapterBriefService,
                // BooksController's user-edit path) leaves it unset and still gets the persist-time stamp.
                if (entry.State == EntityState.Added && cs.CreatedAt == default)
                    cs.CreatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is BookBible bb)
            {
                if (entry.State == EntityState.Added) bb.CreatedAt = bb.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) bb.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is SceneEmbedding se)
            {
                if (entry.State == EntityState.Added) se.CreatedAt = DateTimeOffset.UtcNow;
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
            else if (entry.Entity is AnalysisRunLog rl)
            {
                if (entry.State == EntityState.Added) rl.CreatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is ChapterStyleProfile csp)
            {
                if (entry.State == EntityState.Added) csp.CreatedAt = csp.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) csp.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is BookStyleBaseline bsb)
            {
                if (entry.State == EntityState.Added) bsb.CreatedAt = bsb.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) bsb.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is BookSummaryBaseline bsum)
            {
                if (entry.State == EntityState.Added) bsum.CreatedAt = bsum.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) bsum.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is BookFinding bf)
            {
                if (entry.State == EntityState.Added) bf.CreatedAt = bf.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) bf.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.Entity is BookReviewCoverage brc)
            {
                if (entry.State == EntityState.Added) brc.CreatedAt = brc.UpdatedAt = DateTimeOffset.UtcNow;
                else if (entry.State == EntityState.Modified) brc.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
