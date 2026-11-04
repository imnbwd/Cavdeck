using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Cavdeck.App.Features.Places;

public partial class PlacesView : UserControl
{
    public PlacesView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is PlacesPageViewModel previous)
                previous.PropertyChanged -= OnPagePropertyChanged;
            if (args.NewValue is PlacesPageViewModel page)
            {
                page.PropertyChanged += OnPagePropertyChanged;
                ApplyChrome(page);
                InitializeSorting(page);
            }
        };
        if (DataContext is PlacesPageViewModel current)
        {
            current.PropertyChanged += OnPagePropertyChanged;
            ApplyChrome(current);
            InitializeSorting(current);
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is PlacesPageViewModel page && e.PropertyName is nameof(PlacesPageViewModel.NameHeader)
            or nameof(PlacesPageViewModel.ConnectionHeader)
            or nameof(PlacesPageViewModel.LocationHeader)
            or nameof(PlacesPageViewModel.VisitedHeader)
            or null)
            ApplyChrome(page);
    }

    private void ApplyChrome(PlacesPageViewModel page)
    {
        NameColumn.Header = page.NameHeader;
        ConnectionColumn.Header = page.ConnectionHeader;
        LocationColumn.Header = page.LocationHeader;
        VisitedColumn.Header = page.VisitedHeader;
                VisitedColumn.Visibility = page.IsFavorites ? Visibility.Collapsed : Visibility.Visible;
        UpdateResponsiveColumns(page);
    }

        private void PlacesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is PlacesPageViewModel page)
            UpdateResponsiveColumns(page);
    }

    private void UpdateResponsiveColumns(PlacesPageViewModel page)
    {
        // Keep names and locations legible on compact Windows instead of allowing
        // the less-important connection column to crowd out the primary destination.
        var available = PlacesGrid.ActualWidth;
        ConnectionColumn.Visibility = available > 0 &&
            available < (page.IsFavorites ? 600 : 760)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InitializeSorting(PlacesPageViewModel page)
    {
        // Recent starts newest first. Favorites keep their saved order until sorted.
        if (!page.IsFavorites)
            ApplySort(page, VisitedColumn, ListSortDirection.Descending);
    }

    private void PlacesGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not PlacesPageViewModel page ||
            string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
            return;

        var direction = e.Column.SortDirection switch
        {
            ListSortDirection.Ascending => ListSortDirection.Descending,
            ListSortDirection.Descending => ListSortDirection.Ascending,
            _ => e.Column == VisitedColumn
                ? ListSortDirection.Descending : ListSortDirection.Ascending
        };
        ApplySort(page, e.Column, direction);
    }

    private void ApplySort(PlacesPageViewModel page, DataGridColumn column, ListSortDirection direction)
    {
        var view = CollectionViewSource.GetDefaultView(page.Items);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(column.SortMemberPath, direction));
        }

        foreach (var item in PlacesGrid.Columns)
            item.SortDirection = ReferenceEquals(item, column) ? direction : null;
    }

    private void PlacesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is PlacesPageViewModel page)
            page.Selected = PlacesGrid.SelectedItem as PlaceRow;
    }

    private void PlacesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { Item: PlaceRow row } ||
            DataContext is not PlacesPageViewModel page)
            return;

        var command = Keyboard.Modifiers == ModifierKeys.Control
            ? page.OpenNewTabCommand : page.OpenCommand;
        command.Execute(row);
        e.Handled = true;
    }

    private void PlacesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is PlaceRow && !row.IsSelected)
            PlacesGrid.SelectedItem = row.Item;
    }

    private void PlacesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PlacesPageViewModel page || PlacesGrid.SelectedItem is not PlaceRow row)
            return;

        if (e.Key == Key.Enter)
        {
            var command = Keyboard.Modifiers == ModifierKeys.Control
                ? page.OpenNewTabCommand : page.OpenCommand;
            command.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            page.RemoveCommand.Execute(row);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
