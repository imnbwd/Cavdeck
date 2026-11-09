using System.Globalization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Domain.Transfers;

public static class RetrySchedule
{
    public static bool ShouldRetry(TransferFailure failure, int attempt, int maxAttempts) =>
        failure.Retryable && attempt < maxAttempts;

    public static TimeSpan Delay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 8);
        var seconds = Math.Min(30d, 0.5d * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(seconds * 1000d);
    }
}

public static class IntegrityRules
{
    public static bool IsWholeObjectMd5(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
            return false;
        var text = RemoteIdentity.Normalize(etag);
        if (text.Contains('-', StringComparison.Ordinal) || text.Length != 32)
            return false;
        foreach (var character in text)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }

    public static IntegrityCheck Verify(long localSize, long remoteSize, string? localMd5, string? remoteEtag, bool etagIsMd5)
    {
        if (localSize != remoteSize)
            return IntegrityCheck.Mismatch;
        if (!etagIsMd5 || !IsWholeObjectMd5(remoteEtag))
            return IntegrityCheck.SizeOnly;
        if (string.IsNullOrWhiteSpace(localMd5))
            return IntegrityCheck.SizeOnly;
        return string.Equals(localMd5, RemoteIdentity.Normalize(remoteEtag!), StringComparison.OrdinalIgnoreCase)
            ? IntegrityCheck.Match
            : IntegrityCheck.Mismatch;
    }
}

public static class ResumeRules
{
    public static bool MatchesRange(string? contentRange, long requestedStart, long totalSize, long responseLength)
    {
        if (string.IsNullOrEmpty(contentRange) || !contentRange.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
            return false;
        var range = contentRange.AsSpan(6);
        var dash = range.IndexOf('-');
        var slash = range.IndexOf('/');
        if (dash <= 0 || slash <= dash + 1)
            return false;
        return long.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && long.TryParse(range[(dash + 1)..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            && long.TryParse(range[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            && start == requestedStart && total == totalSize && start < total
            && end == total - 1 && responseLength == total - start;
    }

    public static ResumeAction ForDownload(RemoteIdentity? current, RemoteIdentity? recorded, long partialBytes)
    {
        if (current is null)
            return ResumeAction.Missing;
        // A timestamp alone can have coarse resolution and cannot protect a resumed range.
        if (recorded is null || string.IsNullOrEmpty(recorded.ETag)
            || string.IsNullOrEmpty(current.ETag) || !recorded.SameContent(current))
            return ResumeAction.Restart;
        if (partialBytes > 0 && partialBytes == current.Size)
            return ResumeAction.CommitPartial;
        if (partialBytes > 0 && partialBytes < current.Size)
            return ResumeAction.ContinueRange;
        return ResumeAction.Restart;
    }
}

public static class ConflictNames
{
    public static string WithSuffix(string key, int suffix)
    {
        if (suffix < 1)
            return key;
        var folder = key.EndsWith('/');
        var trimmed = folder ? key.TrimEnd('/') : key;
        var slash = trimmed.LastIndexOf('/');
        var directory = slash >= 0 ? trimmed[..(slash + 1)] : "";
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        var dot = name.LastIndexOf('.');
        var stamped = dot > 0
            ? $"{name[..dot]} ({suffix}){name[dot..]}"
            : $"{name} ({suffix})";
        return folder ? directory + stamped + "/" : directory + stamped;
    }

    public static string NextFree(string key, ISet<string> taken, int maxAttempts)
    {
        if (!taken.Contains(key))
            return key;
        for (var suffix = 1; suffix <= maxAttempts; suffix++)
        {
            var candidate = WithSuffix(key, suffix);
            if (!taken.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("No free object name was available.");
    }
}

public static class TransferPaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Partial(string finalPath) => finalPath + ".cavdeck-partial";

    public static string Partial(string finalPath, Guid jobId, bool isolated = false) =>
        TemporaryBase(finalPath, isolated) + ".cavdeck-" + jobId.ToString("N") + ".partial";

    public static string Commit(string finalPath) => finalPath + ".cavdeck-commit";

    public static string Commit(string finalPath, Guid jobId, bool isolated = false) =>
        TemporaryBase(finalPath, isolated) + ".cavdeck-" + jobId.ToString("N") + ".commit";

    private static string TemporaryBase(string finalPath, bool isolated) =>
        isolated ? Path.GetDirectoryName(finalPath) + Path.DirectorySeparatorChar : finalPath;

    public static bool TryCombine(string root, string relativeKey, ISet<string> used, out string path, out string? error)
    {
        path = "";
        error = null;
        var segments = relativeKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            error = "Escape";
            return false;
        }

        var safe = new string[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = Sanitize(segments[index]);
            if (segment.Length == 0)
            {
                error = "Invalid";
                return false;
            }

            safe[index] = segment;
        }

        path = safe.Aggregate(root, Path.Combine);
        if (!used.Add(path))
        {
            error = "Collision";
            path = "";
            return false;
        }

        return true;
    }

    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        if (cleaned.Length == 0)
            return "_";
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        return Reserved.Contains(stem) ? "_" + cleaned : cleaned;
    }
}

public static class TransferFailures
{
    public static TransferFailure FromStorage(StorageException exception, bool includeEndpointFailures = false)
    {
        var failure = exception.Failure;
        var code = failure.ErrorCode;
        var throttled = failure.StatusCode == 429
            || code is "SlowDown" or "TooManyRequests" or "Throttling";
        var transient = failure.Kind == StorageFailureKind.Network
            || includeEndpointFailures && failure.Kind == StorageFailureKind.Endpoint
            || failure.StatusCode is 408 or 500 or 502 or 503 or 504
            || code is "InternalError" or "ServiceUnavailable" or "RequestTimeout" or "RequestTimeTooSkewed";

        if (string.Equals(code, "NoSuchUpload", StringComparison.OrdinalIgnoreCase))
            return new TransferFailure(TransferFailureKind.Network, true, code, failure.Detail);
        if (string.Equals(code, "SourceChanged", StringComparison.OrdinalIgnoreCase))
            return new TransferFailure(TransferFailureKind.Integrity, false, code, failure.Detail);
        var kind = failure.Kind switch
        {
            StorageFailureKind.AccessDenied => TransferFailureKind.AccessDenied,
            StorageFailureKind.InvalidCredentials => TransferFailureKind.AccessDenied,
            StorageFailureKind.CredentialMissing => TransferFailureKind.AccessDenied,
            StorageFailureKind.ReadOnly => TransferFailureKind.ReadOnly,
            StorageFailureKind.Unsupported => TransferFailureKind.Unsupported,
            StorageFailureKind.PreconditionFailed => TransferFailureKind.Conflict,
            StorageFailureKind.Cancelled => TransferFailureKind.Unknown,
            StorageFailureKind.BucketNotFound when code is "NoSuchKey" or "NotFound" => TransferFailureKind.NotFound,
            StorageFailureKind.BucketNotFound => TransferFailureKind.NotFound,
            _ when failure.StatusCode is 409 or 412 => TransferFailureKind.Conflict,
            _ when throttled => TransferFailureKind.Throttled,
            _ when transient => TransferFailureKind.Network,
            _ => TransferFailureKind.Unknown
        };

        if (throttled)
            kind = TransferFailureKind.Throttled;
        else if (transient && kind is not TransferFailureKind.AccessDenied and not TransferFailureKind.ReadOnly)
            kind = TransferFailureKind.Network;

        var retryable = kind is TransferFailureKind.Network or TransferFailureKind.Throttled;
        return new TransferFailure(kind, retryable, code, failure.Detail);
    }

    public static TransferFailure FromLocal(Exception exception)
    {
        if (IsDiskFull(exception))
            return new TransferFailure(TransferFailureKind.Disk, false, "DiskFull", "The disk is full.");
        if (IsSharingViolation(exception))
            return new TransferFailure(TransferFailureKind.Busy, true, "SharingViolation", "The file is in use.");
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                new TransferFailure(TransferFailureKind.LocalMissing, false, "NotFound", "The local file is missing."),
            UnauthorizedAccessException =>
                new TransferFailure(TransferFailureKind.AccessDenied, false, "AccessDenied", "The local path is not accessible."),
            PathTooLongException =>
                new TransferFailure(TransferFailureKind.InvalidPath, false, "PathTooLong", "The path is too long."),
            IOException =>
                new TransferFailure(TransferFailureKind.Busy, true, "IO", "The file could not be read or written."),
            _ => new TransferFailure(TransferFailureKind.Unknown, false, exception.GetType().Name, exception.Message)
        };
    }

    public static bool IsDiskFull(Exception exception)
    {
        const int diskFull = unchecked((int)0x80070070);
        const int handleDiskFull = unchecked((int)0x80070027);
        if (exception.HResult is diskFull or handleDiskFull)
            return true;
        return exception.InnerException is not null && IsDiskFull(exception.InnerException);
    }

    private static bool IsSharingViolation(Exception exception)
    {
        const int sharing = unchecked((int)0x80070020);
        if (exception.HResult == sharing)
            return true;
        return exception.InnerException is not null && IsSharingViolation(exception.InnerException);
    }
}

public static class ContentTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".csv"] = "text/csv; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".mp4"] = "video/mp4",
        [".mp3"] = "audio/mpeg",
        [".wasm"] = "application/wasm",
        [".woff2"] = "font/woff2"
    };

    public static string? FromPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length == 0 ? null : Map.GetValueOrDefault(extension);
    }
}
