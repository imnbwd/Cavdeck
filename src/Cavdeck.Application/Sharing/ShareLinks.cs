using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Sharing;

public sealed record ShareLink(string Url, bool IsPublic, DateTimeOffset? ExpiresAt);

public interface IDownloadLinkSigner
{
    Task<ShareLink> SignDownloadAsync(StorageConnection connection, string bucket,
        string key, TimeSpan lifetime, CancellationToken cancellationToken);
}

public static class ShareLinks
{
    public static string BucketSettingKey(Guid connectionId, string bucket) =>
        $"{connectionId:N}/{bucket}";

    public static bool TryPublicBase(string? input, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0)
            return false;
        normalized = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    public static ShareLink Public(string bucketRoot, string key)
    {
        if (!TryPublicBase(bucketRoot, out var root) || string.IsNullOrEmpty(key))
            throw new ArgumentException("A valid HTTPS bucket root and object key are required.");
        var path = string.Join("/", key.Split('/').Select(segment => segment switch
        {
            "." => "%2E",
            ".." => "%2E%2E",
            _ => Uri.EscapeDataString(segment)
        }));
        return new ShareLink($"{root}/{path}", true, null);
    }

    public static bool ValidLifetime(TimeSpan lifetime) =>
        lifetime >= TimeSpan.FromMinutes(1) && lifetime <= TimeSpan.FromDays(7);
}
