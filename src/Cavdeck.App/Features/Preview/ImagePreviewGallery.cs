namespace Cavdeck.App.Features.Preview;

public sealed record ImagePreviewItem(string Key, string Name);

/// <summary>
/// An immutable snapshot of images in Explorer display order. Navigation never changes
/// the Explorer selection or relies on a mutable remote listing.
/// </summary>
public sealed class ImagePreviewGallery
{
    private readonly ImagePreviewItem[] _items;
    private int _index;

    public ImagePreviewGallery(IEnumerable<ImagePreviewItem> items, string initialKey)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialKey);

        _items = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .DistinctBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        _index = Array.FindIndex(_items, item =>
            string.Equals(item.Key, initialKey, StringComparison.Ordinal));
        if (_index < 0)
            throw new ArgumentException("The selected image must be part of the gallery.", nameof(initialKey));
    }

    public ImagePreviewItem Current => _items[_index];
    public int Count => _items.Length;
    public int Position => _index + 1;
    public bool CanPrevious => _index > 0;
    public bool CanNext => _index < _items.Length - 1;

    public bool Move(int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));

        var next = _index + direction;
        if (next < 0 || next >= _items.Length)
            return false;
        _index = next;
        return true;
    }
}
