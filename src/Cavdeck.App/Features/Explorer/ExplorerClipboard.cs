using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

// Shared by Explorer tabs. This is an in-app object clipboard, not the Windows file clipboard.
public sealed record ObjectClipboard(
    Guid ConnectionId, string SourceBucket, bool Cut, IReadOnlyList<ObjectEntry> Entries)
{
    public bool Attempted { get; private set; }

    public void MarkAttempted() => Attempted = true;
}

public sealed record ExplorerStorageChanged(Guid ConnectionId, string Bucket);

public sealed class ExplorerClipboard
{
    public event EventHandler? Changed;
    public event EventHandler<ExplorerStorageChanged>? StorageChanged;

    // Broadcast completed server-side mutations to other open Explorer tabs.
    // This does not change the user's clipboard contents or its sequence.
    public void PublishStorageChanged(object sender, Guid connectionId, string bucket) =>
        StorageChanged?.Invoke(sender, new ExplorerStorageChanged(connectionId, bucket));

    public ObjectClipboard? Current { get; private set; }
    public uint InternalSequenceNumber { get; private set; }

    public PasteSource Resolve(FileClipboardSnapshot system) =>
        system.SequenceNumber == 0 ? PasteSource.None
        : Current is not null && system.State != FileClipboardState.Unavailable && InternalSequenceNumber != 0
            && InternalSequenceNumber == system.SequenceNumber
                ? PasteSource.Internal
                : system.State == FileClipboardState.Files
                    ? PasteSource.LocalFiles : PasteSource.None;

    public void Set(ObjectClipboard value) => Set(value, WindowsFileClipboard.SequenceNumber());

    public void Set(ObjectClipboard value, uint sequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(value);
        Current = value;
        InternalSequenceNumber = sequenceNumber;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearIfCurrent(ObjectClipboard value)
    {
        if (!ReferenceEquals(Current, value))
            return;
        Current = null;
        InternalSequenceNumber = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public enum PasteSource { None, Internal, LocalFiles }
