using System.Security.Cryptography;

namespace Cavdeck.Application.Transfers;

internal sealed class CrossPartBudget(long limit)
{
    private readonly object _gate = new();
    private readonly Queue<(long Bytes, TaskCompletionSource<bool> Ready)> _waiters = new();
    private long _used;

    public long Limit { get; } = limit;

    public async Task<IDisposable> AcquireAsync(long bytes, CancellationToken cancellationToken)
    {
        if (bytes > Limit)
            throw new NotSupportedException("The required transfer part exceeds the temporary storage budget.");
        Task? wait = null;
        CancellationTokenRegistration registration = default;
        lock (_gate)
        {
            if (_waiters.Count == 0 && bytes <= Limit - _used)
                _used += bytes;
            else
            {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue((bytes, ready));
                wait = ready.Task;
                registration = cancellationToken.Register(() =>
                {
                    ready.TrySetCanceled(cancellationToken);
                    lock (_gate)
                        Pump();
                });
            }
        }
        try
        {
            if (wait is not null)
                await wait;
        }
        finally
        {
            registration.Dispose();
        }
        return new Releaser(this, bytes);
    }

    private void Release(long bytes)
    {
        lock (_gate)
        {
            _used -= bytes;
            Pump();
        }
    }

    private void Pump()
    {
        while (_waiters.Count > 0)
        {
            var next = _waiters.Peek();
            if (next.Ready.Task.IsCompleted)
            {
                _waiters.Dequeue();
                continue;
            }
            if (next.Bytes > Limit - _used)
                break;
            _waiters.Dequeue();
            _used += next.Bytes;
            next.Ready.TrySetResult(true);
        }
    }

    private sealed class Releaser(CrossPartBudget owner, long bytes) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(bytes);
        }
    }
}

internal sealed class CrossPartBuffer : IAsyncDisposable
{
    private readonly IDisposable? _reservation;
    public Stream Content { get; }
    public string Sha256 { get; }
    public string Md5 { get; }

    private CrossPartBuffer(Stream content, string sha256, string md5, IDisposable? reservation)
    {
        Content = content;
        Sha256 = sha256;
        Md5 = md5;
        _reservation = reservation;
    }

    public static async Task<CrossPartBuffer> ReadAsync(Stream source, long length,
        long memoryLimit, CrossPartBudget tempBudget, CancellationToken cancellationToken)
    {
        IDisposable? reservation = null;
        Stream? content = null;
        try
        {
            if (length <= memoryLimit && length <= int.MaxValue)
                content = new MemoryStream((int)length);
            else
            {
                reservation = await tempBudget.AcquireAsync(length, cancellationToken);
                var path = Path.Combine(Path.GetTempPath(), "cavdeck-cross-part-" + Guid.NewGuid().ToString("N"));
                content = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    81_920, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            }
            var count = await TransferFiles.CopyAsync(source, content, null, cancellationToken);
            if (count != length)
                throw new IOException("The source range was truncated.");
            content.Position = 0;
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken));
            content.Position = 0;
            var md5 = Convert.ToBase64String(await MD5.HashDataAsync(content, cancellationToken));
            content.Position = 0;
            return new CrossPartBuffer(content, sha256, md5, reservation);
        }
        catch
        {
            if (content is not null)
                await content.DisposeAsync();
            reservation?.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Content.DisposeAsync();
        }
        finally
        {
            _reservation?.Dispose();
        }
    }
}
