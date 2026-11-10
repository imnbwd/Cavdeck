using Cavdeck.Application.Abstractions;
using Velopack;
using Velopack.Sources;

namespace Cavdeck.Infrastructure.Updates;

/// <summary>
/// The public stable feed is a fixed application trust boundary. Never load executable
/// updates from a user-supplied URL or a private repository token.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private const string OfficialRepository = "https://github.com/imnbwd/Cavdeck";
    private readonly ITransferActivity _transfers;
    private readonly IApplicationActivityGate _activityGate;
    private UpdateManager? _manager;
    private UpdateInfo? _available;

    public VelopackUpdateService(ITransferActivity transfers, IApplicationActivityGate activityGate)
    {
        _transfers = transfers;
        _activityGate = activityGate;
    }

    public bool HasPendingUpdate => GetLocalState().Status == UpdateStatus.Downloaded;

    public UpdateCheckResult GetLocalState()
    {
        try
        {
            var manager = Manager();
            if (!manager.IsInstalled || manager.IsPortable)
                return new(UpdateStatus.NotInstalled);
            var pending = manager.UpdatePendingRestart;
            return pending is null
                ? new(UpdateStatus.Idle)
                : new(UpdateStatus.Downloaded, pending.Version.ToString());
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var local = GetLocalState();
        if (local.Status != UpdateStatus.Idle)
            return local;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await Manager().CheckForUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _available = latest;
            return latest is null
                ? new(UpdateStatus.UpToDate)
                : new(UpdateStatus.Available, latest.TargetFullRelease.Version.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }
    }

    public async Task<UpdateCheckResult> DownloadAsync(IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var local = GetLocalState();
        if (local.Status != UpdateStatus.Idle)
            return local;
        var available = _available;
        if (available is null)
            return new(UpdateStatus.Failed, Message: "No checked release is available.");
        try
        {
            await Manager().DownloadUpdatesAsync(available,
                progress is null ? null : percent => progress.Report(percent),
                cancellationToken);
            _available = null;
            var pending = GetLocalState();
            return pending.Status == UpdateStatus.Downloaded
                ? pending
                : new(UpdateStatus.Failed, Message: "The downloaded update is not ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }
    }

    public void ApplyPendingAndRestart()
    {
        if (_transfers.HasActiveTransfers)
            throw new InvalidOperationException("Active transfers must complete before updating.");

        var manager = Manager();
        if (!manager.IsInstalled || manager.IsPortable)
            throw new InvalidOperationException("An installed release is required to update.");
        var pending = manager.UpdatePendingRestart
            ?? throw new InvalidOperationException("No downloaded update is ready.");

        // The Desktop's exclusive lease prevents concurrent CLI work while this marker
        // is created. The marker survives process exit, when Windows releases the lease,
        // and is removed by the newly installed Desktop after Velopack has completed.
        _activityGate.MarkUpdatePending(pending.Version.ToString());
        try
        {
            // Never call ApplyUpdatesAndRestart: it kills this process without the
            // application's normal persistence, cancellation and shutdown flow.
            manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
        }
        catch
        {
            // Scheduling failed before the process was handed to Velopack. The caller
            // still owns the exclusive lease, so it is safe to remove the marker here.
            try { _activityGate.ClearUpdatePending(); }
            catch { /* Keep the marker if cleanup cannot be proven safe. */ }
            throw;
        }
    }

    private UpdateManager Manager()
    {
        if (_manager is not null)
            return _manager;

#if DEBUG
        // Local feed override exists only in Debug builds for two-installed-version testing.
        var localFeed = Environment.GetEnvironmentVariable("CAVDECK_DEV_UPDATE_FEED");
        if (!string.IsNullOrWhiteSpace(localFeed))
            return _manager = new UpdateManager(localFeed,
                new UpdateOptions { ExplicitChannel = "stable" });
#endif
        return _manager = new UpdateManager(
            new GithubSource(OfficialRepository, accessToken: null, prerelease: false),
            new UpdateOptions { ExplicitChannel = "stable", AllowVersionDowngrade = false });
    }

    private static UpdateCheckResult Failed(Exception exception) =>
        // Do not surface arbitrary HTTP or file-system exception text in About.
        new(UpdateStatus.Failed, Message: exception.GetType().Name);
}
