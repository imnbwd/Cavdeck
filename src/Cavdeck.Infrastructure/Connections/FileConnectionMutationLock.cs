using System.Diagnostics;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Configuration;

namespace Cavdeck.Infrastructure.Connections;

public sealed class FileConnectionMutationLock(string? path = null, TimeSpan? timeout = null) : IConnectionMutationLock
{
    private readonly string _path = path ?? Path.Combine(CavdeckDataPaths.LocalDirectory, "locks", "connection-catalog.lock");
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var pins = ExactLocalPaths.Pin(_path, includeLeaf: false, createDirectories: true);
        try
        {
            ExactLocalPaths.RejectExistingReparsePoint(_path);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Lease(stream, pins);
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
                {
                    if (timer.Elapsed >= _timeout)
                        throw new ConnectionCatalogBusyException();
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                }
            }
        }
        catch { pins.Dispose(); throw; }
    }

    private sealed class Lease(FileStream stream, IDisposable pins) : IDisposable
    {
        public void Dispose()
        {
            try { stream.Dispose(); }
            finally { pins.Dispose(); }
        }
    }
}
