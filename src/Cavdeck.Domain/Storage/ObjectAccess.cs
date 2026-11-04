namespace Cavdeck.Domain.Storage;

public static class ObjectAccess
{
    public static void Ensure(StorageConnection connection, string bucket, string? key, bool write)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(bucket))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.InvalidConfiguration, "Bucket", null, null, null));

        if (write && connection.IsReadOnly)
            throw new StorageException(new StorageFailure(
                StorageFailureKind.ReadOnly, null, null, null, null));

        if (!string.IsNullOrEmpty(connection.RestrictedBucket)
            && !string.Equals(connection.RestrictedBucket, bucket, StringComparison.Ordinal))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.AccessDenied, "RestrictedBucket", null, null, null));

        if (!string.IsNullOrEmpty(key) && !IsWithinPrefix(connection.KeyPrefix, key))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.AccessDenied, "RestrictedPrefix", null, null, null));
    }

    public static string CombinePrefix(StorageConnection connection, string? requested)
    {
        var required = connection.KeyPrefix ?? "";
        var asked = requested?.Trim().TrimStart('/') ?? "";
        if (asked.Length == 0)
            return required;
        if (!IsWithinPrefix(required, asked))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.AccessDenied, "RestrictedPrefix", null, null, null));
        return asked;
    }

    private static bool IsWithinPrefix(string? required, string key)
    {
        if (string.IsNullOrEmpty(required))
            return true;
        return key.StartsWith(required, StringComparison.Ordinal);
    }
}
