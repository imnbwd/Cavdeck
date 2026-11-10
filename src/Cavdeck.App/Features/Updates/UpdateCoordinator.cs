using Cavdeck.App.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Transfers;

namespace Cavdeck.App.Features.Updates;

/// <summary>
/// One update operation per process, shared by automatic startup checks and manual commands.
/// All public members are invoked from WPF's UI context.
/// </summary>
public interface IUpdateCoordinator
{
    event EventHandler? Changed;
    UpdateStatus Status { get; }
    string? Version { get; }
    string? Diagnostic { get; }
    int Progress { get; }
    bool IsBusy { get; }
    bool CanDownload { get; }
    bool CanRestart { get; }
    Task CheckAsync();
    Task DownloadAsync();
    void RequestRestart();
}

public sealed class UpdateCoordinator : IUpdateCoordinator
{
    private readonly IUpdateService _updater;
    private readonly ApplicationState _state;
    private readonly ApplicationLifetimeCoordinator _lifetime;
    private readonly TransferEngine _transfers;
    private readonly IApplicationActivityGate _activityGate;
    private readonly DialogService _dialogs;
    private readonly LocalizationService _texts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateCoordinator(IUpdateService updater, ApplicationState state,
        ApplicationLifetimeCoordinator lifetime, TransferEngine transfers,
        IApplicationActivityGate activityGate, DialogService dialogs, LocalizationService texts)
    {
        _updater = updater;
        _state = state;
        _lifetime = lifetime;
        _transfers = transfers;
        _activityGate = activityGate;
        _dialogs = dialogs;
        _texts = texts;
    }

    public event EventHandler? Changed;
    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public string? Version { get; private set; }
    public string? Diagnostic { get; private set; }
    public int Progress { get; private set; }
    public bool IsBusy => Status is UpdateStatus.Checking or UpdateStatus.Downloading;
    public bool CanDownload => Status == UpdateStatus.Available;
    public bool CanRestart => Status == UpdateStatus.Downloaded;

    public async Task CheckOnStartupAsync()
    {
        RefreshLocalState();
        if (!_state.Preferences.CheckForUpdates || Status != UpdateStatus.Idle)
            return;
        // Never delay the first window, load credentials or transfer restoration.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await CheckAsync();
    }

    public void RefreshLocalState()
    {
        if (IsBusy) return;
        Update(_updater.GetLocalState());
    }

    public async Task CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (_updater.HasPendingUpdate)
            {
                Update(_updater.GetLocalState());
                return;
            }
            SetStatus(UpdateStatus.Checking);
            var result = await _updater.CheckAsync();
            Update(result);
            if (result.Status == UpdateStatus.Available &&
                _state.Preferences.DownloadUpdatesInBackground)
                await DownloadCoreAsync();
        }
        catch (Exception ex)
        {
            Update(new(UpdateStatus.Failed, Message: ex.GetType().Name));
        }
        finally { _gate.Release(); }
    }

    public async Task DownloadAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (Status != UpdateStatus.Available) return;
            await DownloadCoreAsync();
        }
        catch (Exception ex)
        {
            Update(new(UpdateStatus.Failed, Message: ex.GetType().Name));
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadCoreAsync()
    {
        Progress = 0;
        SetStatus(UpdateStatus.Downloading);
        // Progress<T> captures this UI synchronization context.
        var progress = new Progress<int>(value =>
        {
            Progress = Math.Clamp(value, 0, 100);
            Changed?.Invoke(this, EventArgs.Empty);
        });
        Update(await _updater.DownloadAsync(progress));
    }

    public void RequestRestart()
    {
        if (Status != UpdateStatus.Downloaded || !_updater.HasPendingUpdate)
        {
            RefreshLocalState();
            return;
        }

        var window = System.Windows.Application.Current?.MainWindow;
        // Include paused/queued jobs; these are not counted by HasActiveTransfers.
        if (_transfers.UnfinishedCount > 0)
        {
            _dialogs.ShowChoice(window, new DialogRequest
            {
                Title = _texts["Update_TransfersTitle"],
                Message = _texts["Update_TransfersMessage"],
                PrimaryText = _texts["Dialog_Ok"]
            });
            return;
        }

        if (_dialogs.ShowChoice(window, new DialogRequest
        {
            Title = _texts["Update_RestartTitle"],
            Message = _texts["Update_RestartMessage"],
            PrimaryText = _texts["Update_RestartNow"],
            SecondaryText = _texts["Update_Later"]
        }) != DialogChoice.Primary)
            return;

        IDisposable? updateLease;
        try
        {
            // An exclusive lease proves that no CLI process is still using current/.
            updateLease = _activityGate.TryAcquireUpdateLease();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Queue("update-coordination", ex);
            updateLease = null;
        }

        if (updateLease is null)
        {
            _dialogs.ShowChoice(window, new DialogRequest
            {
                Title = _texts["Update_TransfersTitle"],
                Message = _texts["Update_TransfersMessage"],
                PrimaryText = _texts["Dialog_Ok"]
            });
            return;
        }

        if (!_lifetime.RequestUpdateRestart(_updater, updateLease))
        {
            _dialogs.ShowChoice(window, new DialogRequest
            {
                Title = _texts["Update_TransfersTitle"],
                Message = _texts["Update_TransfersMessage"],
                PrimaryText = _texts["Dialog_Ok"]
            });
        }
    }

    private void Update(UpdateCheckResult result)
    {
        Status = result.Status;
        Version = result.Version;
        Diagnostic = result.Status == UpdateStatus.Failed ? result.Message : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(UpdateStatus status)
    {
        Status = status;
        Diagnostic = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
