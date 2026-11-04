namespace Cavdeck.App.Features.Explorer;

/// <summary>Visible columns chosen from actual file-list width, not window width.</summary>
public readonly record struct ExplorerVisibleColumns(bool Size, bool Kind, bool Modified);

public static class ExplorerColumnLayout
{
    public static ExplorerVisibleColumns ForWidth(double width)
    {
        // The name column always wins; never make the user horizontally scroll to find it.
        if (!double.IsFinite(width) || width <= 0)
            return new ExplorerVisibleColumns(false, false, false);
        return new ExplorerVisibleColumns(width >= 360, width >= 760, width >= 520);
    }
}
