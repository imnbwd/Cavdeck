namespace Cavdeck.Domain.Storage;

public static class ConnectionPlanner
{
    public const string R2Region = "auto";

    public static ProviderCapabilities Capabilities(StorageProviderKind kind) => kind switch
    {
        StorageProviderKind.AwsS3 => new(true, true, true, true, true, true),
        // R2 can presign and copy on its S3 API host. Versioning and the AWS bucket
        // settings surface are not the same product, so those stay off.
        StorageProviderKind.CloudflareR2 => new(true, true, false, false, true, false),
        StorageProviderKind.Minio => new(true, true, true, true, true, false),
        _ => new(true, true, false, false, true, false)
    };

    public static bool CanListAllBuckets(StorageConnection connection) =>
        string.IsNullOrEmpty(connection.RestrictedBucket)
        && Capabilities(connection.Provider).CanListBuckets;

    public static bool TryResolve(
        ConnectionDraft draft,
        out StorageTarget? target,
        out IReadOnlyList<ConnectionIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var found = new List<ConnectionIssue>();
        target = null;

        AddName(found, draft.Name);
        if (!TryPrefix(draft.KeyPrefix, out var prefix))
            found.Add(new ConnectionIssue(ConnectionField.Prefix, ConnectionIssueCode.Invalid));

        var bucket = string.IsNullOrWhiteSpace(draft.RestrictedBucket)
            ? null
            : draft.RestrictedBucket.Trim();
        if (bucket is not null && !IsBucket(bucket))
            found.Add(new ConnectionIssue(ConnectionField.Bucket, ConnectionIssueCode.Invalid));

        string region;
        string endpoint;
        var pathStyle = draft.ForcePathStyle;
        string? accountId = null;

        switch (draft.Provider)
        {
            case StorageProviderKind.CloudflareR2:
                accountId = draft.AccountId?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(accountId))
                    found.Add(new ConnectionIssue(ConnectionField.AccountId, ConnectionIssueCode.Required));
                else if (!IsAccountId(accountId))
                    found.Add(new ConnectionIssue(ConnectionField.AccountId, ConnectionIssueCode.Invalid));
                region = R2Region;
                endpoint = string.IsNullOrEmpty(accountId)
                    ? ""
                    : $"https://{accountId}.r2.cloudflarestorage.com";
                pathStyle = true;
                break;

            case StorageProviderKind.AwsS3:
                region = NormalizeRegion(draft.Region);
                if (string.IsNullOrEmpty(region))
                    found.Add(new ConnectionIssue(ConnectionField.Region, ConnectionIssueCode.Required));
                else if (!IsAwsRegion(region))
                    found.Add(new ConnectionIssue(ConnectionField.Region, ConnectionIssueCode.Invalid));

                if (string.IsNullOrWhiteSpace(draft.Endpoint))
                {
                    endpoint = "";
                    pathStyle = false;
                }
                else if (!TryEndpoint(draft.Endpoint, out endpoint, out var endpointCode))
                    found.Add(new ConnectionIssue(ConnectionField.Endpoint, endpointCode!));
                break;

            default:
                region = NormalizeRegion(draft.Region);
                if (string.IsNullOrEmpty(region))
                    region = "us-east-1";
                else if (!IsAwsRegion(region))
                    found.Add(new ConnectionIssue(ConnectionField.Region, ConnectionIssueCode.Invalid));

                if (!TryEndpoint(draft.Endpoint ?? "", out endpoint, out var code))
                    found.Add(new ConnectionIssue(ConnectionField.Endpoint, code!));
                break;
        }

        if (found.Count > 0)
        {
            issues = found;
            return false;
        }

        target = new StorageTarget(region, endpoint, pathStyle, accountId, bucket, prefix);
        issues = found;
        return true;
    }

    public static IReadOnlyList<ConnectionIssue> ValidateCredentials(
        string? accessKeyId, string? secret, bool requireSecret)
    {
        var issues = new List<ConnectionIssue>();
        if (string.IsNullOrWhiteSpace(accessKeyId))
            issues.Add(new ConnectionIssue(ConnectionField.AccessKeyId, ConnectionIssueCode.Required));
        else if (accessKeyId.Any(char.IsWhiteSpace) || accessKeyId.Trim().Length is < 1 or > 128)
            issues.Add(new ConnectionIssue(ConnectionField.AccessKeyId, ConnectionIssueCode.Invalid));

        var trimmedSecret = secret?.Trim() ?? "";
        if (trimmedSecret.Length == 0)
        {
            if (requireSecret)
                issues.Add(new ConnectionIssue(ConnectionField.Secret, ConnectionIssueCode.SecretRequired));
        }
        else if (trimmedSecret.Length > 1024)
            issues.Add(new ConnectionIssue(ConnectionField.Secret, ConnectionIssueCode.Invalid));

        return issues;
    }

    private static void AddName(List<ConnectionIssue> issues, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new ConnectionIssue(ConnectionField.Name, ConnectionIssueCode.Required));
            return;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 80 || trimmed.Any(char.IsControl))
            issues.Add(new ConnectionIssue(ConnectionField.Name, ConnectionIssueCode.Invalid));
    }

    private static string NormalizeRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) ? "" : region.Trim().ToLowerInvariant();

    private static bool IsAwsRegion(string region) =>
        region.Length is >= 1 and <= 32
        && !string.Equals(region, R2Region, StringComparison.Ordinal)
        && region.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsAccountId(string value) =>
        value.Length is >= 16 and <= 64 && value.All(char.IsAsciiLetterOrDigit);

    private static bool IsBucket(string value) =>
        value.Length is >= 1 and <= 255
        && !value.Contains('/')
        && !value.Contains('\\')
        && !value.Any(char.IsWhiteSpace);

    private static bool TryPrefix(string? raw, out string? prefix)
    {
        prefix = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var text = raw.Trim().Replace('\\', '/').TrimStart('/');
        if (text.Length == 0)
            return true;
        if (text.Length > 1024 || text.Any(char.IsControl))
            return false;
        if (text.Split('/').Any(segment => segment is "." or ".."))
            return false;

        prefix = text;
        return true;
    }

    private static bool TryEndpoint(string raw, out string endpoint, out string? code)
    {
        endpoint = "";
        code = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            code = ConnectionIssueCode.Required;
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            code = ConnectionIssueCode.Invalid;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            code = ConnectionIssueCode.UserInfo;
            return false;
        }

        if (uri.AbsolutePath is not "/" and not ""
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            code = ConnectionIssueCode.Invalid;
            return false;
        }

        endpoint = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
