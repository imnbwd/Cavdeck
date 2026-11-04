namespace Cavdeck.Domain.Storage;

// ToString is intentionally redacted. Records would print the secret.
public sealed class StorageSecrets
{
    public StorageSecrets(string accessKeyId, string secretAccessKey)
    {
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
    }

    public string AccessKeyId { get; }
    public string SecretAccessKey { get; }

    public override string ToString() => "StorageSecrets";
}
