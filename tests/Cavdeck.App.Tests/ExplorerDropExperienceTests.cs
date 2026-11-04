using System.Windows;
using System.Xml.Linq;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerDropExperienceTests
{
    [Fact]
    public void Native_drop_is_scoped_to_browser_pane_with_preview_handlers()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var root = view.Root!;
        Assert.Null(root.Attribute("AllowDrop"));
        var pane = view.Descendants(wpf + "Grid").Single(x =>
            (string?)x.Attribute(xaml + "Name") == "FileBrowserPane");
        Assert.Equal("True", (string?)pane.Attribute("AllowDrop"));
        Assert.Equal("Browser_PreviewDragOver", (string?)pane.Attribute("PreviewDragEnter"));
        Assert.Equal("Browser_PreviewDragOver", (string?)pane.Attribute("PreviewDragOver"));
        Assert.Equal("Browser_PreviewDragLeave", (string?)pane.Attribute("PreviewDragLeave"));
        Assert.Equal("Browser_PreviewDrop", (string?)pane.Attribute("PreviewDrop"));
        Assert.Contains(view.Descendants(wpf + "TextBlock"),
            x => ((string?)x.Attribute("Text"))?.Contains("Explorer.DropDestination") == true);
        // Both the outline and destination hint must be absent until a drag is accepted.
        Assert.Equal(2, view.Descendants(wpf + "Border").Count(x =>
            ((string?)x.Attribute("Visibility"))?.Contains("Explorer.IsDropTarget") == true));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(200, 100, true)]
    [InlineData(-1, 20, false)]
    [InlineData(20, -1, false)]
    [InlineData(300, 20, false)]
    [InlineData(20, 200, false)]
    public void Native_drop_feedback_bounds_are_exclusive_at_far_edges(
        double x, double y, bool expected)
    {
        Assert.Equal(expected, ExplorerDropTargetPolicy.Contains(
            new Point(x, y), 300, 200));
    }

    [Fact]
    public void Drop_feedback_requires_a_measurable_browser_pane()
    {
        Assert.False(ExplorerDropTargetPolicy.Contains(new Point(0, 0), 0, 200));
        Assert.False(ExplorerDropTargetPolicy.Contains(new Point(0, 0), 300, 0));
        Assert.False(ExplorerDropTargetPolicy.Contains(new Point(double.NaN, 1), 300, 200));
    }

    [Theory]
    [InlineData(true, true, DragDropEffects.Copy, "docs/", true)]
    [InlineData(false, true, DragDropEffects.Copy, "docs/", false)]
    [InlineData(true, false, DragDropEffects.Copy, "docs/", false)]
    [InlineData(true, true, DragDropEffects.Move, "docs/", false)]
    [InlineData(true, true, DragDropEffects.Copy, null, false)]
    public void Only_valid_copy_drops_are_accepted(bool canWrite, bool files,
        DragDropEffects effects, string? prefix, bool expected)
    {
        Assert.Equal(expected, ExplorerDropTargetPolicy.Accepts(canWrite, files, effects, prefix));
    }

    [Fact]
    public void Row_drop_targets_resolve_folder_and_files_correctly()
    {
        var texts = new LocalizationService();
        var folder = new ExplorerRow(
            new ObjectEntry(ObjectEntryKind.Folder, "reports", "docs/reports/", 0, null, null), texts);
        var file = new ExplorerRow(
            new ObjectEntry(ObjectEntryKind.File, "report.pdf", "docs/report.pdf", 3, null, null), texts);
        Assert.Equal("docs/reports/", ExplorerDropTargetPolicy.Destination(
            new ExplorerContextTarget(ExplorerContextTargetKind.Item, folder), "docs/"));
        Assert.Equal("docs/", ExplorerDropTargetPolicy.Destination(
            new ExplorerContextTarget(ExplorerContextTargetKind.Item, file), "docs/"));
        Assert.Equal("docs/", ExplorerDropTargetPolicy.Destination(
            new ExplorerContextTarget(ExplorerContextTargetKind.Directory, null), "docs/"));
        Assert.Null(ExplorerDropTargetPolicy.Destination(default, "docs/"));
    }

    [Fact]
    public void Drop_highlight_can_be_cleared_without_affecting_selection()
    {
        var row = new ExplorerRow(
            new ObjectEntry(ObjectEntryKind.Folder, "reports", "docs/reports/", 0, null, null),
            new LocalizationService());
        row.SetDropTarget(true);
        Assert.True(row.IsDropTarget);
        row.SetDropTarget(false);
        Assert.False(row.IsDropTarget);
    }
}
