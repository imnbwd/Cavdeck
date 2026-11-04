using System.Windows;

namespace Cavdeck.App.Features.Explorer;

// One copy-only policy for the list, icon and empty-directory views.
internal static class ExplorerDropTargetPolicy
{
    public static string? Destination(ExplorerContextTarget target, string currentPrefix) =>
        target.Kind switch
        {
            ExplorerContextTargetKind.Directory => currentPrefix,
            ExplorerContextTargetKind.Item when target.Row is { IsFolder: true } folder => folder.Entry.Key,
            ExplorerContextTargetKind.Item => currentPrefix,
            _ => null
        };

    // A shared hit-test for DragLeave and the native pointer watchdog (DIPs, not pixels).
    public static bool Contains(Point point, double width, double height) =>
        width > 0 && height > 0 && point.X >= 0 && point.Y >= 0
        && point.X < width && point.Y < height;

    public static bool Accepts(bool canWriteToCurrentDirectory, bool hasFiles,
        DragDropEffects allowedEffects, string? destination) =>
        canWriteToCurrentDirectory && hasFiles && destination is not null
        && (allowedEffects & DragDropEffects.Copy) != 0;
}
