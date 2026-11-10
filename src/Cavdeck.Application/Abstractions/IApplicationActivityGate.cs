namespace Cavdeck.Application.Abstractions;

/// <summary>
/// Coordinates CLI process lifetime with replacement of the installed application directory.
/// CLI leases may coexist; an update lease is exclusive.
/// </summary>
public interface IApplicationActivityGate
{
    IDisposable AcquireCliLease();
    IDisposable? TryAcquireUpdateLease();
}
