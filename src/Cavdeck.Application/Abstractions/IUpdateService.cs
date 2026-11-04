namespace Cavdeck.Application.Abstractions;

public enum UpdateStatus
{
    Idle,
    Checking,
    Downloading,
    NotConfigured,
    NotInstalled,
    UpToDate,
    Available,
    Downloaded,
    Failed
}

public sealed record UpdateCheckResult(UpdateStatus Status, string? Version = null, string? Message = null);

public interface IUpdateService
{
    // Local-only: safe to call even if automatic network checks are disabled.
    UpdateCheckResult GetLocalState();
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
    Task<UpdateCheckResult> DownloadAsync(IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
    bool HasPendingUpdate { get; }

    // Launches the updater, which waits for this process to exit. Does not exit on its own.
    // Caller must first obtain consent, save all work and stop application services.
    void ApplyPendingAndRestart();
}

public interface ITransferActivity
{
    bool HasActiveTransfers { get; }
}
