using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class ObjectLayoutTests
{
    [Fact]
    public void Compose_keeps_one_level_and_hides_the_current_prefix()
    {
        var entries = ObjectLayout.Compose(
            "images/",
            ["images/photos/", "images/photos/nested/", "other/"],
            [
                new StorageObject("assets", "images/", 0, DateTimeOffset.UnixEpoch, null, null),
                new StorageObject("assets", "images/logo.png", 32, DateTimeOffset.UnixEpoch, "\"abc\"", null),
                new StorageObject("assets", "images/photos/", 0, DateTimeOffset.UnixEpoch, null, null),
                new StorageObject("assets", "images/photos/nested/a.jpg", 10, DateTimeOffset.UnixEpoch, null, null)
            ]);

        Assert.Equal(["photos", "logo.png"], entries.Select(entry => entry.Name).ToArray());
        Assert.Equal(ObjectEntryKind.Folder, entries[0].Kind);
        Assert.Equal("images/photos/", entries[0].Key);
        Assert.Equal(32, entries[1].SizeBytes);
    }

    [Fact]
    public void Sort_keeps_folders_ahead_of_files()
    {
        var entries = ObjectLayout.Sort(
        [
            new ObjectEntry(ObjectEntryKind.File, "b.txt", "b.txt", 9, null, null),
            new ObjectEntry(ObjectEntryKind.Folder, "a", "a/", 0, null, null),
            new ObjectEntry(ObjectEntryKind.File, "a.txt", "a.txt", 1, null, null)
        ], ListingSort.Size, descending: true);

        Assert.Equal(["a", "b.txt", "a.txt"], entries.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Sort_uses_natural_order_for_names_and_keeps_folders_first()
    {
        var entries = new[]
        {
            new ObjectEntry(ObjectEntryKind.File, "file10.txt", "file10.txt", 0, null, null),
            new ObjectEntry(ObjectEntryKind.Folder, "folder10", "folder10/", 0, null, null),
            new ObjectEntry(ObjectEntryKind.File, "file2.txt", "file2.txt", 0, null, null),
            new ObjectEntry(ObjectEntryKind.Folder, "folder2", "folder2/", 0, null, null),
            new ObjectEntry(ObjectEntryKind.File, "file1.txt", "file1.txt", 0, null, null)
        };

        var sorted = ObjectLayout.Sort(entries, ListingSort.Name, descending: false);

        Assert.Equal(
            ["folder2", "folder10", "file1.txt", "file2.txt", "file10.txt"],
            sorted.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Sort_uses_natural_order_for_descending_names_without_reversing_folder_priority()
    {
        var entries = new[]
        {
            new ObjectEntry(ObjectEntryKind.File, "file2.txt", "file2.txt", 0, null, null),
            new ObjectEntry(ObjectEntryKind.Folder, "folder2", "folder2/", 0, null, null),
            new ObjectEntry(ObjectEntryKind.File, "file10.txt", "file10.txt", 0, null, null),
            new ObjectEntry(ObjectEntryKind.Folder, "folder10", "folder10/", 0, null, null)
        };

        var sorted = ObjectLayout.Sort(entries, ListingSort.Name, descending: true);

        Assert.Equal(
            ["folder10", "folder2", "file10.txt", "file2.txt"],
            sorted.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Sort_uses_natural_name_order_as_size_tiebreaker()
    {
        var entries = new[]
        {
            new ObjectEntry(ObjectEntryKind.File, "file10.txt", "file10.txt", 100, null, null),
            new ObjectEntry(ObjectEntryKind.File, "file2.txt", "file2.txt", 100, null, null),
            new ObjectEntry(ObjectEntryKind.File, "file1.txt", "file1.txt", 100, null, null)
        };

        var sorted = ObjectLayout.Sort(entries, ListingSort.Size, descending: false);

        Assert.Equal(
            ["file1.txt", "file2.txt", "file10.txt"],
            sorted.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Object_name_comparer_uses_ordinal_tiebreaker_for_case_only_differences()
    {
        var comparer = ObjectNameComparer.Instance;

        Assert.True(comparer.Compare("File2.txt", "file2.txt") < 0);
        Assert.True(comparer.Compare("file2.txt", "File2.txt") > 0);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("images/", "")]
    [InlineData("images/photos/", "images/")]
    public void Parent_walks_one_prefix(string prefix, string? parent) =>
        Assert.Equal(parent, ObjectLayout.ParentPrefix(prefix));

    [Fact]
    public void Names_reject_separators_and_dot_segments()
    {
        Assert.False(ObjectLayout.TryJoin("images/", "a/b", false, out _, out var slash));
        Assert.Equal(NameIssue.Separator, slash);
        Assert.False(ObjectLayout.TryJoin("images/", "..", true, out _, out var reserved));
        Assert.Equal(NameIssue.Reserved, reserved);
    }

    [Fact]
    public void Copy_into_a_descendant_is_detected()
    {
        Assert.True(ObjectLayout.IsInside("images/photos/raw/", "images/photos/"));
        Assert.False(ObjectLayout.IsInside("images/photos (2)/", "images/photos/"));
        Assert.Equal("images/album/a.jpg", ObjectLayout.MapKey("images/photos/a.jpg", "images/photos/", "images/album/"));
    }

    [Fact]
    public void Sibling_names_keep_a_short_extension()
    {
        Assert.Equal("logo (2).png", ObjectLayout.SiblingName("logo.png", 2));
        Assert.Equal("notes (2)", ObjectLayout.SiblingName("notes", 2));
    }
}
