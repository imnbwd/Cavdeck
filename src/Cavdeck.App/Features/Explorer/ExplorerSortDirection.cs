using System.ComponentModel;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

/// <summary>Consistent first-click sorting for file metadata columns.</summary>
public static class ExplorerSortDirection
{
    public static ListingSort? SortFor(string? member) => member switch
    {
        "Name" => ListingSort.Name,
        "SizeBytes" => ListingSort.Size,
        "Modified" => ListingSort.Modified,
        _ => null
    };

    public static ListSortDirection? DirectionFor(string? member, ListingSort? active, bool descending) =>
        SortFor(member) is { } column && column == active
            ? descending ? ListSortDirection.Descending : ListSortDirection.Ascending
            : null;

    public static bool NextDescending(string? column, ListSortDirection? current) => current switch
    {
        ListSortDirection.Ascending => true,
        ListSortDirection.Descending => false,
        // Newest and largest first; file names alphabetically ascending.
        _ => column is "Modified" or "SizeBytes"
    };
}
