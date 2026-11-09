namespace Cavdeck.Domain.Transfers;

public enum TransferKind { Upload, Download, Copy, Move }

public enum TransferState
{
    Queued,
    Running,
    Paused,
    WaitingRetry,
    Completed,
    Failed,
    Cancelled
}

public enum TransferConflictPolicy { Skip = 0, Overwrite = 1, Rename = 2, Fail = 3 }

public enum TransferFailureKind
{
    None,
    Network,
    Throttled,
    AccessDenied,
    NotFound,
    Conflict,
    Integrity,
    LocalMissing,
    Busy,
    Disk,
    ReadOnly,
    ConnectionMissing,
    InvalidPath,
    Unsupported,
    Unknown
}

public enum ResumeMode { None, Multipart, Range, Reconcile, ParallelRange, SequentialOnly }

public enum ResumeAction { Restart, ContinueRange, CommitPartial, Missing }

public enum IntegrityCheck { Match, Mismatch, SizeOnly }

public sealed record TransferFailure(
    TransferFailureKind Kind,
    bool Retryable,
    string? Code,
    string? Detail)
{
    public bool CleanupPending { get; init; }
}

public sealed record RemoteIdentity(string? ETag, long Size, DateTimeOffset? LastModified, bool ETagIsMd5 = false)
{
    public string? VersionId { get; init; }
    public string? ContentType { get; init; }
    public bool SameContent(RemoteIdentity? other)
    {
        if (other is null || other.Size != Size)
            return false;
        if (!string.IsNullOrEmpty(ETag) && !string.IsNullOrEmpty(other.ETag))
            return string.Equals(Normalize(ETag), Normalize(other.ETag), StringComparison.OrdinalIgnoreCase);
        return LastModified is not null && other.LastModified is not null
            && LastModified == other.LastModified;
    }

    public static string Normalize(string etag) => etag.Trim().Trim('"');
}

public sealed record TransferPart(int Number, long Offset, long Length, string? ETag)
{
    public string? Sha256 { get; init; }
}

public readonly record struct LocalFileStamp(long Length, DateTime LastWriteUtc, DateTime CreationUtc);

public sealed record StorageObjectRef(Guid ConnectionId, string Bucket, string Key, string? VersionId = null);

public enum CrossTransferStage { Preparing, Transferring, Verifying, Removing }

public sealed record CrossTransferSpec(
    StorageObjectRef Source,
    StorageObjectRef Destination,
    TransferConflictPolicy Conflict,
    RemoteIdentity? SourceIdentity = null,
    RemoteIdentity? DestinationBaseline = null,
    string? SourceConfiguration = null,
    string? DestinationConfiguration = null,
    CrossTransferStage Stage = CrossTransferStage.Preparing,
    string? Verification = null,
    bool CleanupPending = false,
    bool Move = false)
{
    public string? SourceETag { get; init; }
    public bool SinglePut { get; init; }
}

public sealed record TransferJob(
    Guid Id,
    Guid BatchId,
    Guid ConnectionId,
    TransferKind Kind,
    TransferState State,
    string Bucket,
    string Key,
    string LocalPath,
    string DisplayName,
    long BytesTransferred,
    long? TotalBytes,
    int Attempt,
    bool Skipped,
    TransferFailure? Failure,
    string? UploadId,
    RemoteIdentity? Remote,
    RemoteIdentity? Baseline,
    ResumeMode Resume,
    TransferConflictPolicy Conflict,
    string? ContentType,
    bool FolderMarker,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt)
{
    public DateTimeOffset? SourceModifiedAt { get; init; }
    // Keep first-observed destination identity even after a transient failure.
    public bool BaselineCaptured { get; init; }
    public string? ConnectionConfiguration { get; init; }
    public LocalFileStamp? LocalDestinationBaseline { get; init; }
    public bool LocalDestinationBaselineCaptured { get; init; }
    public CrossTransferSpec? CrossTransfer { get; init; }

    public bool IsOpen => State is TransferState.Queued or TransferState.Running
        or TransferState.Paused or TransferState.WaitingRetry;

    public bool BlocksShutdown => IsOpen;

    public bool BlocksUpdates => State is TransferState.Queued or TransferState.Running
        or TransferState.WaitingRetry;

    public static TransferJob Create(
        TransferKind kind,
        Guid connectionId,
        string bucket,
        string key,
        string localPath,
        long? totalBytes,
        TransferConflictPolicy conflict,
        Guid? batchId = null,
        string? contentType = null,
        bool folderMarker = false,
        DateTimeOffset? createdAt = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var name = DisplayNameOf(localPath, key, folderMarker);
        return new TransferJob(
            Guid.NewGuid(),
            batchId ?? Guid.NewGuid(),
            connectionId,
            kind,
            TransferState.Queued,
            bucket,
            key,
            localPath,
            name,
            0,
            totalBytes,
            0,
            false,
            null,
            null,
            null,
            null,
            kind == TransferKind.Download ? ResumeMode.Range : ResumeMode.Reconcile,
            conflict,
            contentType,
            folderMarker,
            now,
            now,
            null);
    }

    public static string DisplayNameOf(string localPath, string key, bool folderMarker)
    {
        if (folderMarker)
            return key.TrimEnd('/');
        var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
            name = key.TrimEnd('/').Split('/').LastOrDefault() ?? key;
        return name;
    }
}
