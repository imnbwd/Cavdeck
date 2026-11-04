using System.Text.Json;
using System.Text.Json.Serialization;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class CrossTransferModelTests
{
    [Fact]
    public void Cross_copy_payload_round_trips_without_changing_existing_job_shape()
    {
        var source = new StorageObjectRef(Guid.NewGuid(), "源 bucket", "folder/空 格.txt");
        var destination = new StorageObjectRef(Guid.NewGuid(), "target", "Prefix/空 格.txt");
        var job = TransferJob.Create(TransferKind.Copy, destination.ConnectionId,
            destination.Bucket, destination.Key, "", 12, TransferConflictPolicy.Skip) with
        {
            CrossTransfer = new CrossTransferSpec(source, destination,
                TransferConflictPolicy.Skip, new RemoteIdentity("\"etag\"", 12, DateTimeOffset.UtcNow),
                Stage: CrossTransferStage.Transferring),
            UploadId = "upload-123"
        };
        var json = JsonSerializer.Serialize(job, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        var restored = JsonSerializer.Deserialize<TransferJob>(json, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.Equal(job.CrossTransfer, restored?.CrossTransfer);
        Assert.Equal("upload-123", restored?.UploadId);
        Assert.DoesNotContain("SecretAccessKey", json, StringComparison.Ordinal);
    }
}
