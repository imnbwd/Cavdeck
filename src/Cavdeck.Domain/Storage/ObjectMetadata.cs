using System.Text;
using System.Net.Http.Headers;

namespace Cavdeck.Domain.Storage;

public sealed record ObjectHttpMetadata(string? ContentType, string? CacheControl,
    string? ContentDisposition, string? ContentEncoding, string? ContentLanguage, string? Expires);

public sealed record ObjectMetadataValues(ObjectHttpMetadata Http, IReadOnlyDictionary<string, string> Custom)
{
    public bool SameAs(ObjectMetadataValues other) => Http == other.Http &&
        Custom.Count == other.Custom.Count &&
        Custom.All(pair => other.Custom.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

public enum MetadataEditBlock { None, ReadOnly, Provider, LargeObject, Incomplete, ProtectedObject, UnverifiedPolicy }
public enum MetadataValidation { None, InvalidHeader, InvalidKey, DuplicateKey, TooLarge, InvalidContentType, InvalidExpires }

public sealed record ObjectMetadataSnapshot(ObjectHead Head, DateTimeOffset? LastModified,
    string? StorageClass, string? Encryption, string? VersionId, string? WebsiteRedirect,
    ObjectMetadataValues Values, MetadataEditBlock EditBlock)
{
    public bool SameRevision(ObjectMetadataSnapshot other) => Head == other.Head &&
        LastModified == other.LastModified && StorageClass == other.StorageClass &&
        Encryption == other.Encryption && VersionId == other.VersionId &&
        WebsiteRedirect == other.WebsiteRedirect && Values.SameAs(other.Values);
}

public static class ObjectMetadataRules
{
    public const long SingleCopyLimit = 5L * 1024 * 1024 * 1024;
    public static MetadataValidation Validate(ObjectMetadataValues values)
    {
        var h = values.Http;
        if (new[] { h.ContentType, h.CacheControl, h.ContentDisposition, h.ContentEncoding,
            h.ContentLanguage, h.Expires }.Any(value => !ValidHeader(value)))
            return MetadataValidation.InvalidHeader;
        if (h.ContentType is not null && !MediaTypeHeaderValue.TryParse(h.ContentType, out _))
            return MetadataValidation.InvalidContentType;
        if (h.Expires is not null && !DateTimeOffset.TryParseExact(h.Expires, "r",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out _))
            return MetadataValidation.InvalidExpires;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var pair in values.Custom)
        {
            if (pair.Key.Length == 0 || pair.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                || !pair.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return MetadataValidation.InvalidKey;
            if (!keys.Add(pair.Key)) return MetadataValidation.DuplicateKey;
            if (!ValidHeader(pair.Value)) return MetadataValidation.InvalidHeader;
            bytes += Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value);
        }
        return bytes > 2048 ? MetadataValidation.TooLarge : MetadataValidation.None;
    }

    // Restrict edits to printable ASCII to avoid SDK/provider-specific header encoding.
    private static bool ValidHeader(string? value) => value is null || value.All(c => c >= 32 && c <= 126);

    public static MetadataEditBlock BasicBlock(StorageConnection connection, ObjectMetadataSnapshot snapshot)
    {
        if (connection.IsReadOnly) return MetadataEditBlock.ReadOnly;
        if (!ConnectionPlanner.Capabilities(connection.Provider).CanEditObjectMetadata)
            return MetadataEditBlock.Provider;
        if (snapshot.Head.SizeBytes > SingleCopyLimit) return MetadataEditBlock.LargeObject;
        if (string.IsNullOrEmpty(snapshot.Head.ETag) || snapshot.LastModified is null)
            return MetadataEditBlock.Incomplete;
        return snapshot.EditBlock;
    }
}
