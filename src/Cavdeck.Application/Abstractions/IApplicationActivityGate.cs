namespace Cavdeck.Application.Abstractions;

/// <summary>
/// Coordinates CLI process lifetime with replacement of the installed application directory.
/// CLI leases may coexist; an update lease is exclusive. A persistent marker keeps new CLI
/// processes blocked after the desktop exits and until the updater has completed.
/// </summary>
public interface IApplicationActivityGate
{
    IDisposable AcquireCliLease();
    IDisposable? TryAcquireUpdateLease();
    void MarkUpdatePending(string targetVersion);
    void ClearUpdatePending();
    void ReconcileUpdatePending(string currentVersion, bool hasPendingUpdate);
}
