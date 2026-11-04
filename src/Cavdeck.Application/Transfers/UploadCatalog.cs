using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Transfers;

public sealed record UploadSource(string LocalPath, string Key, long? Size, bool FolderMarker, string? Error);

public static class UploadCatalog
{
    public static IReadOnlyList<UploadSource> Expand(string keyPrefix, IEnumerable<string> paths) =>
        Enumerate(keyPrefix, paths).ToList();

    public static IEnumerable<UploadSource> Enumerate(string keyPrefix, IEnumerable<string> paths)
    {
        var prefix = keyPrefix;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield return new UploadSource("", prefix, null, false, "Missing");
                continue;
            }

            string? full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception)
            {
                full = null;
            }
            if (full is null)
            {
                yield return new UploadSource(raw, prefix, null, false, "Invalid");
                continue;
            }

            if (Directory.Exists(full))
            {
                foreach (var source in Walk(prefix, DirectoryParent(full), full))
                    yield return source;
            }
            else if (File.Exists(full))
                yield return AddFile(prefix, Path.GetDirectoryName(full) ?? full, full);
            else
                yield return new UploadSource(full, prefix, null, false, "Missing");
        }
    }

    private static IEnumerable<UploadSource> Walk(string prefix, string parent, string directory)
    {
        FileInfo[] files = [];
        DirectoryInfo[] children = [];
        string? error = null;
        try
        {
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                error = "Reparse";
            else
            {
                files = info.GetFiles();
                children = info.GetDirectories();
            }
        }
        catch (Exception)
        {
            error = "Unreadable";
        }

        if (error is not null)
        {
            yield return new UploadSource(directory, prefix, null, false, error);
            yield break;
        }

        foreach (var file in files)
            yield return AddFile(prefix, parent, file.FullName);
        foreach (var child in children)
        {
            foreach (var source in Walk(prefix, parent, child.FullName))
                yield return source;
        }
        if (files.Length == 0 && children.Length == 0)
            yield return Make(directory, Join(prefix, parent, directory) + "/", 0, true, null);
    }

    private static UploadSource AddFile(string prefix, string parent, string file)
    {
        long size;
        try
        {
            var info = new FileInfo(file);
            // A file symlink can otherwise read and upload a target outside the chosen tree.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new UploadSource(file, prefix, null, false, "Reparse");
            size = info.Length;
        }
        catch (Exception)
        {
            return new UploadSource(file, prefix, null, false, "Unreadable");
        }

        var key = Join(prefix, parent, file);
        var error = KeyError(key);
        return Make(file, key, size, false, error);
    }

    private static UploadSource Make(string path, string key, long? size, bool folder, string? error) =>
        new(path, key, size, folder, error);

    private static string Join(string prefix, string parent, string path)
    {
        var relative = Path.GetRelativePath(parent, path).Replace('\\', '/');
        return prefix + relative;
    }

    private static string? KeyError(string key)
    {
        if (key.Split('/').Any(segment => segment is "." or ".."))
            return "Escape";
        return System.Text.Encoding.UTF8.GetByteCount(key) > ObjectLayout.MaxKeyBytes ? "TooLong" : null;
    }

    private static string DirectoryParent(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(trimmed) ?? trimmed;
    }
}
