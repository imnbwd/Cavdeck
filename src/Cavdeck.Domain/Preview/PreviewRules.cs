namespace Cavdeck.Domain.Preview;

public enum PreviewKind
{
    Folder,
    Image,
    Pdf,
    Text,
    TooLarge,
    External
}

public enum PreviewFileType
{
    Unknown,
    Image,
    Pdf,
    Text,
    Audio,
    Video
}

public readonly record struct PreviewDecision(PreviewKind Kind);

public static class PreviewRules
{
    public const long MaxTextBytes = 1_048_576;
    public const long MaxImageBytes = 15_728_640;
    public const long MaxPdfBytes = 104_857_600;
    public const long MaxShellBytes = 104_857_600;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.Ordinal)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "webp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.Ordinal)
    {
        "mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "wma"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal)
    {
        "mp4", "m4v", "mov", "avi", "mkv", "webm", "wmv"
    };

    private static readonly HashSet<string> TextExtensions = BuildTextExtensions();

    private static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "makefile", "jenkinsfile", "license", "readme",
        ".gitignore", ".gitattributes", ".editorconfig", ".npmrc", ".nvmrc", ".env"
    };

    public static PreviewFileType Classify(string? name, string? contentType = null)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmed = name.Trim();
            if (TextNames.Contains(trimmed))
                return PreviewFileType.Text;

            var lower = trimmed.ToLowerInvariant();
            var dot = lower.LastIndexOf('.');
            if (dot > 0 && dot < lower.Length - 1)
            {
                var extension = lower[(dot + 1)..];
                if (ImageExtensions.Contains(extension))
                    return PreviewFileType.Image;
                if (extension == "pdf")
                    return PreviewFileType.Pdf;
                if (TextExtensions.Contains(extension))
                    return PreviewFileType.Text;
                if (AudioExtensions.Contains(extension))
                    return PreviewFileType.Audio;
                if (VideoExtensions.Contains(extension))
                    return PreviewFileType.Video;
            }
        }

        // MIME is a fallback only. Known extensions remain authoritative, and active
        // formats such as SVG/HTML are not promoted to image previews by MIME.
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(mediaType))
            return PreviewFileType.Unknown;

        if (mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            return PreviewFileType.Pdf;
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
            return PreviewFileType.Image;
        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return PreviewFileType.Audio;
        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return PreviewFileType.Video;

        return PreviewFileType.Unknown;
    }

    public static PreviewDecision Decide(
        string? name, bool folder, long sizeBytes, string? contentType = null)
    {
        if (folder)
            return new PreviewDecision(PreviewKind.Folder);

        return Classify(name, contentType) switch
        {
            PreviewFileType.Image => Sized(sizeBytes, MaxImageBytes, PreviewKind.Image),
            PreviewFileType.Pdf => Sized(sizeBytes, MaxPdfBytes, PreviewKind.Pdf),
            PreviewFileType.Text => Sized(sizeBytes, MaxTextBytes, PreviewKind.Text),
            // Audio and video are recognized for future viewers but remain external
            // in this foundation change, preserving current UI behavior.
            _ => new PreviewDecision(PreviewKind.External)
        };
    }

    public static bool CanOpenInShell(long sizeBytes) =>
        sizeBytes >= 0 && sizeBytes <= MaxShellBytes;

    private static PreviewDecision Sized(long sizeBytes, long maxBytes, PreviewKind kind) =>
        sizeBytes < 0 || sizeBytes > maxBytes
            ? new PreviewDecision(PreviewKind.TooLarge)
            : new PreviewDecision(kind);

    private static HashSet<string> BuildTextExtensions()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        string[] extensions =
        [
            "txt", "md", "markdown", "log",
            "json", "jsonc", "yaml", "yml", "xml", "toml", "ini", "env", "config",
            "csv", "tsv",
            "svg", "html", "htm",
            "cs", "js", "mjs", "cjs", "ts", "tsx", "jsx", "py", "go", "rs", "java", "kt", "kts",
            "cpp", "cc", "c", "h", "hpp", "css", "scss", "less", "vue", "php", "rb", "swift",
            "sql", "sh", "ps1", "bat", "cmd", "lua", "dart"
        ];
        foreach (var extension in extensions)
            set.Add(extension);
        return set;
    }
}
