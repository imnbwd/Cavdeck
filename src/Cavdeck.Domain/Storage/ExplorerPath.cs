using System.Text;

namespace Cavdeck.Domain.Storage;

public enum ExplorerPathIssue { None, Required, Unsupported, Invalid, TooLong }

public readonly record struct ExplorerPathInput(string Text, bool BucketPath = false, bool FileOnly = false);

public readonly record struct ExplorerPath(string Key, bool DirectoryOnly)
{
    private const int S3MaxKeyBytes = 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ExplorerPathIssue ParseTarget(ExplorerPathInput input, string currentBucket,
        string root, string current, out string bucket, out ExplorerPath path)
    {
        bucket = currentBucket;
        path = default;
        var text = input.Text?.TrimStart() ?? "";
        var uri = text.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);
        if (!uri && !input.BucketPath)
            return Parse(text, root, current, out path);

        // Copy URI emits literal object keys. Only the ordinary path syntax folds separators.
        var target = uri ? text[5..] : text.TrimEnd().Replace('\\', '/');
        if (target.Length == 0)
            return uri ? ExplorerPathIssue.Invalid : ExplorerPathIssue.Required;
        var slash = target.IndexOf('/');
        var name = slash < 0 ? target : target[..slash];
        if (name.Length == 0 || name is "." or ".."
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            return ExplorerPathIssue.Invalid;

        bucket = name;
        var key = slash < 0 ? "" : target[(slash + 1)..];
        if (key.Length == 0)
            return ValidateKey(root, directoryOnly: true, out path);

        var directoryOnly = key.EndsWith('/');
        // Browsing normalizes directory prefixes. A literal backslash in a filename can
        // still be resolved by HEAD, but one in a parent prefix cannot be browsed faithfully.
        var parentLength = key.LastIndexOf('/') + 1;
        if (key.StartsWith('/') || HasInvalidSegments(key, directoryOnly)
            || key.AsSpan(0, parentLength).Contains('\\'))
            return ExplorerPathIssue.Invalid;

        return ValidateKey(uri ? key : root + key, directoryOnly, out path);
    }

    public static ExplorerPathIssue Parse(string? input, string root, string current, out ExplorerPath path)
    {
        path = default;
        var value = input?.Trim().Replace('\\', '/') ?? "";
        if (value.Length == 0)
            return ExplorerPathIssue.Required;
        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains("://", StringComparison.Ordinal)
            || value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1..3] == ":/")
            return ExplorerPathIssue.Unsupported;

        var absolute = value.StartsWith('/');
        var directoryOnly = value.EndsWith('/');
        var relative = absolute ? value[1..] : value;
        if (HasInvalidSegments(relative, directoryOnly))
            return ExplorerPathIssue.Invalid;

        return ValidateKey((absolute ? root : current) + relative, directoryOnly, out path);
    }

    private static bool HasInvalidSegments(string path, bool directoryOnly)
    {
        var body = directoryOnly && path.Length > 0 ? path[..^1] : path;
        return path.Any(char.IsControl)
            || body.Length > 0 && body.Split('/').Any(segment => segment is "" or "." or "..");
    }

    private static ExplorerPathIssue ValidateKey(string key, bool directoryOnly, out ExplorerPath path)
    {
        path = default;
        try
        {
            if (!FitsKey(key))
                return ExplorerPathIssue.TooLong;
        }
        catch (EncoderFallbackException)
        {
            return ExplorerPathIssue.Invalid;
        }

        path = new ExplorerPath(key, directoryOnly);
        return ExplorerPathIssue.None;
    }

    public static bool FitsKey(string key) => Utf8.GetByteCount(key) <= S3MaxKeyBytes;
}
