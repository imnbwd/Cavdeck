using System.Windows;
using System.Windows.Controls;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public partial class ExplorerView
{
    // These entries exist only while a thumbnail visual is realized and visible.
    // They are released on virtualization, view switching and tab deactivation.
    private readonly Dictionary<Image, ThumbnailHandle> _thumbnailHandles = new();

    private sealed class ThumbnailHandle(
        ExplorerViewModel explorer, ExplorerRow row, ObjectEntry entry)
    {
        public ExplorerViewModel Explorer { get; } = explorer;
        public ExplorerRow Row { get; } = row;
        public ObjectEntry Entry { get; } = entry;
        public CancellationTokenSource? Load { get; set; }
    }

    private void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
            ActivateThumbnail(image);
    }

    private void Thumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
            ReleaseThumbnail(image);
    }

    private void Thumbnail_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Image image)
            return;
        if (e.NewValue is true)
            ActivateThumbnail(image);
        else
            ReleaseThumbnail(image);
    }

    private void Thumbnail_ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Image image)
            return;

        ReleaseThumbnail(image);
        ActivateThumbnail(image);
    }

    private void ActivateThumbnail(Image image)
    {
        if (!IsLoaded || !IsVisible || !image.IsLoaded || !image.IsVisible
            || image.DataContext is not ExplorerRow { ThumbnailCandidate: true } row
            || Explorer is not { } explorer)
            return;

        if (_thumbnailHandles.TryGetValue(image, out var existing))
        {
            if (ReferenceEquals(existing.Row, row) && ReferenceEquals(existing.Explorer, explorer)
                && Equals(existing.Entry, row.Entry))
                return;
            ReleaseThumbnail(image);
        }

        var handle = new ThumbnailHandle(explorer, row, row.Entry);
        _thumbnailHandles[image] = handle;

        if (row.HasThumbnail)
            return;

        var cancellation = new CancellationTokenSource();
        handle.Load = cancellation;
        _ = FetchThumbnailAsync(image, handle, cancellation);
    }

    private async Task FetchThumbnailAsync(
        Image image, ThumbnailHandle handle, CancellationTokenSource cancellation)
    {
        try
        {
            var thumbnail = await handle.Explorer.LoadThumbnailAsync(handle.Row, cancellation.Token);
            if (thumbnail is null || cancellation.IsCancellationRequested
                || !_thumbnailHandles.TryGetValue(image, out var active)
                || !ReferenceEquals(active, handle)
                || !ReferenceEquals(Explorer, handle.Explorer)
                || !ReferenceEquals(image.DataContext, handle.Row)
                || !Equals(handle.Row.Entry, handle.Entry)
                || !image.IsVisible)
                return;

            handle.Row.SetThumbnail(thumbnail);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(handle.Load, cancellation))
            {
                handle.Load = null;
                cancellation.Dispose();
            }
        }
    }

    private void ReleaseThumbnail(Image image)
    {
        if (!_thumbnailHandles.Remove(image, out var handle))
            return;

        var cancellation = handle.Load;
        handle.Load = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        // An image shared by two templates should not disappear until its last
        // realized presenter is gone. Otherwise rows retain only cached thumbnails.
        if (!_thumbnailHandles.Values.Any(active => ReferenceEquals(active.Row, handle.Row)))
            handle.Row.SetThumbnail(null);
    }

    private void CancelAllThumbnailRequests()
    {
        foreach (var image in _thumbnailHandles.Keys.ToArray())
            ReleaseThumbnail(image);
    }
}
