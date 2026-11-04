using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Cavdeck.App.Features.Explorer;

public enum FileClipboardState { Unavailable, Empty, Files }
public enum LocalClipboardOperation { Copy, Move }

public sealed record FileClipboardSnapshot(
    FileClipboardState State, uint SequenceNumber, IReadOnlyList<string> Paths,
    LocalClipboardOperation Operation = LocalClipboardOperation.Copy);

public static class WindowsFileClipboard
{
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint DropEffectLink = 4;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public static uint SequenceNumber() => GetClipboardSequenceNumber();

    // WPF Clipboard must be read on the calling STA thread. A bounded retry covers
    // the brief lock held by other clipboard owners without retaining stale data.
    public static FileClipboardSnapshot Read()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var before = SequenceNumber();
                if (before == 0)
                    return new(FileClipboardState.Unavailable, 0, []);
                var data = Clipboard.GetDataObject();
                var snapshot = FromDataObject(data, before);
                return SequenceNumber() == before ? snapshot
                    : new(FileClipboardState.Unavailable, 0, []);
            }
            catch (Exception exception) when (exception is ExternalException or COMException)
            {
                if (attempt < 2)
                    System.Threading.Thread.Sleep(15);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            {
                break;
            }
        }
        return new(FileClipboardState.Unavailable, 0, []);
    }

    public static FileClipboardSnapshot FromDataObject(IDataObject? data, uint sequenceNumber)
    {
        if (sequenceNumber == 0 || data is null)
            return new(FileClipboardState.Empty, sequenceNumber, []);
        if (!data.GetDataPresent(DataFormats.FileDrop, false))
            return new(FileClipboardState.Empty, sequenceNumber, []);
        if (data.GetData(DataFormats.FileDrop, false) is not string[] paths || paths.Length == 0
            || paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            return new(FileClipboardState.Unavailable, sequenceNumber, []);

        var operation = LocalClipboardOperation.Copy;
        if (data.GetDataPresent(PreferredDropEffect, false))
        {
            var effect = data.GetData(PreferredDropEffect, false);
            byte[]? bytes = effect switch
            {
                byte[] array => array,
                MemoryStream stream => stream.ToArray(),
                _ => null
            };
            if (bytes is not { Length: >= 4 })
                return new(FileClipboardState.Unavailable, sequenceNumber, []);
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            // Explorer can advertise COPY | LINK (5). LINK is an alternative
            // capability; the COPY and MOVE bits determine the paste intent.
            var intent = flags & (DropEffectCopy | DropEffectMove);
            if ((flags & ~(DropEffectCopy | DropEffectMove | DropEffectLink)) != 0
                || intent == 0 || intent == (DropEffectCopy | DropEffectMove))
                return new(FileClipboardState.Unavailable, sequenceNumber, []);
            if ((flags & DropEffectMove) != 0)
                operation = LocalClipboardOperation.Move;
        }
        return new(FileClipboardState.Files, sequenceNumber, paths.ToArray(), operation);
    }
}
