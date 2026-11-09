using System.Security.Cryptography;
using System.Text;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

public static class CrossTransferPlanner
{
    public static string Prefix(string? key) => string.IsNullOrEmpty(key) || key.EndsWith('/')
        ? key ?? "" : key + "/";

    public static string MapKey(string key, string sourcePrefix, string destinationPrefix)
    {
        if (!key.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new ArgumentException("Key is outside the source prefix.", nameof(key));
        return destinationPrefix + key[sourcePrefix.Length..];
    }

    public static IReadOnlyList<TransferPart> Plan(long length, TransferEngineOptions options) =>
        TransferPlanner.Plan(length, options.CrossCopyPartSizeBytes, options.MinimumPartBytes,
            TransferPlanner.S3MaximumParts);

    public static bool SameBackend(StorageConnection left, StorageConnection right)
    {
        if (left.Id == right.Id)
            return true;
        if (left.Provider == StorageProviderKind.AwsS3 && right.Provider == StorageProviderKind.AwsS3
            && string.IsNullOrEmpty(left.Endpoint) && string.IsNullOrEmpty(right.Endpoint))
            return true;
        if (!string.IsNullOrEmpty(left.Endpoint) && !string.IsNullOrEmpty(right.Endpoint))
            return string.Equals(left.Endpoint.TrimEnd('/'), right.Endpoint.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        var explicitEndpoint = string.IsNullOrEmpty(left.Endpoint) ? right.Endpoint : left.Endpoint;
        var native = string.IsNullOrEmpty(left.Endpoint) ? left : right;
        return native.Provider == StorageProviderKind.AwsS3
            && Uri.TryCreate(explicitEndpoint, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase);
    }

    // Local read-only policy does not change backend identity. Access is checked
    // separately for source reads and destination writes on every attempt.
    public static string ConfigurationIdentity(StorageConnection connection) =>
        LegacyConfigurationIdentity(connection with { IsReadOnly = false });

    public static bool MatchesConfiguration(string? expected, StorageConnection connection) =>
        expected is null || expected == ConfigurationIdentity(connection)
            // Accept journals written before local policy was excluded from identity.
            || expected == LegacyConfigurationIdentity(connection with { IsReadOnly = true });

    private static string LegacyConfigurationIdentity(StorageConnection connection)
    {
        var value = string.Join('\n', connection.Provider, connection.Endpoint, connection.Region,
            connection.CredentialReference, connection.IsReadOnly, connection.RestrictedBucket,
            connection.KeyPrefix, connection.ForcePathStyle, connection.AccountId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
