using Cavdeck.App.Features.Explorer;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerTransferRefreshRulesTests
{
    private static readonly Guid SourceId = Guid.NewGuid();
    private static readonly Guid DestinationId = Guid.NewGuid();

    private static TransferJob Move(TransferState state = TransferState.Completed, bool skipped = false) =>
        TransferJob.Create(TransferKind.Copy, DestinationId, "target", "dest/folder/file.txt",
            "", 7, TransferConflictPolicy.Skip) with
        {
            State = state,
            Skipped = skipped,
            CrossTransfer = new CrossTransferSpec(
                new StorageObjectRef(SourceId, "source", "home/folder/file.txt"),
                new StorageObjectRef(DestinationId, "target", "dest/folder/file.txt"),
                TransferConflictPolicy.Skip, Move: true)
        };

    [Fact]
    public void Completed_move_refreshes_both_source_and_destination_directories()
    {
        var job = Move();
        Assert.True(ExplorerTransferRefreshRules.SourceAffected(job, SourceId, "source", "home/"));
        Assert.True(ExplorerTransferRefreshRules.DestinationAffected(job, DestinationId, "target", "dest/"));
        Assert.True(ExplorerTransferRefreshRules.SourceAffected(job, SourceId, "source", "home/folder/"));
    }

    [Fact]
    public void Other_buckets_and_sibling_prefixes_do_not_refresh()
    {
        var job = Move();
        Assert.False(ExplorerTransferRefreshRules.SourceAffected(job, SourceId, "other", "home/"));
        Assert.False(ExplorerTransferRefreshRules.DestinationAffected(job, DestinationId, "target", "destination/"));
        Assert.False(ExplorerTransferRefreshRules.SourceAffected(job, SourceId, "source", "home/folder2/"));
    }

    [Theory]
    [InlineData(TransferState.Queued)]
    [InlineData(TransferState.Running)]
    [InlineData(TransferState.Failed)]
    [InlineData(TransferState.Cancelled)]
    public void Non_completed_move_does_not_trigger_refresh(TransferState state)
    {
        Assert.False(ExplorerTransferRefreshRules.SourceAffected(Move(state), SourceId, "source", "home/"));
        Assert.False(ExplorerTransferRefreshRules.DestinationAffected(Move(state), DestinationId, "target", "dest/"));
    }

    [Fact]
    public void Skipped_move_never_refreshes_or_selects_destination()
    {
        var job = Move(skipped: true);
        Assert.False(ExplorerTransferRefreshRules.SourceAffected(job, SourceId, "source", "home/"));
        Assert.False(ExplorerTransferRefreshRules.DestinationAffected(job, DestinationId, "target", "dest/"));
    }

    [Fact]
    public void Copy_only_refreshes_destination()
    {
        var job = Move() with { CrossTransfer = Move().CrossTransfer! with { Move = false } };
        Assert.False(ExplorerTransferRefreshRules.SourceAffected(job, SourceId, "source", "home/"));
        Assert.True(ExplorerTransferRefreshRules.DestinationAffected(job, DestinationId, "target", "dest/"));
    }
}
