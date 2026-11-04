using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class ObjectFileKindTests
{
    [Theory]
    [InlineData("photos", true, ObjectFileKind.Folder)]
    [InlineData("logo.PNG", false, ObjectFileKind.Image)]
    [InlineData("clip.mp4", false, ObjectFileKind.Video)]
    [InlineData("track.flac", false, ObjectFileKind.Audio)]
    [InlineData("App.cs", false, ObjectFileKind.Code)]
    [InlineData("types.pyi", false, ObjectFileKind.Code)]
    [InlineData("native.cxx", false, ObjectFileKind.Code)]
    [InlineData("module.mts", false, ObjectFileKind.Code)]
    [InlineData("notes.json", false, ObjectFileKind.Data)]
    [InlineData("spec.xsd", false, ObjectFileKind.Data)]
    [InlineData("transform.xsl", false, ObjectFileKind.Data)]
    [InlineData("transform.xslt", false, ObjectFileKind.Data)]
    [InlineData("MainWindow.xaml", false, ObjectFileKind.Data)]
    [InlineData("Strings.resx", false, ObjectFileKind.Data)]
    [InlineData("vector.svg", false, ObjectFileKind.Image)]
    [InlineData("readme.md", false, ObjectFileKind.Text)]
    [InlineData("brief.docx", false, ObjectFileKind.Document)]
    [InlineData("spec.pdf", false, ObjectFileKind.Pdf)]
    [InlineData("build.tar.gz", false, ObjectFileKind.Archive)]
    [InlineData("sheet.xlsx", false, ObjectFileKind.Spreadsheet)]
    [InlineData("deck.pptx", false, ObjectFileKind.Presentation)]
    [InlineData("Inter.woff2", false, ObjectFileKind.Font)]
    [InlineData("setup.exe", false, ObjectFileKind.Program)]
    [InlineData("Dockerfile", false, ObjectFileKind.Code)]
    [InlineData(".gitignore", false, ObjectFileKind.Data)]
    [InlineData("blob", false, ObjectFileKind.File)]
    [InlineData("unmapped.unknown", false, ObjectFileKind.File)]
    [InlineData("", false, ObjectFileKind.File)]
    public void Classify_maps_common_names(string name, bool folder, ObjectFileKind expected) =>
        Assert.Equal(expected, ObjectFileKinds.Classify(name, folder));
}
