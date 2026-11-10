using Cavdeck.Application.Connections;
using Cavdeck.Application.Explorer;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.Configuration;
using Cavdeck.Infrastructure.Updates;

namespace Cavdeck.Cli;

internal sealed record CliError(int ExitCode, string Code, string Message, bool Retryable = false, object? Details = null)
{
    public static CliError Arguments { get; } = new(2, "INVALID_ARGUMENTS", CliText.Get("InvalidArguments"));
    public static CliError Cancelled { get; } = new(130, "CANCELLED", CliText.Get("Cancelled"));

    public static CliError From(Exception exception, CancellationToken token) => exception switch
    {
        OperationCanceledException when token.IsCancellationRequested => Cancelled,
        OperationCanceledException => new(6, "NETWORK_FAILURE", CliText.Get("Timeout"), true),
        CliException error => error.Error,
        ConnectionCatalogBusyException => new(5, "CONNECTION_CATALOG_BUSY", CliText.Get("CatalogBusy"), true),
        ApplicationUpdateInProgressException => new(5, "APPLICATION_UPDATING", CliText.Get("ApplicationUpdating"), true),
        ConnectionValidationException validation when validation.Issues.Any(issue => issue.Code == ConnectionIssueCode.Duplicate) =>
            new(5, "CONNECTION_NAME_EXISTS", CliText.Get("ConnectionNameExists")),
        ConnectionValidationException validation => new(2, "INVALID_CONFIGURATION", CliText.Get("InvalidConnection"),
            Details: validation.Issues.Select(issue => new { field = issue.Field.ToString(), issue.Code }).ToArray()),
        ObjectManifestLimitException => new(2, "TOO_MANY_OBJECTS", CliText.Get("CopyLimit")),
        StorageException storage => FromStorage(storage.Failure),
        UnsupportedConfigurationVersionException => new(9, "UNSUPPORTED_CONFIGURATION", CliText.Get("NewerCatalog")),
        InvalidDataException or System.Text.Json.JsonException => new(8, "INVALID_CONFIGURATION", CliText.Get("InvalidCatalog")),
        IOException or UnauthorizedAccessException or ArgumentException => new(8, "LOCAL_FILE_ERROR", CliText.Get("LocalAccess")),
        NotSupportedException => new(9, "UNSUPPORTED", CliText.Get("Unsupported")),
        _ => new(1, "UNKNOWN_ERROR", CliText.Get("UnknownError"))
    };

    public static CliError FromTransfer(TransferFailure? failure, bool cross = false) => failure?.Kind switch
    {
        _ when cross && failure?.Code == "ConditionalDeleteUnsupported" =>
            new(9, "UNSUPPORTED_CONDITIONAL_DELETE", CliText.Get("ConditionalDeleteUnsupported")),
        _ when cross && failure?.Code is "SourceChanged" or "SourceChangedBeforeDelete" => new(5, "SOURCE_CHANGED", CliText.Get("SourceChanged")),
        _ when cross && failure?.Code is "DestinationChanged" or "DestinationAppearedDuringResume" => new(5, "DESTINATION_CHANGED", CliText.Get("DestinationChanged")),
        TransferFailureKind.ConnectionMissing => new(3, "CONNECTION_NOT_FOUND", CliText.Get("ConnectionRemoved")),
        TransferFailureKind.NotFound => new(3, "OBJECT_NOT_FOUND", CliText.Get("ObjectMissing")),
        TransferFailureKind.AccessDenied or TransferFailureKind.ReadOnly => new(4, "ACCESS_DENIED", CliText.Get("TransferAccessDenied")),
        TransferFailureKind.Conflict => new(5, "TARGET_CONFLICT", CliText.Get("TargetConflict")),
        TransferFailureKind.Network or TransferFailureKind.Throttled =>
            new(6, "NETWORK_FAILURE", CliText.Get("TransferNetwork"), true),
        TransferFailureKind.LocalMissing or TransferFailureKind.InvalidPath or TransferFailureKind.Busy or TransferFailureKind.Disk =>
            new(8, "LOCAL_FILE_ERROR", CliText.Get("InvalidLocalPath")),
        TransferFailureKind.Unsupported => new(9, "UNSUPPORTED_CONDITIONAL_WRITE", CliText.Get("ConditionalUnsupported")),
        TransferFailureKind.Integrity => new(1, "INTEGRITY_FAILURE", CliText.Get("IntegrityFailure")),
        _ => new(1, "TRANSFER_FAILED", CliText.Get("TransferFailed"))
    };

    public static CliError FromStorage(StorageFailure failure) => failure.Kind switch
    {
        _ when failure.ErrorCode == "ConditionalDeleteUnsupported" =>
            new(9, "UNSUPPORTED_CONDITIONAL_DELETE", CliText.Get("ConditionalDeleteUnsupported")),
        _ when failure.ErrorCode == "ConnectionMissing" => new(3, "CONNECTION_NOT_FOUND", CliText.Get("ConnectionMissing")),
        StorageFailureKind.InvalidCredentials or StorageFailureKind.CredentialMissing =>
            new(4, "AUTH_FAILED", CliText.Get("AuthFailed")),
        StorageFailureKind.ReadOnly or StorageFailureKind.AccessDenied => new(4, "ACCESS_DENIED", CliText.Get("StorageAccessDenied")),
        StorageFailureKind.BucketNotFound => new(3, "OBJECT_NOT_FOUND", CliText.Get("StorageMissing")),
        StorageFailureKind.PreconditionFailed => new(5, "TARGET_CONFLICT", CliText.Get("TargetConflict")),
        StorageFailureKind.Network or StorageFailureKind.Endpoint =>
            new(6, "NETWORK_FAILURE", CliText.Get("StorageNetwork"), true),
        StorageFailureKind.Unsupported => new(9, "UNSUPPORTED", CliText.Get("StorageUnsupported")),
        StorageFailureKind.InvalidConfiguration => new(2, "INVALID_CONFIGURATION", CliText.Get("InvalidConnection")),
        StorageFailureKind.Cancelled => Cancelled,
        _ when failure.StatusCode == 400 && failure.ErrorCode is ("InvalidArgument" or "InvalidToken") => Arguments,
        _ when failure.StatusCode == 409 => new(5, "TARGET_CONFLICT", CliText.Get("DestinationChanged")),
        _ when failure.StatusCode is 429 or >= 500 => new(6, "NETWORK_FAILURE", CliText.Get("StorageNetwork"), true),
        _ => new(1, "STORAGE_ERROR", CliText.Get("StorageError"))
    };
}

internal sealed class CliException(CliError error) : Exception(error.Code)
{
    public CliError Error { get; } = error;
}
