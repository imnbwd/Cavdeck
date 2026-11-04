using Cavdeck.Application.Transfers;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class UploadCatalogClipboardTests
{
    [Fact]
    public void Mixed_paths_keep_folder_structure_and_empty_folder_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-upload-test-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(root);
            var folder = Directory.CreateDirectory(Path.Combine(root, "folder"));
            Directory.CreateDirectory(Path.Combine(folder.FullName, "empty"));
            File.WriteAllText(Path.Combine(folder.FullName, "inside.txt"), "inside");
            var standalone = Path.Combine(root, "outside.txt");
            File.WriteAllText(standalone, "outside");

            var sources = UploadCatalog.Expand("projects/", [folder.FullName, standalone]);

            Assert.Contains(sources, source => source.Key == "projects/folder/inside.txt" && source.Error is null);
            Assert.Contains(sources, source => source.Key == "projects/folder/empty/" && source.FolderMarker);
            Assert.Contains(sources, source => source.Key == "projects/outside.txt" && source.Error is null);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Missing_source_becomes_a_failed_upload_source()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cavdeck-missing-" + Guid.NewGuid());
        var source = Assert.Single(UploadCatalog.Expand("projects/", [missing]));
        Assert.Equal("Missing", source.Error);
    }
}
