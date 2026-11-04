using System.IO;
using System.Windows;
using Cavdeck.App.Features.Explorer;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class WindowsFileClipboardTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void File_drop_keeps_every_path(int count)
    {
        var paths = Enumerable.Range(0, count)
            .Select(index => Path.GetFullPath($"item-{index}.txt")).ToArray();
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);

        var snapshot = WindowsFileClipboard.FromDataObject(data, 4);

        Assert.Equal(FileClipboardState.Files, snapshot.State);
        Assert.Equal(paths, snapshot.Paths);
        Assert.Equal(LocalClipboardOperation.Copy, snapshot.Operation);
    }

    [Fact]
    public void Move_effect_is_little_endian_and_does_not_change_the_file_list()
    {
        var path = Path.GetFullPath("folder");
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path });
        data.SetData("Preferred DropEffect", new MemoryStream([2, 0, 0, 0]));

        var snapshot = WindowsFileClipboard.FromDataObject(data, 8);

        Assert.Equal(LocalClipboardOperation.Move, snapshot.Operation);
        Assert.Equal(path, Assert.Single(snapshot.Paths));
    }

    [Theory]
    [InlineData(1u, LocalClipboardOperation.Copy)]
    [InlineData(5u, LocalClipboardOperation.Copy)]
    [InlineData(2u, LocalClipboardOperation.Move)]
    [InlineData(6u, LocalClipboardOperation.Move)]
    public void Recognized_drop_effect_bits_select_copy_or_move(uint flags, LocalClipboardOperation expected)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { Path.GetFullPath("file.txt") });
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, flags);
        data.SetData("Preferred DropEffect", new MemoryStream(bytes));

        var snapshot = WindowsFileClipboard.FromDataObject(data, 9);

        Assert.Equal(FileClipboardState.Files, snapshot.State);
        Assert.Equal(expected, snapshot.Operation);
        Assert.Equal(PasteSource.LocalFiles, new ExplorerClipboard().Resolve(snapshot));
    }

    [Fact]
    public void Text_even_when_it_is_a_real_file_path_is_not_a_file_drop()
    {
        var data = new DataObject();
        data.SetText(Path.GetFullPath("real-file.txt"));
        Assert.Equal(FileClipboardState.Empty, WindowsFileClipboard.FromDataObject(data, 3).State);
    }

    [Fact]
    public void Image_and_empty_clipboards_are_not_file_drops()
    {
        var image = new DataObject();
        image.SetData(DataFormats.Bitmap, new byte[] { 1, 2 });
        Assert.Equal(FileClipboardState.Empty, WindowsFileClipboard.FromDataObject(image, 3).State);
        Assert.Equal(FileClipboardState.Empty, WindowsFileClipboard.FromDataObject(null, 3).State);
    }

    [Fact]
    public void Malformed_file_drop_is_not_queued()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { "relative.txt" });
        Assert.Equal(FileClipboardState.Unavailable, WindowsFileClipboard.FromDataObject(data, 3).State);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(7u)]
    [InlineData(8u)]
    public void Ambiguous_or_unsupported_drop_effect_is_not_treated_as_copy(uint flags)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { Path.GetFullPath("file.txt") });
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, flags);
        data.SetData("Preferred DropEffect", bytes);
        Assert.Equal(FileClipboardState.Unavailable, WindowsFileClipboard.FromDataObject(data, 3).State);
    }

    [Fact]
    public void Latest_clipboard_sequence_controls_paste_source()
    {
        var internalClipboard = new ExplorerClipboard();
        var entry = new ObjectEntry(ObjectEntryKind.File, "cloud.txt", "cloud.txt", 1, null, null);
        internalClipboard.Set(new ObjectClipboard(Guid.NewGuid(), "bucket", false, [entry]), 10);
        var externalFile = new FileClipboardSnapshot(FileClipboardState.Files, 9, [Path.GetFullPath("local.txt")]);

        Assert.Equal(PasteSource.Internal, internalClipboard.Resolve(externalFile with { SequenceNumber = 10 }));
        Assert.Equal(PasteSource.LocalFiles, internalClipboard.Resolve(externalFile with { SequenceNumber = 11 }));
        Assert.Equal(PasteSource.None, internalClipboard.Resolve(new(FileClipboardState.Empty, 11, [])));
        Assert.Equal(PasteSource.None, internalClipboard.Resolve(new(FileClipboardState.Unavailable, 10, [])));
        Assert.Equal(PasteSource.None, internalClipboard.Resolve(new(FileClipboardState.Files, 0, externalFile.Paths)));
        internalClipboard.Set(new ObjectClipboard(Guid.NewGuid(), "bucket", true, [entry]), 12);
        Assert.Equal(PasteSource.Internal, internalClipboard.Resolve(new(FileClipboardState.Empty, 12, [])));
    }
}
