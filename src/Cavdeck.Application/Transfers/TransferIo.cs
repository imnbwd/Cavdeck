using System.Security.Cryptography;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

internal sealed class ConcurrencyGate
{
    private enum WaiterState { Waiting, Granted, Cancelled }

    private sealed class Waiter(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public WaiterState State { get; set; }
    }

    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private int _limit;
    private int _used;

    public ConcurrencyGate(int limit) => _limit = Math.Max(1, limit);

    public void SetLimit(int limit)
    {
        lock (_gate)
        {
            _limit = Math.Max(1, limit);
            Pump();
        }
    }

    public Task AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_used < _limit)
            {
                _used++;
                return Task.CompletedTask;
            }

            var waiter = new Waiter(cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                // Register while holding the gate so a release cannot grant the
                // waiter before its cancellation callback has been installed.
                // Cancellation callbacks may run synchronously; Monitor locks are
                // re-entrant, and the state transition remains serialized here.
                waiter.Registration = cancellationToken.UnsafeRegister(
                    static (state, token) =>
                    {
                        var registration = ((ConcurrencyGate Gate, Waiter Waiter))state!;
                        registration.Gate.CancelWaiter(registration.Waiter, token);
                    },
                    (this, waiter));

                // The token may have been cancelled synchronously during Register.
                if (waiter.State != WaiterState.Waiting)
                    waiter.Registration.Unregister();
            }

            return waiter.Completion.Task;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_used == 0)
                throw new InvalidOperationException("A concurrency permit was released without being acquired.");

            _used--;
            Pump();
        }
    }

    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Once a permit is granted, acquisition wins the race. The caller
            // owns that permit and is responsible for releasing it in its finally.
            if (waiter.State != WaiterState.Waiting)
                return;

            waiter.State = WaiterState.Cancelled;
            if (waiter.Node is { } node)
                _waiters.Remove(node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(cancellationToken);
            Pump();
        }
    }

    private void Pump()
    {
        while (_used < _limit && _waiters.First is { } node)
        {
            _waiters.RemoveFirst();
            var next = node.Value;
            next.Node = null;
            if (next.State != WaiterState.Waiting)
                continue;

            // State, permit accounting and completion are changed under the same
            // lock. A concurrent cancellation can therefore either cancel a
            // queued waiter or observe a granted permit, never split the two.
            next.State = WaiterState.Granted;
            _used++;
            next.Registration.Unregister();
            next.Completion.TrySetResult(true);
        }
    }
}

internal interface ITransferSink
{
    TransferJob Current(Guid id);
    Task PublishAsync(TransferJob job, CancellationToken cancellationToken);
    void ReportProgress(TransferJob job);
    void ResetProgress(TransferJob job);
    Task PersistCheckpointAsync(TransferJob job, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferPart>> GetPartsAsync(Guid id, CancellationToken cancellationToken);
    Task SavePartAsync(Guid id, TransferPart part, CancellationToken cancellationToken);
    Task ClearPartsAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class FileWindowStream : Stream
{
    private readonly FileStream _file;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    public FileWindowStream(string path, long offset, long length)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81_920, FileOptions.SequentialScan);
        _offset = offset;
        _length = length;
        _file.Seek(offset, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = Remaining(count);
        if (allowed == 0)
            return 0;
        var read = _file.Read(buffer, offset, allowed);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var allowed = Remaining(buffer.Length);
        if (allowed == 0)
            return 0;
        var read = await _file.ReadAsync(buffer[..allowed], cancellationToken);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        if (next < 0 || next > _length)
            throw new IOException("Seek is outside the part window.");
        _position = next;
        _file.Seek(_offset + next, SeekOrigin.Begin);
        return next;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _file.Dispose();
        base.Dispose(disposing);
    }

    private int Remaining(int count)
    {
        var left = _length - _position;
        if (left <= 0)
            return 0;
        return (int)Math.Min(count, left);
    }
}

internal sealed class DestinationChangedException : IOException
{
    public DestinationChangedException() : base("The local destination changed while downloading.") { }
}

internal readonly record struct LocalFileStamp(long Length, DateTime LastWriteUtc, DateTime CreationUtc);

internal static class TransferFiles
{
    public static LocalFileStamp? Snapshot(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            return null;
        return new LocalFileStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
    }

    public static async Task<string> Md5DigestAsync(Stream source, long expectedLength, CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
            throw new NotSupportedException("An upload source must support seeking for checksum verification.");
        var start = source.Position;
        try
        {
            var digest = await MD5.HashDataAsync(source, cancellationToken);
            if (source.Position - start != expectedLength)
                throw new IOException("The local file changed while its checksum was being calculated.");
            return Convert.ToBase64String(digest);
        }
        finally
        {
            source.Position = start;
        }
    }

    public static async Task<long> CopyAsync(
        Stream source, Stream destination, Func<long, Task>? progress, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81_920);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                if (progress is not null)
                    await progress(total);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        await destination.FlushAsync(cancellationToken);
        return total;
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81_920, FileOptions.SequentialScan);
        var hash = await MD5.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void Commit(string partial, string finalPath, Guid jobId, LocalFileStamp? initial)
    {
        var parent = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        var commit = TransferPaths.Commit(finalPath, jobId);
        if (File.Exists(commit))
            File.Delete(commit);
        File.Move(partial, commit);
        try
        {
            // A new target must never be overwritten, even if it appears between
            // the existence check and the move.
            if (initial is null)
            {
                if (File.Exists(finalPath))
                    throw new DestinationChangedException();
                try { File.Move(commit, finalPath); }
                catch (IOException) when (File.Exists(finalPath))
                {
                    throw new DestinationChangedException();
                }
                return;
            }

            // Exclude writers during the final identity check and replacement.
            // Share.Delete permits File.Replace on Windows.
            try
            {
                using var held = new FileStream(finalPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete);
                if (Snapshot(finalPath) != initial)
                    throw new DestinationChangedException();
                File.Replace(commit, finalPath, null, ignoreMetadataErrors: true);
            }
            catch (FileNotFoundException)
            {
                throw new DestinationChangedException();
            }
        }
        catch
        {
            // A failed commit must not strand an orphan .commit file. Retain the
            // verified partial so users can safely resolve conflicts and retry.
            try
            {
                if (File.Exists(commit) && !File.Exists(partial))
                    File.Move(commit, partial);
            }
            catch (IOException) { /* Preserve original failure; a later retry cleans up commit. */ }
            throw;
        }
    }

    public static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
