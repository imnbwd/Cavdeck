namespace Cavdeck.Domain.Storage;

public static class StorageFailureClassifier
{
    private static readonly HashSet<string> CredentialCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "InvalidAccessKeyId",
        "SignatureDoesNotMatch",
        "InvalidClientTokenId",
        "InvalidSecurity",
        "ExpiredToken",
        "TokenRefreshRequired",
        "InvalidToken"
    };

    private static readonly HashSet<string> EndpointCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PermanentRedirect",
        "AuthorizationHeaderMalformed",
        "IllegalLocationConstraintException",
        "InvalidLocationConstraint",
        "InvalidEndpoint",
        "InvalidAddressingHeader"
    };

    private static readonly HashSet<string> DeniedCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AccessDenied",
        "AllAccessDisabled",
        "AccessDeniedException"
    };

    public static StorageFailure FromS3(
        string? errorCode,
        int? statusCode,
        string? requestId,
        string? message,
        IEnumerable<string?>? secrets = null)
    {
        var kind = KindFromS3(errorCode, statusCode, message);
        return new StorageFailure(
            kind,
            string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim(),
            statusCode,
            string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim(),
            SecretRedactor.Redact(message, secrets));
    }

    public static StorageFailure FromTransport(string? message, IEnumerable<string?>? secrets = null)
    {
        var kind = LooksLikeEndpoint(message)
            ? StorageFailureKind.Endpoint
            : StorageFailureKind.Network;
        return new StorageFailure(kind, null, null, null, SecretRedactor.Redact(message, secrets));
    }

    private static StorageFailureKind KindFromS3(string? errorCode, int? statusCode, string? message)
    {
        if (errorCode is not null && CredentialCodes.Contains(errorCode))
            return StorageFailureKind.InvalidCredentials;
        if (errorCode is not null && EndpointCodes.Contains(errorCode))
            return StorageFailureKind.Endpoint;
        if (statusCode == 301)
            return StorageFailureKind.Endpoint;
        if (string.Equals(errorCode, "NoSuchBucket", StringComparison.OrdinalIgnoreCase) || statusCode == 404)
            return StorageFailureKind.BucketNotFound;
        if (statusCode == 412
            || string.Equals(errorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase))
            return StorageFailureKind.PreconditionFailed;
        if (string.Equals(errorCode, "InvalidBucketName", StringComparison.OrdinalIgnoreCase))
            return StorageFailureKind.InvalidConfiguration;
        if ((errorCode is not null && DeniedCodes.Contains(errorCode)) || statusCode is 401 or 403)
            return statusCode == 401
                ? StorageFailureKind.InvalidCredentials
                : StorageFailureKind.AccessDenied;
        if (LooksLikeEndpoint(message))
            return StorageFailureKind.Endpoint;
        if (statusCode is null or 408 or 504)
            return StorageFailureKind.Network;
        return StorageFailureKind.Unknown;
    }

    private static bool LooksLikeEndpoint(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        return message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || message.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase)
            || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SSL", StringComparison.OrdinalIgnoreCase);
    }
}
