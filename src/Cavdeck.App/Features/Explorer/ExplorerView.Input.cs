using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public partial class ExplorerView
{
    private void ObjectsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A file list is one Tab stop, not a spreadsheet of individually tabbable cells.
        // Leave Ctrl+Tab alone so the shell can still switch workspace tabs.
        if (e.Key != Key.Tab ||
            Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift))
            return;

        var direction = Keyboard.Modifiers == ModifierKeys.Shift
            ? FocusNavigationDirection.Previous
            : FocusNavigationDirection.Next;

        // With TabNavigation=Once, starting at the focused cell moves to the next
        // control outside the list while retaining DataGrid's arrow-key selection.
        if (Keyboard.FocusedElement is not UIElement focused ||
            !focused.MoveFocus(new TraversalRequest(direction)))
            ObjectsGrid.MoveFocus(new TraversalRequest(direction));

        e.Handled = true;
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var explorer = Explorer;
        if (explorer is null)
            return;
        if (e.Key == Key.Escape && _marqueeCandidate)
        {
            CancelMarquee();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && explorer.Preview.IsExpanded)
        {
            explorer.Preview.ToggleExpandedCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusToolbarSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && explorer.Preview.CanSave)
        {
            explorer.Preview.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (IsEditingFocus())
            return;

        if (e.Key == Key.F5 && explorer.CanRefresh)
        {
            explorer.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && explorer.CanDelete)
        {
            explorer.DeleteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && explorer.CanRename)
        {
            explorer.RenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && explorer.CanGoUp)
        {
            explorer.UpCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && explorer.ShowObjects && explorer.SelectedRows.Count == 1
                 && (ObjectsGrid.IsKeyboardFocusWithin || ObjectsIcons.IsKeyboardFocusWithin))
        {
            explorer.OpenRowCommand.Execute(explorer.SelectedRows[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && explorer.ShowBuckets && BucketsGrid.IsKeyboardFocusWithin
                 && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is null
                 && BucketsGrid.SelectedItem is BucketRow bucket)
        {
            explorer.OpenRowCommand.Execute(bucket);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && explorer.CanCopy)
        {
            explorer.CopyCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control && explorer.CanCut)
        {
            explorer.CutCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && explorer.CanPaste)
        {
            explorer.PasteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsEditingFocus()
    {
        var node = Keyboard.FocusedElement as DependencyObject;
        while (node is not null)
        {
            if (node is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }
                || node.GetType().FullName?.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal) == true)
                return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : null;
        }
        return false;
    }

    private void Root_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        Explorer?.RefreshPasteAvailability();

    private void Root_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Explorer?.RefreshPasteAvailability();

    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Explorer is { } explorer)
        {
            if (explorer.HasFilter)
                explorer.ClearSearchCommand.Execute(null);
            else if (explorer.ToolbarShowCloseSearchButton)
                explorer.CloseToolbarSearch();
            else
                return;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || sender is not TextBox input || Explorer is null)
            return;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Explorer.CanRunSearch)
        {
            Explorer.RunSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ToolbarRow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Explorer?.SetToolbarWidth(e.NewSize.Width);

    private void ToolbarSearch_Click(object sender, RoutedEventArgs e)
    {
        if (Explorer is not { } explorer)
            return;
        explorer.ToggleToolbarSearch();
        if (explorer.ToolbarShowSearchInput)
            FocusToolbarSearch();
    }

    private void FocusToolbarSearch()
    {
        if (Explorer is { ToolbarShowSearchInput: false } explorer)
            explorer.ToggleToolbarSearch();
        Dispatcher.BeginInvoke(() =>
        {
            SearchInput.Focus();
            SearchInput.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void UploadOptions_Click(object sender, RoutedEventArgs e) => OpenMenu(UploadOptionsButton, UploadSplitButton);

    private void More_Click(object sender, RoutedEventArgs e) => OpenMenu(MoreButton);

    private void New_Click(object sender, RoutedEventArgs e) => OpenMenu(NewButton);

    private void SelectionMore_Click(object sender, RoutedEventArgs e) => OpenMenu(SelectionMoreButton);

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (Explorer?.CanSelectAll != true)
            return;
        if (Explorer.IsIconView)
        {
            ObjectsIcons.Focus();
            ObjectsIcons.SelectAll();
        }
        else
        {
            ObjectsGrid.Focus();
            ObjectsGrid.SelectAll();
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (Explorer?.IsIconView == true)
            ObjectsIcons.UnselectAll();
        else
            ObjectsGrid.UnselectAll();
    }

    private void SearchScope_Click(object sender, RoutedEventArgs e) => OpenMenu(SearchScopeButton);

    private void OpenMenu(Button button, FrameworkElement? anchor = null)
    {
        Explorer?.RefreshPasteAvailability();
        if (button.ContextMenu is not { } menu)
            return;
        // For the upload split button, align with the whole control, not just its chevron.
        menu.PlacementTarget = anchor ?? button;
        menu.IsOpen = true;
    }

    private void SearchScopeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var index) && Explorer is { } explorer)
            explorer.SearchScopeIndex = index;
        SearchInput.Focus();
    }

    private void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = (DataContext as ExplorerWorkspace)?.Texts["Transfer_PickFiles"]
        };
        if (dialog.ShowDialog() == true && Explorer is { } explorer)
            explorer.RunUiOperation(() => explorer.QueueUploadsAsync(dialog.FileNames));
    }

    private void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = (DataContext as ExplorerWorkspace)?.Texts["Transfer_PickFolder"]
        };
        if (dialog.ShowDialog() == true && Explorer is { } explorer)
            explorer.RunUiOperation(() => explorer.QueueUploadsAsync([dialog.FolderName]));
    }

    private void Download_Click(object sender, RoutedEventArgs e) => DownloadToFolder(preview: false);

    private void DownloadToFolder(bool preview)
    {
        var explorer = Explorer;
        if (explorer is null) return;
        if (explorer.AutomaticDownloadDirectory is { } automatic)
        {
            explorer.RunUiOperation(() => preview ? explorer.DownloadOpenObjectAsync(automatic)
                : explorer.QueueDownloadsAsync(automatic));
            return;
        }
        // An unavailable saved folder falls back to asking the user.
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = (DataContext as ExplorerWorkspace)?.Texts["Transfer_PickDestination"]
        };
        if (explorer.PreferredDownloadDirectory is { } preferred)
            dialog.InitialDirectory = preferred;
        if (dialog.ShowDialog() == true)
            explorer.RunUiOperation(() => preview ? explorer.DownloadOpenObjectAsync(dialog.FolderName)
                : explorer.QueueDownloadsAsync(dialog.FolderName));
    }

    // Preview handlers run before DataGrid/ListBox children can consume native shell drops.
    // The toolbar, search results, bucket list and side panel are not upload targets.
    private void Browser_PreviewDragOver(object sender, DragEventArgs e)
    {
        var explorer = Explorer;
        var target = ResolveBrowserDropTarget(e);
        var prefix = ExplorerDropTargetPolicy.Destination(target, explorer?.Place?.Prefix ?? "");
        var accepted = ExplorerDropTargetPolicy.Accepts(explorer?.CanWriteToCurrentDirectory == true,
            e.Data.GetDataPresent(DataFormats.FileDrop), e.AllowedEffects, prefix);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropFeedback(accepted, prefix, accepted && target.Row?.IsFolder == true ? target.Row : null);
        e.Handled = true;
    }

    private void Browser_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // Keep the hint when moving between children; true departures clear it immediately.
        // The watchdog also handles an Esc/cancel or DragLeave that Windows never delivers.
        if (!ExplorerDropTargetPolicy.Contains(e.GetPosition(FileBrowserPane),
                FileBrowserPane.ActualWidth, FileBrowserPane.ActualHeight))
            ClearDropFeedback();
    }

    private void Browser_PreviewDrop(object sender, DragEventArgs e)
    {
        var explorer = Explorer;
        var target = ResolveBrowserDropTarget(e);
        var prefix = ExplorerDropTargetPolicy.Destination(target, explorer?.Place?.Prefix ?? "");
        ClearDropFeedback();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!ExplorerDropTargetPolicy.Accepts(explorer?.CanWriteToCurrentDirectory == true,
                e.Data.GetDataPresent(DataFormats.FileDrop), e.AllowedEffects, prefix)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;

        e.Effects = DragDropEffects.Copy; // The local originals must remain untouched.
        explorer!.RunUiOperation(() => explorer.QueueDroppedUploadsAsync(paths, prefix!));
    }

    private ExplorerContextTarget ResolveBrowserDropTarget(DragEventArgs e)
    {
        var explorer = Explorer;
        var source = e.OriginalSource as DependencyObject;
        if (source is null || explorer is null)
            return default;
        if (explorer.ShowObjectList)
        {
            ItemsControl host = explorer.IsIconView ? ObjectsIcons : ObjectsGrid;
            if (IsWithin(source, host))
                return ResolveObjectContextTarget(host, source, e.GetPosition(host));
        }

        return explorer.ShowEmptyObjects && IsWithin(source, EmptyObjectsArea)
            ? new ExplorerContextTarget(ExplorerContextTargetKind.Directory, null) : default;
    }

}
