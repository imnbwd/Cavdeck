using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Connections;

// Names and local write policy can change without disturbing a browsing session.
public sealed record ConnectionChange(
    StorageConnection? Previous, StorageConnection Current, bool CredentialsChanged = false)
{
    public bool RequiresReconnect => Previous is not null && (CredentialsChanged
        || Previous.ClientRevision != Current.ClientRevision
        || Previous.Provider != Current.Provider
        || Previous.Endpoint != Current.Endpoint
        || Previous.Region != Current.Region
        || Previous.CredentialReference != Current.CredentialReference
        || Previous.ForcePathStyle != Current.ForcePathStyle
        || Previous.AccountId != Current.AccountId
        || Previous.RestrictedBucket != Current.RestrictedBucket
        || Previous.KeyPrefix != Current.KeyPrefix);

    public static bool ContainsLocation(StorageConnection connection, string? bucket, string prefix) =>
        (string.IsNullOrEmpty(connection.RestrictedBucket) || connection.RestrictedBucket == bucket)
        && (string.IsNullOrEmpty(connection.KeyPrefix)
            || prefix.StartsWith(connection.KeyPrefix, StringComparison.Ordinal));
}
