namespace Cavdeck.Domain.Storage;

public enum ObjectEntryKind { Folder, File }

public enum ListingSort { Name, Size, Modified }

public enum NameIssue { None, Empty, Separator, Control, Reserved, TooLong }

public enum ConflictPolicy { FailIfExists, SkipExisting, Overwrite, Rename }

public sealed record ObjectEntry(
    ObjectEntryKind Kind,
    string Name,
    string Key,
    long SizeBytes,
    DateTimeOffset? LastModified,
    string? ETag);

public sealed record PrefixPage(
    IReadOnlyList<string> CommonPrefixes,
    IReadOnlyList<StorageObject> Objects,
    string? ContinuationToken);

public sealed record PrefixCrumb(string Name, string Prefix);

public sealed record MutationIssue(string Key, StorageFailure Failure);

public sealed record MutationReport(
    int Succeeded,
    int Skipped,
    int Failed,
    bool BlockedOnConflict,
    int ConflictCount,
    IReadOnlyList<MutationIssue> Issues)
{
    public IReadOnlyList<string> SucceededKeys { get; init; } = [];
    public bool IsFullSuccess => Succeeded > 0 && Skipped == 0 && Failed == 0 && !BlockedOnConflict;
    public static MutationReport None { get; } = new(0, 0, 0, false, 0, []);

    public static MutationReport Conflict(int count) =>
        new(0, 0, 0, true, count, []);

    public static MutationReport FromIssue(string key, StorageFailure failure) =>
        new(0, 0, 1, false, 0, [new MutationIssue(key, failure)]);
}

public sealed record BrowseResult(
    string Prefix,
    IReadOnlyList<ObjectEntry> Entries,
    string? ContinuationToken);

// Prefixes are object-key conventions, not directories. A folder key always ends with '/'.
public static class ObjectLayout
{
    public const int MaxKeyBytes = 1020;

    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return "";

        var text = prefix.Trim().Replace('\\', '/').TrimStart('/');
        if (text.Length == 0)
            return "";
        if (text.Any(char.IsControl) || text.Split('/').Any(segment => segment is "." or ".."))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.InvalidConfiguration, "Prefix", null, null, null));

        return text.EndsWith('/') ? text : text + "/";
    }

    public static string RootPrefix(StorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return NormalizePrefix(connection.KeyPrefix);
    }

    public static string? ParentPrefix(string prefix)
    {
        var normalized = NormalizePrefix(prefix);
        if (normalized.Length == 0)
            return null;
        var body = normalized.TrimEnd('/');
        var slash = body.LastIndexOf('/');
        return slash < 0 ? "" : body[..(slash + 1)];
    }

    public static IReadOnlyList<PrefixCrumb> Crumbs(string prefix)
    {
        var normalized = NormalizePrefix(prefix);
        if (normalized.Length == 0)
            return [];

        var parts = normalized.TrimEnd('/').Split('/');
        var crumbs = new List<PrefixCrumb>(parts.Length);
        var current = "";
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue;
            current += part + "/";
            crumbs.Add(new PrefixCrumb(part, current));
        }

        return crumbs;
    }

    public static NameIssue InspectName(string? raw, out string name)
    {
        name = raw?.Trim() ?? "";
        if (name.Length == 0)
            return NameIssue.Empty;
        if (name.Contains('/') || name.Contains('\\'))
            return NameIssue.Separator;
        if (name is "." or "..")
            return NameIssue.Reserved;
        if (name.Any(char.IsControl))
            return NameIssue.Control;
        return NameIssue.None;
    }

    public static bool TryJoin(string prefix, string rawName, bool folder, out string key, out NameIssue issue)
    {
        issue = InspectName(rawName, out var name);
        key = "";
        if (issue != NameIssue.None)
            return false;

        var joined = NormalizePrefix(prefix) + name + (folder ? "/" : "");
        if (System.Text.Encoding.UTF8.GetByteCount(joined) > MaxKeyBytes)
        {
            issue = NameIssue.TooLong;
            return false;
        }

        key = joined;
        return true;
    }

    public static string DisplayName(string key)
    {
        var trimmed = key.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    public static string SiblingName(string name, int copyIndex)
    {
        if (copyIndex <= 1)
            return name;
        var dot = name.LastIndexOf('.');
        var suffix = $" ({copyIndex})";
        if (dot > 0 && name[(dot + 1)..].Length is > 0 and <= 8 && !name[(dot + 1)..].Contains(' '))
            return string.Concat(name.AsSpan(0, dot), suffix, name.AsSpan(dot));
        return name + suffix;
    }

    public static string MapKey(string key, string sourcePrefix, string destinationPrefix)
    {
        var source = NormalizePrefix(sourcePrefix);
        var destination = NormalizePrefix(destinationPrefix);
        if (!key.StartsWith(source, StringComparison.Ordinal))
            throw new ArgumentException("Key is outside the source prefix.", nameof(key));
        return destination + key[source.Length..];
    }

    public static bool IsInside(string candidatePrefix, string ancestorPrefix)
    {
        var ancestor = NormalizePrefix(ancestorPrefix);
        if (ancestor.Length == 0)
            return false;
        var candidate = NormalizePrefix(candidatePrefix);
        return candidate.StartsWith(ancestor, StringComparison.Ordinal);
    }

    public static string ObjectUri(string bucket, string key) => $"s3://{bucket}/{key}";

    public static IReadOnlyList<ObjectEntry> Compose(
        string prefix,
        IReadOnlyList<string> commonPrefixes,
        IReadOnlyList<StorageObject> objects)
    {
        var normalized = NormalizePrefix(prefix);
        var entries = new List<ObjectEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in commonPrefixes)
        {
            if (!TryImmediateFolder(normalized, folder, out var key, out var name))
                continue;
            if (seen.Add(key))
                entries.Add(new ObjectEntry(ObjectEntryKind.Folder, name, key, 0, null, null));
        }

        foreach (var item in objects)
        {
            if (string.IsNullOrEmpty(item.Key) || item.Key == normalized)
                continue;
            if (!item.Key.StartsWith(normalized, StringComparison.Ordinal))
                continue;

            var relative = item.Key[normalized.Length..];
            if (relative.Length == 0 || relative.Contains('/') && !relative.EndsWith('/'))
                continue;

            if (relative.EndsWith('/'))
            {
                if (relative[..^1].Contains('/'))
                    continue;
                if (!seen.Add(item.Key))
                    continue;
                entries.Add(new ObjectEntry(
                    ObjectEntryKind.Folder,
                    relative.TrimEnd('/'),
                    item.Key,
                    0,
                    item.LastModified,
                    item.ETag));
                continue;
            }

            if (seen.Add(item.Key))
                entries.Add(new ObjectEntry(
                    ObjectEntryKind.File,
                    relative,
                    item.Key,
                    item.SizeBytes,
                    item.LastModified,
                    item.ETag));
        }

        return entries;
    }

    public static IReadOnlyList<ObjectEntry> Filter(IReadOnlyList<ObjectEntry> entries, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return entries;
        var needle = query.Trim();
        return entries
            .Where(entry => entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static IReadOnlyList<ObjectEntry> Sort(
        IReadOnlyList<ObjectEntry> entries, ListingSort sort, bool descending)
    {
        IOrderedEnumerable<ObjectEntry> ordered = entries.OrderBy(entry => entry.Kind == ObjectEntryKind.Folder ? 0 : 1);
        ordered = sort switch
        {
            ListingSort.Size => descending
                ? ordered.ThenByDescending(entry => entry.SizeBytes).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(entry => entry.SizeBytes).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            ListingSort.Modified => descending
                ? ordered.ThenByDescending(entry => entry.LastModified ?? DateTimeOffset.MinValue)
                : ordered.ThenBy(entry => entry.LastModified ?? DateTimeOffset.MaxValue),
            _ => descending
                ? ordered.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        };
        return ordered.ToList();
    }

    public static StorageFailure NameFailure(NameIssue issue) =>
        new(StorageFailureKind.InvalidConfiguration, issue.ToString(), null, null, null);

    public static StorageFailure Named(string code) =>
        new(StorageFailureKind.InvalidConfiguration, code, null, null, null);

    private static bool TryImmediateFolder(string prefix, string folder, out string key, out string name)
    {
        key = "";
        name = "";
        string normalized;
        try
        {
            normalized = NormalizePrefix(folder);
        }
        catch (StorageException)
        {
            return false;
        }

        if (normalized.Length == 0 || normalized == prefix || !normalized.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var relative = normalized[prefix.Length..].TrimEnd('/');
        if (relative.Length == 0 || relative.Contains('/'))
            return false;

        key = normalized;
        name = relative;
        return true;
    }
}
