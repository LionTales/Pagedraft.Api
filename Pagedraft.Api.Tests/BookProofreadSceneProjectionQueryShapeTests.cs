using System;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Pins the SQL of the whole-book proofread's chapter-state scene load (fixes plan be-f04). It runs on every
/// status and start call, and used to select whole Scene rows, bodies included, for the entire book. Compiled
/// against the SqlServer provider with <see cref="EntityFrameworkQueryableExtensions.ToQueryString"/> (InMemory
/// moves no bytes and cannot see it). Same technique as <see cref="BookExportProofreadQueryShapeTests"/>.
/// </summary>
public class BookProofreadSceneProjectionQueryShapeTests
{
    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=(local);Database=BookProofreadSceneProjectionQueryShapeTests;Trusted_Connection=True;TrustServerCertificate=True;")
        .Options);

    [Fact]
    public void The_scene_layer_load_selects_no_body_column()
    {
        using var db = NewDb();
        var sql = BookProofreadService.SceneStampsQuery(db, new[] { Guid.NewGuid() }).ToQueryString();

        Assert.False(string.IsNullOrWhiteSpace(sql));
        Assert.Contains("FROM [Scenes]", sql);
        Assert.Contains("[UpdatedAt]", sql);
        Assert.Contains("[CreatedAt]", sql);

        Assert.DoesNotContain("[ContentSfdt]", sql);
        Assert.DoesNotContain("[ContentText]", sql);
    }

    [Fact]
    public void The_renderable_check_load_reads_the_sfdt_only()
    {
        using var db = NewDb();
        var sql = BookProofreadService.SceneBodiesQuery(db, new[] { Guid.NewGuid() }).ToQueryString();

        Assert.Contains("[ContentSfdt]", sql);
        Assert.DoesNotContain("[ContentText]", sql);
    }
}
