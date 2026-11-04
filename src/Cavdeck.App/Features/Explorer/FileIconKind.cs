namespace Cavdeck.App.Features.Explorer;

// Visual identity is separate from ObjectFileKind, which supplies the localized type label.
public enum FileIconKind
{
    Default,
    Json,
    Xml,
    Markdown,
    Word,
    Excel,
    PowerPoint,
    PlainText,
    Executable,
    Csv,
    Yaml,
    Python,
    C,
    Cpp,
    CSharp,
    JavaScript,
    TypeScript,
    Java,
    Html,
    Css,
    Shell,
    Sql,
    Go,
    Rust
}

public static class FileIconKinds
{
    public static FileIconKind Classify(string? name, bool folder)
    {
        if (folder || string.IsNullOrWhiteSpace(name))
            return FileIconKind.Default;

        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot <= 0 || dot == trimmed.Length - 1)
            return FileIconKind.Default;

        return trimmed[(dot + 1)..].ToLowerInvariant() switch
        {
            "json" or "jsonc" => FileIconKind.Json,
            "xml" or "xsd" or "xsl" or "xslt" or "xaml" or "resx" => FileIconKind.Xml,
            "md" or "markdown" => FileIconKind.Markdown,
            "doc" or "docx" => FileIconKind.Word,
            "xls" or "xlsx" => FileIconKind.Excel,
            "ppt" or "pptx" => FileIconKind.PowerPoint,
            "txt" => FileIconKind.PlainText,
            "exe" or "msi" => FileIconKind.Executable,
            "csv" or "tsv" => FileIconKind.Csv,
            "yaml" or "yml" => FileIconKind.Yaml,
            "py" or "pyw" or "pyi" => FileIconKind.Python,
            "c" or "h" => FileIconKind.C,
            "cpp" or "cc" or "cxx" or "hpp" or "hh" or "hxx" => FileIconKind.Cpp,
            "cs" => FileIconKind.CSharp,
            "js" or "mjs" or "cjs" or "jsx" => FileIconKind.JavaScript,
            "ts" or "tsx" or "mts" or "cts" => FileIconKind.TypeScript,
            "java" => FileIconKind.Java,
            "html" or "htm" => FileIconKind.Html,
            "css" or "scss" or "less" => FileIconKind.Css,
            "sh" or "ps1" or "bat" or "cmd" => FileIconKind.Shell,
            "sql" => FileIconKind.Sql,
            "go" => FileIconKind.Go,
            "rs" => FileIconKind.Rust,
            _ => FileIconKind.Default
        };
    }
}
