namespace Cavdeck.Domain.Storage;

public enum ObjectFileKind
{
    Folder,
    Image,
    Video,
    Audio,
    Code,
    Data,
    Text,
    Document,
    Pdf,
    Archive,
    Spreadsheet,
    Presentation,
    Font,
    Program,
    File
}

public static class ObjectFileKinds
{
    private static readonly Dictionary<string, ObjectFileKind> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dockerfile"] = ObjectFileKind.Code,
        ["makefile"] = ObjectFileKind.Code,
        ["jenkinsfile"] = ObjectFileKind.Code,
        ["license"] = ObjectFileKind.Text,
        ["readme"] = ObjectFileKind.Text,
        [".gitignore"] = ObjectFileKind.Data,
        [".gitattributes"] = ObjectFileKind.Data,
        [".editorconfig"] = ObjectFileKind.Data,
        [".npmrc"] = ObjectFileKind.Data,
        [".nvmrc"] = ObjectFileKind.Data
    };

    private static readonly Dictionary<string, ObjectFileKind> Extensions = BuildExtensions();

    public static ObjectFileKind Classify(string? name, bool folder)
    {
        if (folder)
            return ObjectFileKind.Folder;
        if (string.IsNullOrWhiteSpace(name))
            return ObjectFileKind.File;

        var trimmed = name.Trim();
        if (Names.TryGetValue(trimmed, out var named))
            return named;

        var lower = trimmed.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal)
            || lower.EndsWith(".tar.bz2", StringComparison.Ordinal)
            || lower.EndsWith(".tar.xz", StringComparison.Ordinal))
            return ObjectFileKind.Archive;

        var dot = lower.LastIndexOf('.');
        if (dot <= 0 || dot == lower.Length - 1)
            return ObjectFileKind.File;

        return Extensions.TryGetValue(lower[(dot + 1)..], out var kind)
            ? kind
            : ObjectFileKind.File;
    }

    private static Dictionary<string, ObjectFileKind> BuildExtensions()
    {
        var map = new Dictionary<string, ObjectFileKind>(StringComparer.Ordinal);
        Add(map, ObjectFileKind.Image, "png", "jpg", "jpeg", "gif", "webp", "bmp", "tif", "tiff", "ico", "avif", "heic", "heif", "svg");
        Add(map, ObjectFileKind.Video, "mp4", "mov", "mkv", "webm", "avi", "m4v", "mpg", "mpeg", "wmv");
        Add(map, ObjectFileKind.Audio, "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "aiff");
        Add(map, ObjectFileKind.Code, "cs", "js", "mjs", "cjs", "ts", "tsx", "mts", "cts", "jsx", "py", "pyw", "pyi", "go", "rs", "java", "kt", "kts", "cpp", "cc", "cxx", "c", "h", "hpp", "hh", "hxx", "css", "scss", "less", "html", "htm", "vue", "php", "rb", "swift", "sql", "sh", "ps1", "bat", "cmd", "lua", "dart");
        Add(map, ObjectFileKind.Data, "json", "jsonc", "yaml", "yml", "xml", "xsd", "xsl", "xslt", "xaml", "resx", "toml", "ini", "env", "config");
        Add(map, ObjectFileKind.Text, "txt", "md", "markdown", "log");
        Add(map, ObjectFileKind.Document, "doc", "docx", "odt", "rtf", "pages");
        Add(map, ObjectFileKind.Pdf, "pdf");
        Add(map, ObjectFileKind.Archive, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zipx");
        Add(map, ObjectFileKind.Spreadsheet, "xls", "xlsx", "ods", "csv", "tsv", "numbers");
        Add(map, ObjectFileKind.Presentation, "ppt", "pptx", "odp", "key");
        Add(map, ObjectFileKind.Font, "ttf", "otf", "woff", "woff2");
        Add(map, ObjectFileKind.Program, "exe", "dll", "msi", "bin");
        return map;
    }

    private static void Add(Dictionary<string, ObjectFileKind> map, ObjectFileKind kind, params string[] extensions)
    {
        foreach (var extension in extensions)
            map[extension] = kind;
    }
}
