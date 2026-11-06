using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public partial class ExplorerView : UserControl
{
    private LocalizationService? _texts;
    private ExplorerViewModel? _explorer;
    private bool _restoringSelection;
    private readonly DispatcherTimer _objectsGridReflowTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(140)
    };
    private int _iconAnchor;
    private int _iconCaret = -1;
    private double _iconTileWidth = 164;
    private IInputElement? _focusAfterBrowserHidden;
    private bool _restoreEmptyObjectsFocus;
    private readonly ContextMenu _gridFileMenu;
    private readonly ContextMenu _iconsFileMenu;
    private readonly ContextMenu _gridDirectoryMenu;
    private readonly ContextMenu _iconsDirectoryMenu;
    // Native shell drags do not guarantee a final WPF DragLeave/Drop (e.g. Esc or switching windows).
    private readonly DispatcherTimer _dropFeedbackWatchdog = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };

    public ExplorerView()
    {
        InitializeComponent();
        _gridFileMenu = ObjectsGrid.ContextMenu;
        _iconsFileMenu = ObjectsIcons.ContextMenu;
        _gridDirectoryMenu = (ContextMenu)FindResource("CurrentDirectoryMenu");
        _iconsDirectoryMenu = (ContextMenu)FindResource("CurrentDirectoryMenu");
        _marqueeTimer.Tick += Marquee_ScrollTick;
        _dropFeedbackWatchdog.Tick += DropFeedbackWatchdog_Tick;
        _objectsGridReflowTimer.Tick += ObjectsGridReflowTimer_Tick;
        DataContextChanged += (_, _) => { ClearDropFeedback(); HookLanguage(); };
        Loaded += (_, _) => { ClearDropFeedback(); HookLanguage(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearDropFeedback(); };
        Unloaded += (_, _) =>
        {
            _objectsGridReflowTimer.Stop();
            ClearDropFeedback();
            CancelMarquee();
            UnhookExplorer();
        };
    }

    private ExplorerViewModel? Explorer =>
        (DataContext as ExplorerWorkspace)?.Explorer;

    private void HookLanguage()
    {
        if (_texts is not null)
            _texts.PropertyChanged -= OnLanguageChanged;
        _texts = (DataContext as ExplorerWorkspace)?.Texts;
        if (_texts is not null)
            _texts.PropertyChanged += OnLanguageChanged;
        if (!ReferenceEquals(_explorer, Explorer))
        {
            UnhookExplorer();
            _explorer = Explorer;
            if (_explorer is not null)
            {
                _explorer.PropertyChanged += OnExplorerChanged;
                _explorer.SelectionSyncRequested += OnSelectionSyncRequested;
                _explorer.Rows.CollectionChanged += MarqueeRowsChanged;
                _explorer.Crumbs.CollectionChanged += OnCrumbsChanged;
                _explorer.Preview.DownloadRequested += OnPreviewDownload;
                _explorer.Preview.StateChanged += OnPreviewStateChanged;
                Dispatcher.BeginInvoke(ScrollCrumbsToEnd, DispatcherPriority.ContextIdle);
            }
        }
        ApplyHeaders();
        SyncSide();
        Explorer?.SetToolbarWidth(ToolbarRow.ActualWidth);
    }

    private void UnhookExplorer()
    {
        CancelMarquee();
        ClearDropFeedback();
        ClearPendingEmptyObjectsFocus();
        if (_explorer is null)
            return;
        _explorer.PropertyChanged -= OnExplorerChanged;
        _explorer.SelectionSyncRequested -= OnSelectionSyncRequested;
        _explorer.Rows.CollectionChanged -= MarqueeRowsChanged;
        _explorer.Crumbs.CollectionChanged -= OnCrumbsChanged;
        _explorer.Preview.DownloadRequested -= OnPreviewDownload;
        _explorer.Preview.StateChanged -= OnPreviewStateChanged;
        _explorer = null;
    }

    private void SetDropFeedback(bool accepted, string? prefix, ExplorerRow? folder)
    {
        if (!accepted || Explorer is not { } explorer)
        {
            ClearDropFeedback();
            return;
        }

        explorer.SetDropTarget(true, prefix, folder);
        _dropFeedbackWatchdog.Start();
    }

    private void ClearDropFeedback()
    {
        _dropFeedbackWatchdog.Stop();
        // Clear both contexts during a tab/view-model swap; neither may retain a stale hint.
        _explorer?.SetDropTarget(false);
        if (!ReferenceEquals(_explorer, Explorer))
            Explorer?.SetDropTarget(false);
    }

    private void DropFeedbackWatchdog_Tick(object? sender, EventArgs e)
    {
        // Unlike a time-only watchdog, checking the native mouse buttons and pointer does
        // not hide the hint while the user pauses over a valid folder during a drag.
        if (!IsLoaded || !IsVisible || _explorer?.IsDropTarget != true ||
            !NativeDragButtonPressed() || !GetCursorPos(out var cursor) ||
            !ExplorerDropTargetPolicy.Contains(
                FileBrowserPane.PointFromScreen(new Point(cursor.X, cursor.Y)),
                FileBrowserPane.ActualWidth, FileBrowserPane.ActualHeight))
            ClearDropFeedback();
    }

    private static bool NativeDragButtonPressed() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || // VK_LBUTTON
        (GetAsyncKeyState(0x02) & 0x8000) != 0;   // VK_RBUTTON

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void OnExplorerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExplorerViewModel.IsIconView))
        {
            CancelMarquee();
            Dispatcher.BeginInvoke(RestoreSelection);
        }
        if (e.PropertyName is nameof(ExplorerViewModel.ActiveSort) or nameof(ExplorerViewModel.SortDescending))
            SyncSortHeaders();
        if (e.PropertyName is nameof(ExplorerViewModel.IsCompactGrid))
            Dispatcher.BeginInvoke(UpdateIconTileWidth);
        if (e.PropertyName is nameof(ExplorerViewModel.ShowBucketRegion))
            UpdateBucketColumns();
        if (e.PropertyName is nameof(ExplorerViewModel.ShowSide) or nameof(ExplorerViewModel.SideExpanded)
            or nameof(ExplorerViewModel.DetailsPaneWidth))
        {
            SyncSide();
            // A pane transition changes the DataGrid viewport independently of
            // ordinary window resizing. Reflow only for this targeted change.
            ScheduleObjectsGridReflow();
        }

        if (e.PropertyName is nameof(ExplorerViewModel.IsBusy)
            or nameof(ExplorerViewModel.ShowEmptyObjects)
            or nameof(ExplorerViewModel.ShowObjectList)
            or nameof(ExplorerViewModel.ShowObjectIcons)
            or nameof(ExplorerViewModel.ShowError)
            or nameof(ExplorerViewModel.ShowEmptyBuckets)
            or nameof(ExplorerViewModel.ShowNoMatches)
            or nameof(ExplorerViewModel.ShowSearchPrompt)
            or nameof(ExplorerViewModel.ShowSearchPartialEmpty))
            TryRestoreEmptyObjectsFocus();
    }

    private void Root_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        // Only consider focus that was inside a browser list. Losing focus while a
        // list remains visible (e.g. Tab to the toolbar) is intentional navigation.
        FrameworkElement? browser = IsWithin(source, ObjectsGrid) ? ObjectsGrid
            : IsWithin(source, ObjectsIcons) ? ObjectsIcons
            : IsWithin(source, BucketsGrid) ? BucketsGrid
            : null;
        if (browser is null)
            return;

        var nextFocus = e.NewFocus;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || !IsVisible || browser.IsVisible
                || nextFocus is DependencyObject next && IsWithin(next, Root))
                return;

            _focusAfterBrowserHidden = nextFocus;
            _restoreEmptyObjectsFocus = true;
            TryRestoreEmptyObjectsFocus();
        }, DispatcherPriority.Input);
    }

    private void TryRestoreEmptyObjectsFocus()
    {
        if (!_restoreEmptyObjectsFocus || !IsLoaded || !IsVisible)
            return;

        if (Explorer is not { } explorer)
        {
            ClearPendingEmptyObjectsFocus();
            return;
        }

        // Navigation temporarily clears the rows before setting IsBusy. Do not
        // restore focus during that transient state; wait for the final view state.
        if (explorer.IsBusy)
            return;

        if (!explorer.ShowEmptyObjects)
        {
            if (explorer.ShowObjectList || explorer.ShowObjectIcons || explorer.ShowError
                || explorer.ShowEmptyBuckets || explorer.ShowNoMatches
                || explorer.ShowSearchPrompt || explorer.ShowSearchPartialEmpty
                || !explorer.ShowObjects)
                ClearPendingEmptyObjectsFocus();
            return;
        }

        var expectedFocus = _focusAfterBrowserHidden;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_restoreEmptyObjectsFocus)
                return;
            if (!IsLoaded || !IsVisible
                || Explorer is not { IsBusy: false, ShowEmptyObjects: true })
                return;

            // Do not steal focus if the user moved to another control while the
            // destination was loading. Restore only if focus has not changed.
            if (!ReferenceEquals(Keyboard.FocusedElement, expectedFocus))
            {
                ClearPendingEmptyObjectsFocus();
                return;
            }

            ClearPendingEmptyObjectsFocus();
            EmptyObjectsArea.Focus();
        }, DispatcherPriority.Input);
    }

    private void ClearPendingEmptyObjectsFocus()
    {
        _restoreEmptyObjectsFocus = false;
        _focusAfterBrowserHidden = null;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => ApplyHeaders();

    private void OnCrumbsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ScrollCrumbsToEnd, DispatcherPriority.ContextIdle);

    private void ScrollCrumbsToEnd()
    {
        if (CrumbScroll.ScrollableWidth > 0)
            CrumbScroll.ScrollToRightEnd();
    }

    private void CrumbScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CrumbScroll.ScrollableWidth <= 0)
            return;
        CrumbScroll.ScrollToHorizontalOffset(CrumbScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void CrumbScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            ScrollCrumbsToEnd();
    }

    private void ApplyHeaders()
    {
        if (_texts is null)
            return;
        SetHeader(BucketsGrid, 0, "Explorer_Name");
        SetHeader(BucketsGrid, 1, "Explorer_Region");
        SetHeader(ObjectsGrid, 0, "Explorer_Name");
        SetHeader(ObjectsGrid, 1, "Explorer_Size");
        SetHeader(ObjectsGrid, 2, "Explorer_Kind");
        SetHeader(ObjectsGrid, 3, "Explorer_Modified");
        UpdateBucketColumns();
        UpdateResponsiveColumns();
        SyncSortHeaders();
    }

    // The VM owns the active sort across navigation, tab recreation and view switches.
    private void SyncSortHeaders()
    {
        var active = Explorer?.ActiveSort;
        var descending = Explorer?.SortDescending ?? false;
        foreach (var column in ObjectsGrid.Columns)
            column.SortDirection = ExplorerSortDirection.DirectionFor(column.SortMemberPath, active, descending);
    }

    private void UpdateBucketColumns()
    {
        // Leave useful space for long bucket names and the inline favorite action.
        BucketsGrid.Columns[1].Visibility = Explorer?.ShowBucketRegion == true
            && BucketsGrid.ActualWidth >= 360
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BucketsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            UpdateBucketColumns();
    }

    private void ObjectsGrid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            ScheduleObjectsGridReflow();
    }

    private void ObjectsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;

        // WPF already lays out the grid as the window changes size.
        // Do not force a second layout pass during maximize/restore.
        UpdateResponsiveColumns();
    }

    private void ScheduleObjectsGridReflow()
    {
        // Window maximize/restore can raise many SizeChanged events. Wait until
        // the viewport has settled so the expensive DataGrid reflow runs once.
        _objectsGridReflowTimer.Stop();
        if (IsLoaded && ObjectsGrid.IsLoaded && ObjectsGrid.IsVisible)
            _objectsGridReflowTimer.Start();
    }

    private void ObjectsGridReflowTimer_Tick(object? sender, EventArgs e)
    {
        _objectsGridReflowTimer.Stop();
        if (!IsLoaded || !ObjectsGrid.IsLoaded || !ObjectsGrid.IsVisible)
            return;

        ExplorerDataGridViewport.Reflow(ObjectsGrid);
    }

    private void UpdateResponsiveColumns()
    {
        if (ObjectsGrid.ActualWidth <= 0)
            return;
        var columns = ExplorerColumnLayout.ForWidth(ObjectsGrid.ActualWidth);
        ObjectsGrid.Columns[1].Visibility = columns.Size ? Visibility.Visible : Visibility.Collapsed;
        ObjectsGrid.Columns[2].Visibility = columns.Kind ? Visibility.Visible : Visibility.Collapsed;
        ObjectsGrid.Columns[3].Visibility = columns.Modified ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetHeader(DataGrid grid, int index, string key)
    {
        if (index < grid.Columns.Count)
            grid.Columns[index].Header = _texts![key];
    }

    private void Objects_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || Explorer is not { IsIconView: false })
            return;
        if (!Explorer.RequestSelection(ObjectsGrid.SelectedItems.OfType<ExplorerRow>().ToList()))
            RestoreListedSelection();
    }

    private void Icons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || Explorer is not { IsIconView: true })
            return;
        if (ObjectsIcons.SelectedIndex < 0)
        {
            _iconAnchor = 0;
            _iconCaret = -1;
        }
        if (Keyboard.Modifiers != ModifierKeys.Shift && ObjectsIcons.SelectedIndex >= 0)
        {
            _iconAnchor = ObjectsIcons.SelectedIndex;
            _iconCaret = ObjectsIcons.SelectedIndex;
        }
        if (!Explorer.RequestSelection(ObjectsIcons.SelectedItems.OfType<ExplorerRow>().ToList()))
            RestoreListedSelection();
    }

    private void Objects_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Never open the previously selected object when double-clicking whitespace
        // or a header. The hit row must be the item that receives the action.
        if (e.ChangedButton != MouseButton.Left ||
            FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (FindRow(e.OriginalSource as DependencyObject) is { Item: ExplorerRow row })
        {
            _ = Explorer?.OpenEntryAsync(row);
            e.Handled = true;
        }
    }

    private void Buckets_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (FindRow(e.OriginalSource as DependencyObject) is { Item: BucketRow bucket })
        {
            Explorer?.OpenRowCommand.Execute(bucket);
            e.Handled = true;
        }
    }

    private void Objects_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var explorer = Explorer;
        if (explorer is null)
            return;
        var descending = ExplorerSortDirection.NextDescending(
            e.Column.SortMemberPath, e.Column.SortDirection);
        var sort = ExplorerSortDirection.SortFor(e.Column.SortMemberPath);
        if (sort is not null)
            explorer.ApplySort(sort.Value, descending);
    }

    private void Icons_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is { DataContext: ExplorerRow row, IsSelected: false })
            ObjectsIcons.SelectedItem = row;
    }

    private void Icons_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            UpdateIconTileWidth();
    }

    private void UpdateIconTileWidth()
    {
        if (ObjectsIcons is null)
            return;
        var available = Math.Max(0, ObjectsIcons.ActualWidth - 42);
        if (available <= 0)
            return;
        var compact = Explorer?.IsCompactGrid == true;
        var minimum = compact ? 112.0 : 152.0;
        var maximum = compact ? 136.0 : 176.0;
        var columns = Math.Max(1, (int)Math.Floor((available + 8) / (minimum + 8)));
        _iconTileWidth = Math.Min(maximum, Math.Max(48, available / columns - 8));
        ObjectsIcons.Tag = _iconTileWidth;
    }

    private void Icons_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ExplorerRow row)
            _ = Explorer?.OpenEntryAsync(row);
    }

    private void Icons_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var delta = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -IconColumns(),
            Key.Down => IconColumns(),
            _ => 0
        };
        if (delta == 0 || ObjectsIcons.Items.Count == 0)
            return;

        var current = _iconCaret < 0 ? Math.Max(0, ObjectsIcons.SelectedIndex) : _iconCaret;
        var next = Math.Clamp(current + delta, 0, ObjectsIcons.Items.Count - 1);
        _iconCaret = next;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control)
        {
            var focused = ObjectsIcons.Items[next];
            ObjectsIcons.ScrollIntoView(focused);
            (ObjectsIcons.ItemContainerGenerator.ContainerFromItem(focused) as ListBoxItem)?.Focus();
            e.Handled = true;
            return;
        }
        if (!modifiers.HasFlag(ModifierKeys.Shift))
            _iconAnchor = next;

        var start = Math.Min(_iconAnchor, next);
        var end = Math.Max(_iconAnchor, next);
        _restoringSelection = true;
        try
        {
            if (!modifiers.HasFlag(ModifierKeys.Control) || !modifiers.HasFlag(ModifierKeys.Shift))
                ObjectsIcons.SelectedItems.Clear();
            var selected = ObjectsIcons.SelectedItems.Cast<object>().ToHashSet();
            for (var index = start; index <= end; index++)
                if (selected.Add(ObjectsIcons.Items[index]))
                    ObjectsIcons.SelectedItems.Add(ObjectsIcons.Items[index]);
        }
        finally
        {
            _restoringSelection = false;
        }

        if (Explorer?.RequestSelection(ObjectsIcons.SelectedItems.OfType<ExplorerRow>().ToList()) == false)
            RestoreListedSelection();
        var focusedItem = ObjectsIcons.Items[next];
        ObjectsIcons.ScrollIntoView(focusedItem);
        (ObjectsIcons.ItemContainerGenerator.ContainerFromItem(focusedItem) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    private void RestoreListedSelection()
    {
        var explorer = Explorer;
        if (explorer is null)
            return;
        _restoringSelection = true;
        try
        {
            if (explorer.IsIconView)
                SelectRows(ObjectsIcons.SelectedItems, explorer.SelectedRows);
            else
                SelectRows(ObjectsGrid.SelectedItems, explorer.SelectedRows);
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private void OnPreviewStateChanged(object? sender, EventArgs e) => SyncSide();

    private void OnSelectionSyncRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            RestoreListedSelection();
            if (Explorer?.IsIconView == true && ObjectsIcons.SelectedItem is not null)
                ObjectsIcons.ScrollIntoView(ObjectsIcons.SelectedItem);
            else if (ObjectsGrid.SelectedItem is not null)
                ObjectsGrid.ScrollIntoView(ObjectsGrid.SelectedItem);
        }, DispatcherPriority.Input);

    private void OnPreviewDownload(object? sender, EventArgs e) => DownloadToFolder(preview: true);

    private void SyncSide()
    {
        if (SideColumn is null || SideSplitter is null)
            return;
        var explorer = Explorer;
        var expanded = explorer?.ShowSide == true && explorer.Preview.IsExpanded && explorer.Preview.ShowingContent;
        BrowserArea.ColumnDefinitions[0].Width = expanded ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FileBrowserPane.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        if (explorer?.ShowSide != true)
        {
            SideColumn.Width = new GridLength(0);
            SideColumn.MinWidth = 0;
            SideColumn.MaxWidth = double.PositiveInfinity;
            SideSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        if (expanded)
        {
            SideSplitter.Visibility = Visibility.Collapsed;
            SideColumn.MinWidth = 0;
            SideColumn.MaxWidth = double.PositiveInfinity;
            SideColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        SideSplitter.Visibility = Visibility.Visible;
        // Keep a useful file-list viewport when the details pane is open.
        // The splitter itself sits between the two columns, so reserve a small
        // safety margin beyond the minimum list width.
        const double minimumFileListWidth = 560;
        const double splitterSafetyWidth = 8;
        SideColumn.MinWidth = 280;
        var widthAvailableForDetails = BrowserArea.ActualWidth
            - minimumFileListWidth - splitterSafetyWidth;
        SideColumn.MaxWidth = Math.Max(
            SideColumn.MinWidth,
            Math.Min(720, Math.Min(BrowserArea.ActualWidth * 0.45, widthAvailableForDetails)));
        var desired = Math.Min(SideColumn.MaxWidth, explorer.DetailsPaneWidth);
        if (SideColumn.Width.GridUnitType != GridUnitType.Pixel || Math.Abs(SideColumn.Width.Value - desired) > 1)
            SideColumn.Width = new GridLength(desired);
    }

    private void BrowserArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            SyncSide();
    }

    private void SideSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Explorer?.SetDetailsPaneWidth(SideColumn.ActualWidth);
    }

    private void RestoreSelection()
    {
        var explorer = Explorer;
        if (explorer is null)
            return;

        _restoringSelection = true;
        try
        {
            if (explorer.IsIconView)
            {
                SelectRows(ObjectsIcons.SelectedItems, explorer.SelectedRows);
                ObjectsIcons.Focus();
                if (ObjectsIcons.SelectedItem is not null)
                    ObjectsIcons.ScrollIntoView(ObjectsIcons.SelectedItem);
            }
            else
            {
                SelectRows(ObjectsGrid.SelectedItems, explorer.SelectedRows);
                ObjectsGrid.Focus();
                if (ObjectsGrid.SelectedItem is not null)
                    ObjectsGrid.ScrollIntoView(ObjectsGrid.SelectedItem);
            }
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private static void SelectRows(System.Collections.IList selected, IReadOnlyList<ExplorerRow> rows)
    {
        selected.Clear();
        foreach (var row in rows)
            selected.Add(row);
    }

    private int IconColumns()
    {
        var width = Math.Max(0, ObjectsIcons.ActualWidth - 42);
        return Math.Max(1, (int)Math.Floor((width + 8) / (_iconTileWidth + 8)));
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null && source is not T)
            source = VisualTreeHelper.GetParent(source);
        return source as T;
    }

    private static DataGridRow? FindRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow)
            source = VisualTreeHelper.GetParent(source);
        return source as DataGridRow;
    }
}

internal enum ExplorerContextTargetKind { None, Item, Directory }

internal readonly record struct ExplorerContextTarget(ExplorerContextTargetKind Kind, ExplorerRow? Row);
