using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Configuration;

namespace Cavdeck.Infrastructure.Updates;

/// <summary>
/// Uses Windows file-sharing semantics as a cross-process reader/writer gate.
/// Every CLI process holds a shared handle; the desktop updater must obtain an
/// exclusive handle before Velopack can replace the current application directory.
/// A durable marker keeps CLI startup blocked after the desktop exits and until
/// the newly installed Desktop version has started.
/// </summary>
public sealed class FileApplicationActivityGate : IApplicationActivityGate
{
    private readonly string _path;
    private string PendingUpdatePath => _path + ".pending";

    public FileApplicationActivityGate(string? path = null)
    {
        _path = path ?? Path.Combine(
            CavdeckDataPaths.LocalDirectory, "locks", "application-activity.lock");
    }

    public IDisposable AcquireCliLease()
    {
        var pins = ExactLocalPaths.Pin(_path, includeLeaf: false, createDirectories: true);
        FileStream? stream = null;
        try
        {
            ExactLocalPaths.RejectExistingReparsePoint(_path);
            stream = new FileStream(
                _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

            // Check after acquiring the shared lease. The updater cannot create the
            // marker while this lease exists, so this closes the check/start race.
            if (HasPendingUpdateMarker())
                throw new ApplicationUpdateInProgressException();

            return new Lease(stream, pins);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            stream?.Dispose();
            pins.Dispose();
            throw new ApplicationUpdateInProgressException();
        }
        catch
        {
            stream?.Dispose();
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

    public void MarkUpdatePending(string targetVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        var version = targetVersion.Trim();
        if (version.Length > 128 || version.Any(char.IsControl))
            throw new ArgumentException("The update version is invalid.", nameof(targetVersion));

        var markerPath = PendingUpdatePath;
        using var pins = ExactLocalPaths.Pin(markerPath, includeLeaf: false, createDirectories: true);
        ExactLocalPaths.RejectExistingReparsePoint(markerPath);

        var temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, version);
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public void ClearUpdatePending()
    {
        var markerPath = PendingUpdatePath;
        using var pins = ExactLocalPaths.Pin(markerPath, includeLeaf: false, createDirectories: true);
        ExactLocalPaths.RejectExistingReparsePoint(markerPath);
        File.Delete(markerPath);
    }

    public void ReconcileUpdatePending(string currentVersion, bool hasPendingUpdate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        using var updateLease = TryAcquireUpdateLease();
        if (updateLease is null)
            return;

        var pendingVersion = ReadPendingUpdateVersion();
        if (pendingVersion is null)
            return;

        if (!hasPendingUpdate || VersionsEqual(pendingVersion, currentVersion))
            ClearUpdatePending();
    }

    private bool HasPendingUpdateMarker()
    {
        var markerPath = PendingUpdatePath;
        using var pins = ExactLocalPaths.Pin(markerPath, includeLeaf: false, createDirectories: true);
        ExactLocalPaths.RejectExistingReparsePoint(markerPath);
        try
        {
            using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private string? ReadPendingUpdateVersion()
    {
        var markerPath = PendingUpdatePath;
        using var pins = ExactLocalPaths.Pin(markerPath, includeLeaf: false, createDirectories: true);
        ExactLocalPaths.RejectExistingReparsePoint(markerPath);
        try
        {
            return File.ReadAllText(markerPath).Trim();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool VersionsEqual(string left, string right) =>
        NormalizeVersion(left).Equals(NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string value)
    {
        var normalized = value.Trim().Split('+', 2)[0].Split('-', 2)[0];
        var components = normalized.Split('.');
        while (components.Length > 3 && components[^1] == "0")
            components = components[..^1];
        return string.Join('.', components);
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

/// <summary>Raised when a CLI command starts while the desktop updater owns the installation.</summary>
public sealed class ApplicationUpdateInProgressException()
    : IOException("Cavdeck is being updated. Retry the command after the update completes.")
{
}
