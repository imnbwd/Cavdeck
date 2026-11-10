using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

/// <summary>
/// Attached behavior for virtualized image presenters. Keeps loading/cancellation out of
/// reusable DataTemplates and works with both DataGrid and ListBox containers.
/// </summary>
public static class ThumbnailLoader
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(ThumbnailLoader),
            new PropertyMetadata(false, OnEnabledChanged));

    private static readonly ConditionalWeakTable<Image, State> States = new();
    private static readonly ConditionalWeakTable<ExplorerRow, RowCounter> Counters = new();

    private sealed class State
    {
        public Request? Active;
    }

    private sealed class RowCounter
    {
        public int VisiblePresenters;
    }

    private sealed class Request(ExplorerViewModel explorer, ExplorerRow row, ObjectEntry entry)
    {
        public ExplorerViewModel Explorer { get; } = explorer;
        public ExplorerRow Row { get; } = row;
        public ObjectEntry Entry { get; } = entry;
        public CancellationTokenSource? Cancellation { get; set; }
    }

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool enabled) =>
        element.SetValue(EnabledProperty, enabled);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Image image)
            return;

        if (e.NewValue is true)
        {
            image.Loaded += OnLoaded;
            image.Unloaded += OnUnloaded;
            image.IsVisibleChanged += OnVisibilityChanged;
            image.DataContextChanged += OnDataContextChanged;
            Activate(image);
        }
        else
        {
            image.Loaded -= OnLoaded;
            image.Unloaded -= OnUnloaded;
            image.IsVisibleChanged -= OnVisibilityChanged;
            image.DataContextChanged -= OnDataContextChanged;
            Release(image);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Activate((Image)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Release((Image)sender);

    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Activate((Image)sender);
        else
            Release((Image)sender);
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var image = (Image)sender;
        Release(image);
        Activate(image);
    }

    private static void Activate(Image image)
    {
        if (!image.IsLoaded || !image.IsVisible
            || image.DataContext is not ExplorerRow { ThumbnailCandidate: true } row
            || FindExplorer(image) is not { } explorer)
            return;

        var state = States.GetOrCreateValue(image);
        if (state.Active is { } previous)
        {
            if (ReferenceEquals(previous.Explorer, explorer)
                && ReferenceEquals(previous.Row, row)
                && Equals(previous.Entry, row.Entry))
                return;
            Release(image);
        }

        var request = new Request(explorer, row, row.Entry);
        state.Active = request;
        Counters.GetOrCreateValue(row).VisiblePresenters++;
        if (row.HasThumbnail)
            return;

        var cancellation = new CancellationTokenSource();
        request.Cancellation = cancellation;
        _ = LoadAsync(image, state, request, cancellation);
    }

    private static async Task LoadAsync(
        Image image, State state, Request request, CancellationTokenSource cancellation)
    {
        try
        {
            var bitmap = await request.Explorer.LoadThumbnailAsync(request.Row, cancellation.Token);
            if (bitmap is null || cancellation.IsCancellationRequested
                || !ReferenceEquals(state.Active, request)
                || !ReferenceEquals(FindExplorer(image), request.Explorer)
                || !ReferenceEquals(image.DataContext, request.Row)
                || !Equals(request.Row.Entry, request.Entry)
                || !image.IsVisible)
                return;

            request.Row.SetThumbnail(bitmap);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A thumbnail must never block list rendering or file operations.
        }
        finally
        {
            if (ReferenceEquals(request.Cancellation, cancellation))
            {
                request.Cancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private static ExplorerViewModel? FindExplorer(DependencyObject child)
    {
        DependencyObject? node = child;
        while (node is not null)
        {
            if (node is ItemsControl items && items.DataContext is ExplorerWorkspace workspace)
                return workspace.Explorer;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static void Release(Image image)
    {
        if (!States.TryGetValue(image, out var state) || state.Active is not { } request)
            return;

        state.Active = null;
        var cancellation = request.Cancellation;
        request.Cancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        var counter = Counters.GetOrCreateValue(request.Row);
        counter.VisiblePresenters = Math.Max(0, counter.VisiblePresenters - 1);
        if (counter.VisiblePresenters == 0)
            request.Row.SetThumbnail(null);
    }

    /// <summary>Immediately releases realized thumbnails on tab hide or context replacement.</summary>
    public static void CancelVisible(DependencyObject root)
    {
        if (root is Image image && GetEnabled(image))
            Release(image);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
            CancelVisible(VisualTreeHelper.GetChild(root, index));
    }
}
