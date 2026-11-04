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

    private static readonly HashSet<string> TextExtensions = BuildTextExtensions();

    private static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "makefile", "jenkinsfile", "license", "readme",
        ".gitignore", ".gitattributes", ".editorconfig", ".npmrc", ".nvmrc", ".env"
    };

    public static PreviewDecision Decide(string? name, bool folder, long sizeBytes)
    {
        if (folder)
            return new PreviewDecision(PreviewKind.Folder);

        var slot = Classify(name);
        if (slot == Slot.Image)
            return Sized(sizeBytes, MaxImageBytes, PreviewKind.Image);
        if (slot == Slot.Pdf)
            return Sized(sizeBytes, MaxPdfBytes, PreviewKind.Pdf);
        if (slot == Slot.Text)
            return Sized(sizeBytes, MaxTextBytes, PreviewKind.Text);
        return new PreviewDecision(PreviewKind.External);
    }

    public static bool CanOpenInShell(long sizeBytes) =>
        sizeBytes >= 0 && sizeBytes <= MaxShellBytes;

    private static PreviewDecision Sized(long sizeBytes, long maxBytes, PreviewKind kind) =>
        sizeBytes < 0 || sizeBytes > maxBytes
            ? new PreviewDecision(PreviewKind.TooLarge)
            : new PreviewDecision(kind);

    private static Slot Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Slot.Other;

        var trimmed = name.Trim();
        if (TextNames.Contains(trimmed))
            return Slot.Text;

        var lower = trimmed.ToLowerInvariant();
        var dot = lower.LastIndexOf('.');
        if (dot <= 0 || dot == lower.Length - 1)
            return Slot.Other;

        var extension = lower[(dot + 1)..];
        if (ImageExtensions.Contains(extension))
            return Slot.Image;
        if (extension == "pdf")
            return Slot.Pdf;
        return TextExtensions.Contains(extension) ? Slot.Text : Slot.Other;
    }

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

    private enum Slot { Other, Image, Pdf, Text }
}
