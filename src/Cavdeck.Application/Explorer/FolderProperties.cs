using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Explorer;

public sealed record FolderProperties(long SizeBytes, long Files, long Subfolders, long ListRequests);

/// <summary>Read-only statistics for current objects, not historical versions or billed storage.</summary>
internal static class FolderPropertiesScanner
{
    public static async Task<FolderProperties> ScanAsync(IObjectStorageProvider provider,
        StorageConnection connection, string bucket, IReadOnlyList<ObjectEntry> selection,
        Action<FolderProperties>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 0)
            throw new ArgumentException("A scope is required.", nameof(selection));

        // Validate every selected key before issuing any remote requests.
        foreach (var item in selection)
        {
            token.ThrowIfCancellationRequested();
            ObjectAccess.Ensure(connection, bucket, item.Key, write: false);
            if (item.Key.Length == 0 && !string.IsNullOrEmpty(connection.KeyPrefix))
                throw new StorageException(new StorageFailure(StorageFailureKind.AccessDenied,
                    "RestrictedPrefix", null, null, null));
            if (item.Kind == ObjectEntryKind.Folder && item.Key.Length > 0 && !item.Key.EndsWith('/'))
                throw new ArgumentException("A folder must be an exact slash-terminated prefix.");
        }

        var roots = new List<string>();
        foreach (var prefix in selection.Where(item => item.Kind == ObjectEntryKind.Folder)
                     .Select(item => item.Key).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            if (roots.Count == 0 || !prefix.StartsWith(roots[^1], StringComparison.Ordinal))
                roots.Add(prefix);
        }
        var folders = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0, files = 0, requests = 0;
        FolderProperties Snapshot() => new(bytes, files, folders.Count, requests);

        foreach (var prefix in roots)
        {
            string? cursor = null;
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                token.ThrowIfCancellationRequested();
                var page = await provider.ListExactPageAsync(connection, bucket, prefix,
                    true, cursor, 1000, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (page.Objects.Count > 1000 || page.CommonPrefixes.Count != 0)
                    throw new InvalidOperationException("Invalid recursive listing response.");
                requests++;
                foreach (var item in page.Objects)
                {
                    token.ThrowIfCancellationRequested();
                    if (!item.Key.StartsWith(prefix, StringComparison.Ordinal) || item.SizeBytes < 0)
                        throw new InvalidOperationException("Invalid object in recursive listing.");
                    // Include empty logical folders, and count non-empty slash objects as data.
                    for (var slash = item.Key.IndexOf('/', prefix.Length); slash >= 0;
                         slash = item.Key.IndexOf('/', slash + 1))
                        folders.Add(item.Key[..(slash + 1)]);
                    if (item.Key.EndsWith('/') && item.SizeBytes == 0)
                        continue;
                    checked { bytes += item.SizeBytes; files++; }
                }
                cursor = page.ContinuationToken;
                if (!string.IsNullOrEmpty(cursor) && !tokens.Add(cursor))
                    throw new InvalidOperationException("The storage provider repeated a continuation token.");
                progress?.Invoke(Snapshot());
            } while (!string.IsNullOrEmpty(cursor));
        }

        // Explicitly selected files outside the selected folders are refreshed individually.
        // Folder scans never perform a HEAD per object.
        foreach (var key in selection.Where(item => item.Kind == ObjectEntryKind.File)
                     .Select(item => item.Key).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (roots.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
                continue;
            var head = await provider.HeadObjectAsync(connection, bucket, key, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (head.SizeBytes < 0 || head.Key != key)
                throw new InvalidOperationException("Invalid object metadata.");
            checked { bytes += head.SizeBytes; files++; }
            progress?.Invoke(Snapshot());
        }
        token.ThrowIfCancellationRequested();
        return Snapshot();
    }
}
