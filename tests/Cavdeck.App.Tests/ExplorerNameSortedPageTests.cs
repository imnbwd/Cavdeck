using System.Collections.Specialized;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerNameSortedPageTests
{
    [Fact]
    public void Loading_more_keeps_name_order_and_preserves_existing_row_identity()
    {
        var texts = new LocalizationService();
        var folder = new ExplorerRow(Entry(ObjectEntryKind.Folder, "books"), texts);
        var file = new ExplorerRow(Entry(ObjectEntryKind.File, "readme.md"), texts);
        var rows = new ResettableCollection<ExplorerRow> { folder, file };
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        ExplorerNameSortedPage.Append(rows,
        [
            Entry(ObjectEntryKind.File, "AGENTS.md"),
            Entry(ObjectEntryKind.Folder, "assets"),
            Entry(ObjectEntryKind.File, "readme.md"), // Already loaded
            Entry(ObjectEntryKind.File, "test.html")
        ], texts);

        Assert.Equal(["assets", "books", "AGENTS.md", "readme.md", "test.html"],
            rows.Select(row => row.Name).ToArray());
        Assert.Same(folder, rows[1]);
        Assert.Same(file, rows[3]);
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    [Fact]
    public void Appended_page_follows_the_same_name_order_as_full_sort()
    {
        var texts = new LocalizationService();
        var first = new[]
        {
            Entry(ObjectEntryKind.Folder, "m"),
            Entry(ObjectEntryKind.File, "z.txt")
        };
        var next = new[]
        {
            Entry(ObjectEntryKind.File, "Alpha.txt"),
            Entry(ObjectEntryKind.Folder, "a"),
            Entry(ObjectEntryKind.File, "beta.txt"),
            Entry(ObjectEntryKind.Folder, "z")
        };
        var rows = new ResettableCollection<ExplorerRow>();
        rows.ReplaceWith(ObjectLayout.Sort(first, ListingSort.Name, descending: false)
            .Select(item => new ExplorerRow(item, texts)));

        ExplorerNameSortedPage.Append(rows, next, texts);

        var expected = ObjectLayout.Sort(first.Concat(next).ToArray(), ListingSort.Name, descending: false);
        Assert.Equal(expected.Select(x => x.Key), rows.Select(x => x.Entry.Key));
    }

    [Fact]
    public void Appended_numeric_names_follow_the_same_natural_order_as_full_sort()
    {
        var texts = new LocalizationService();
        var first = new[]
        {
            Entry(ObjectEntryKind.File, "file10.txt")
        };
        var next = new[]
        {
            Entry(ObjectEntryKind.File, "file2.txt"),
            Entry(ObjectEntryKind.File, "file1.txt"),
            Entry(ObjectEntryKind.File, "file20.txt")
        };
        var rows = new ResettableCollection<ExplorerRow>();
        rows.ReplaceWith(ObjectLayout.Sort(first, ListingSort.Name, descending: false)
            .Select(item => new ExplorerRow(item, texts)));

        ExplorerNameSortedPage.Append(rows, next, texts);

        var expected = ObjectLayout.Sort(first.Concat(next).ToArray(), ListingSort.Name, descending: false);
        Assert.Equal(expected.Select(item => item.Key), rows.Select(row => row.Entry.Key));
        Assert.Equal(
            ["file1.txt", "file2.txt", "file10.txt", "file20.txt"],
            rows.Select(row => row.Name).ToArray());
    }

    private static ObjectEntry Entry(ObjectEntryKind kind, string name) =>
        new(kind, name, name + (kind == ObjectEntryKind.Folder ? "/" : ""), 0, null, null);
}
