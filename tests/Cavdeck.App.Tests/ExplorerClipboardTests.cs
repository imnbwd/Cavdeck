using Cavdeck.App.Features.Explorer;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerClipboardTests
{
    [Fact]
    public void Cut_clipboard_remains_valid_until_its_matching_clipboard_is_cleared()
    {
        var clipboard = new ExplorerClipboard();
        var entry = new ObjectEntry(ObjectEntryKind.File, "a.txt", "a.txt", 1, null, null);
        var cut = new ObjectClipboard(Guid.NewGuid(), "bucket", true, [entry]);
        clipboard.Set(cut, sequenceNumber: 42);
        var system = new FileClipboardSnapshot(FileClipboardState.Empty, 42, []);

        Assert.Equal(PasteSource.Internal, clipboard.Resolve(system));
        Assert.True(clipboard.Current!.Cut);
        clipboard.ClearIfCurrent(cut);
        Assert.Equal(PasteSource.None, clipboard.Resolve(system));
    }

    [Fact]
    public void External_clipboard_replacement_invalidates_internal_cut_without_reusing_stale_data()
    {
        var clipboard = new ExplorerClipboard();
        var entry = new ObjectEntry(ObjectEntryKind.File, "a.txt", "a.txt", 1, null, null);
        clipboard.Set(new ObjectClipboard(Guid.NewGuid(), "bucket", true, [entry]),
            sequenceNumber: 42);

        Assert.Equal(PasteSource.None, clipboard.Resolve(new FileClipboardSnapshot(
            FileClipboardState.Empty, 43, [])));
        Assert.Equal(PasteSource.LocalFiles, clipboard.Resolve(new FileClipboardSnapshot(
            FileClipboardState.Files, 43, ["C:\\safe\\file.txt"])));
    }

    [Fact]
    public void Storage_mutation_notification_does_not_modify_clipboard_or_its_sequence()
    {
        var clipboard = new ExplorerClipboard();
        var cut = new ObjectClipboard(Guid.NewGuid(), "source", true,
            [new ObjectEntry(ObjectEntryKind.File, "a.txt", "a.txt", 1, null, null)]);
        clipboard.Set(cut, sequenceNumber: 42);
        var origin = new object();
        object? notifiedSender = null;
        ExplorerStorageChanged? notified = null;
        var clipboardChanges = 0;
        clipboard.Changed += (_, _) => clipboardChanges++;
        clipboard.StorageChanged += (sender, change) =>
        {
            notifiedSender = sender;
            notified = change;
        };

        clipboard.PublishStorageChanged(origin, cut.ConnectionId, "target");

        Assert.Same(origin, notifiedSender);
        Assert.Equal(cut.ConnectionId, notified?.ConnectionId);
        Assert.Equal("target", notified?.Bucket);
        Assert.Same(cut, clipboard.Current);
        Assert.Equal((uint)42, clipboard.InternalSequenceNumber);
        Assert.Equal(0, clipboardChanges);
    }

    [Fact]
    public void Replacing_a_clipboard_does_not_allow_an_older_operation_to_clear_it()
    {
        var clipboard = new ExplorerClipboard();
        var connection = Guid.NewGuid();
        var entry = new ObjectEntry(ObjectEntryKind.File, "a.txt", "a.txt", 1, null, null);
        var first = new ObjectClipboard(connection, "source", true, [entry]);
        var second = new ObjectClipboard(connection, "source", false, [entry]);
        var changes = 0;
        clipboard.Changed += (_, _) => changes++;

        clipboard.Set(first);
        clipboard.Set(second);
        clipboard.ClearIfCurrent(first);

        Assert.Same(second, clipboard.Current);
        clipboard.ClearIfCurrent(second);
        Assert.Null(clipboard.Current);
        Assert.Equal(3, changes);
    }
}
