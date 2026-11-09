namespace Cavdeck.Domain.Storage;

public enum ConnectionField
{
    Name,
    AccountId,
    Region,
    Endpoint,
    AccessKeyId,
    Secret,
    Bucket,
    Prefix
}

public static class ConnectionIssueCode
{
    public const string Required = "required";
    public const string Invalid = "invalid";
    public const string UserInfo = "userinfo";
    public const string SecretRequired = "secret-required";
    public const string Duplicate = "duplicate";
}

public sealed record ConnectionIssue(ConnectionField Field, string Code);

// Class rather than a record so a log of the draft cannot print the secret.
public sealed class ConnectionDraft
{
    public Guid? Id { get; init; }
    public string Name { get; init; } = "";
    public StorageProviderKind Provider { get; init; } = StorageProviderKind.CloudflareR2;
    public string? AccountId { get; init; }
    public string? Region { get; init; }
    public string? Endpoint { get; init; }
    public bool ForcePathStyle { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretAccessKey { get; init; }
    public bool KeepExistingSecret { get; init; }
    public bool IsReadOnly { get; init; }
    public string? RestrictedBucket { get; init; }
    public string? KeyPrefix { get; init; }

    public override string ToString() => $"{Provider} '{Name}'";
}

public sealed record StorageTarget(
    string Region,
    string Endpoint,
    bool ForcePathStyle,
    string? AccountId,
    string? RestrictedBucket,
    string? KeyPrefix);
