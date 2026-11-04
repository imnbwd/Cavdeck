using System.Windows;

namespace Cavdeck.App.Features.Explorer;

// Selection is calculated from logical item indices, not generated WPF containers:
// virtualization must never cause off-screen objects to be omitted.
public static class MarqueeSelectionGeometry
{
    public const double TileGap = 8;

    public static Rect Between(Point first, Point last) => new(
        Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
        Math.Max(1, Math.Abs(last.X - first.X)), Math.Max(1, Math.Abs(last.Y - first.Y)));

    public static IReadOnlyList<int> HitRows(Rect area, int itemCount, double rowHeight, double contentWidth)
    {
        if (itemCount <= 0 || rowHeight <= 0 || area.Right <= 0 ||
            area.Left >= contentWidth || area.Bottom <= 0)
            return [];

        var first = Math.Max(0, (int)Math.Floor(Math.Max(0, area.Top) / rowHeight));
        var last = Math.Min(itemCount - 1, (int)Math.Ceiling(area.Bottom / rowHeight) - 1);
        if (last < first)
            return [];
        var result = new List<int>(last - first + 1);
        for (var index = first; index <= last; index++)
            result.Add(index);
        return result;
    }

    public static IReadOnlyList<int> HitTiles(
        Rect area, int itemCount, int columns, double width, double height)
    {
        if (itemCount <= 0 || columns <= 0 || width <= 0 || height <= 0 ||
            area.Right <= 0 || area.Bottom <= 0)
            return [];

        var pitchX = width + TileGap;
        var pitchY = height + TileGap;
        var firstRow = Math.Max(0, (int)Math.Floor(Math.Max(0, area.Top) / pitchY));
        var lastRow = Math.Min((itemCount - 1) / columns,
            (int)Math.Ceiling(area.Bottom / pitchY) - 1);
        var firstColumn = Math.Max(0, (int)Math.Floor(Math.Max(0, area.Left) / pitchX));
        var lastColumn = Math.Min(columns - 1, (int)Math.Ceiling(area.Right / pitchX) - 1);
        var result = new List<int>();
        for (var row = firstRow; row <= lastRow; row++)
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            var index = row * columns + column;
            if (index >= itemCount)
                break;
            if (area.IntersectsWith(new Rect(column * pitchX, row * pitchY, width, height)))
                result.Add(index);
        }
        return result;
    }
}
