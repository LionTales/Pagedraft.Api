using System;
using System.Linq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Pagedraft.Api.Tests;

public class AnalysisRunLogDeleteBehaviorTests
{
    [Fact]
    public void Model_ForeignKeyDeleteBehavior_AnalysisRunLogToAnalysisResult_IsSetNull()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new AppDbContext(options);

        var analysisRunLogEntity = db.Model.FindEntityType(typeof(AnalysisRunLog));
        Assert.NotNull(analysisRunLogEntity);

        var fkToAnalysisResult = analysisRunLogEntity!
            .GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(AnalysisResult));

        Assert.Equal(DeleteBehavior.SetNull, fkToAnalysisResult.DeleteBehavior);
        Assert.False(fkToAnalysisResult.IsRequired);

        // This relationship should remain Cascade so deleting a Chapter removes its AnalysisResults.
        var analysisResultEntity = db.Model.FindEntityType(typeof(AnalysisResult));
        Assert.NotNull(analysisResultEntity);

        var fkToChapter = analysisResultEntity!
            .GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(Chapter));

        Assert.Equal(DeleteBehavior.Cascade, fkToChapter.DeleteBehavior);
    }

    [Fact]
    public void Model_ForeignKeyDeleteBehavior_AnalysisRunLogToPromptTemplate_IsRestrict()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new AppDbContext(options);

        var analysisRunLogEntity = db.Model.FindEntityType(typeof(AnalysisRunLog));
        Assert.NotNull(analysisRunLogEntity);

        var fkToPromptTemplate = analysisRunLogEntity!
            .GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(PromptTemplate));

        Assert.Equal(DeleteBehavior.Restrict, fkToPromptTemplate.DeleteBehavior);
    }
}
