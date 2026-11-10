using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Configuration;

namespace Cavdeck.Infrastructure.Updates;

/// <summary>
/// Uses Windows file-sharing semantics as a cross-process reader/writer gate.
/// Every CLI process holds a shared handle; the desktop updater must obtain an
/// exclusive handle before Velopack can replace the current application directory.
/// </summary>
public sealed class FileApplicationActivityGate : IApplicationActivityGate
{
    private readonly string _path;

    public FileApplicationActivityGate(string? path = null)
    {
        _path = path ?? Path.Combine(
            CavdeckDataPaths.LocalDirectory, "locks", "application-activity.lock");
    }

    public IDisposable AcquireCliLease()
    {
        var pins = ExactLocalPaths.Pin(_path, includeLeaf: false, createDirectories: true);
        try
        {
            ExactLocalPaths.RejectExistingReparsePoint(_path);
            var stream = new FileStream(
                _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            return new Lease(stream, pins);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            pins.Dispose();
            throw new ApplicationUpdateInProgressException();
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    public IDisposable? TryAcquireUpdateLease()
    {
        var pins = ExactLocalPaths.Pin(_path, includeLeaf: false, createDirectories: true);
        try
        {
            ExactLocalPaths.RejectExistingReparsePoint(_path);
            var stream = new FileStream(
                _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new Lease(stream, pins);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            pins.Dispose();
            return null;
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) is 32 or 33;

    private sealed class Lease(FileStream stream, IDisposable pins) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { stream.Dispose(); }
            finally { pins.Dispose(); }
        }
    }
}

/// <summary>Raised when a CLI command starts while the desktop updater owns the exclusive lease.</summary>
public sealed class ApplicationUpdateInProgressException()
    : IOException("Cavdeck is applying an update. Retry the command after the update completes.")
{
}
