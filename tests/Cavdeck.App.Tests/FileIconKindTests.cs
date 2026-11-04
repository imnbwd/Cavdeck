using Cavdeck.App.Features.Explorer;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class FileIconKindTests
{
    [Theory]
    [InlineData("payload.JSON", FileIconKind.Json)]
    [InlineData("settings.jsonc", FileIconKind.Json)]
    [InlineData("layout.xml", FileIconKind.Xml)]
    [InlineData("北京13清单计价规范(增值税).XSD", FileIconKind.Xml)]
    [InlineData("schema.xsd", FileIconKind.Xml)]
    [InlineData("transform.xsl", FileIconKind.Xml)]
    [InlineData("transform.xslt", FileIconKind.Xml)]
    [InlineData("window.xaml", FileIconKind.Xml)]
    [InlineData("Strings.resx", FileIconKind.Xml)]
    [InlineData("README.md", FileIconKind.Markdown)]
    [InlineData("guide.markdown", FileIconKind.Markdown)]
    [InlineData("report.docx", FileIconKind.Word)]
    [InlineData("legacy.doc", FileIconKind.Word)]
    [InlineData("budget.xlsx", FileIconKind.Excel)]
    [InlineData("slides.pptx", FileIconKind.PowerPoint)]
    [InlineData("notes.txt", FileIconKind.PlainText)]
    [InlineData("setup.exe", FileIconKind.Executable)]
    [InlineData("installer.msi", FileIconKind.Executable)]
    [InlineData("export.csv", FileIconKind.Csv)]
    [InlineData("table.tsv", FileIconKind.Csv)]
    [InlineData("config.yaml", FileIconKind.Yaml)]
    [InlineData("config.yml", FileIconKind.Yaml)]
    [InlineData("module.py", FileIconKind.Python)]
    [InlineData("types.pyi", FileIconKind.Python)]
    [InlineData("native.c", FileIconKind.C)]
    [InlineData("native.h", FileIconKind.C)]
    [InlineData("engine.cpp", FileIconKind.Cpp)]
    [InlineData("engine.hxx", FileIconKind.Cpp)]
    [InlineData("Program.cs", FileIconKind.CSharp)]
    [InlineData("site.js", FileIconKind.JavaScript)]
    [InlineData("view.jsx", FileIconKind.JavaScript)]
    [InlineData("app.ts", FileIconKind.TypeScript)]
    [InlineData("view.tsx", FileIconKind.TypeScript)]
    [InlineData("Main.java", FileIconKind.Java)]
    [InlineData("index.html", FileIconKind.Html)]
    [InlineData("style.css", FileIconKind.Css)]
    [InlineData("build.ps1", FileIconKind.Shell)]
    [InlineData("schema.sql", FileIconKind.Sql)]
    [InlineData("main.go", FileIconKind.Go)]
    [InlineData("lib.rs", FileIconKind.Rust)]
    [InlineData("widget.swift", FileIconKind.Default)]
    [InlineData("photo.png", FileIconKind.Default)]
    [InlineData("unknown.custom", FileIconKind.Default)]
    [InlineData("unmapped.XYZ", FileIconKind.Default)]
    [InlineData(".json", FileIconKind.Default)]
    [InlineData("no-extension", FileIconKind.Default)]
    public void File_extensions_choose_expected_icon(string name, FileIconKind expected) =>
        Assert.Equal(expected, FileIconKinds.Classify(name, folder: false));

    [Fact]
    public void A_folder_never_uses_a_file_extension_icon() =>
        Assert.Equal(FileIconKind.Default, FileIconKinds.Classify("reports.xlsx", folder: true));
}
