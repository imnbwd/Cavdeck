namespace Cavdeck.Domain.Storage;

public enum StorageFailureKind
{
    InvalidCredentials,
    AccessDenied,
    Endpoint,
    BucketNotFound,
    Network,
    Cancelled,
    CredentialMissing,
    InvalidConfiguration,
    ReadOnly,
    Unknown,
    PreconditionFailed,
    Unsupported
}

// Detail is a redacted technical message. User-facing copy is chosen by the UI from Kind.
public sealed record StorageFailure(
    StorageFailureKind Kind,
    string? ErrorCode,
    int? StatusCode,
    string? RequestId,
    string? Detail);

public sealed class StorageException : Exception
{
    public StorageException(StorageFailure failure)
        : base(failure.Detail ?? failure.Kind.ToString())
    {
        Failure = failure;
    }

    public StorageFailure Failure { get; }
}
