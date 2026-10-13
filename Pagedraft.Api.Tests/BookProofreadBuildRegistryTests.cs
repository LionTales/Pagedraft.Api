using System;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

public class BookProofreadBuildRegistryTests
{
    [Fact]
    public void TryStart_Complete_TryCancel_roundtrip()
    {
        var reg = new BookProofreadBuildRegistry();
        var book = Guid.NewGuid();
        var job = Guid.NewGuid();

        Assert.True(reg.TryStart(book, "he", job, out var cts));
        Assert.NotNull(cts);
        Assert.Equal(job, reg.TryGetActive(book, "he"));

        // Second start for the same key loses and gets no token source.
        Assert.False(reg.TryStart(book, "he", Guid.NewGuid(), out var loser));
        Assert.Null(loser);
        Assert.Equal(job, reg.TryGetActive(book, "he"));

        // Another language is an independent key.
        Assert.True(reg.TryStart(book, "en", Guid.NewGuid(), out _));

        Assert.False(cts!.IsCancellationRequested);
        Assert.False(reg.TryCancel(book, Guid.NewGuid()));      // unknown job id
        Assert.True(reg.TryCancel(book, job));                   // by job id
        Assert.True(cts.IsCancellationRequested);
        Assert.True(reg.TryCancel(book, "he"));                  // by language, idempotent

        reg.Complete(book, "he");
        Assert.Null(reg.TryGetActive(book, "he"));
        Assert.False(reg.TryCancel(book, "he"));
        reg.Complete(book, "he");                                // idempotent
        Assert.True(reg.TryStart(book, "he", Guid.NewGuid(), out _)); // key reusable after Complete
    }

    [Fact]
    public void A_closed_cancel_intake_refuses_every_cancel_and_leaves_the_token_alone()
    {
        var reg = new BookProofreadBuildRegistry();
        var book = Guid.NewGuid();
        var job = Guid.NewGuid();
        Assert.True(reg.TryStart(book, "he", job, out var cts));

        var other = Guid.NewGuid();
        Assert.True(reg.TryStart(book, "en", other, out var otherCts));
        reg.CloseCancelIntake(book, "en", Guid.NewGuid()); // another job's id: no-op
        reg.CloseCancelIntake(book, "fr", other);          // another key: no-op
        Assert.True(reg.TryCancel(book, other));
        Assert.True(otherCts!.IsCancellationRequested);

        reg.CloseCancelIntake(book, "he", job);
        Assert.False(reg.TryCancel(book, job));
        Assert.False(reg.TryCancel(book, "he"));
        Assert.False(cts!.IsCancellationRequested);
        Assert.Equal(job, reg.TryGetActive(book, "he")); // still registered until Complete
    }
}
