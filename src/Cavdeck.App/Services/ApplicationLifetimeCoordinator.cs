using System.Windows;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Localization;
using Cavdeck.App.Views;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Transfers;

namespace Cavdeck.App.Services;

public sealed class ApplicationLifetimeCoordinator
{
    private readonly ApplicationState _state;
    private readonly TransferEngine _engine;
    private readonly DialogService _dialogs;
    private readonly LocalizationService _texts;
    private ShellWindow? _window;
    private TrayIconService? _tray;
    private bool _exiting;

    public ApplicationLifetimeCoordinator(ApplicationState state, TransferEngine engine,
        DialogService dialogs, LocalizationService texts)
    {
        _state = state;
        _engine = engine;
        _dialogs = dialogs;
        _texts = texts;
    }

    public void Attach(ShellWindow window, TrayIconService tray)
    {
        _window = window;
        _tray = tray;
    }

    public bool HandleWindowClose()
    {
        if (_exiting) return true;
        if (_window is null) return true;
        var summary = TransferTraySummary.FromJobs(_engine.Jobs);
        var decision = WindowClosePolicy.Decide(_state.Preferences.WindowCloseBehavior,
            summary.Unfinished, _tray?.IsAvailable == true);
        if (decision == WindowCloseDecision.Hide)
        {
            TryHide();
            return true;
        }
        if (_state.Preferences.WindowCloseBehavior == WindowCloseBehavior.MinimizeToTray &&
            _tray?.IsAvailable != true)
            MessageBox.Show(_window, _texts["Tray_Unavailable"], _texts["Brand_Name"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
        if (decision == WindowCloseDecision.Confirm)
        {
            var choice = _dialogs.ShowChoice(_window, new DialogRequest
            {
                Title = _texts["Tray_UnfinishedTitle"],
                Message = string.Format(_texts["Tray_UnfinishedMessage"],
                    summary.Running, summary.Waiting, summary.Paused),
                PrimaryText = _texts["Tray_KeepBackground"],
                SecondaryText = _texts["Tray_ExitApplication"],
                TertiaryText = _texts["Dialog_Cancel"]
            });
            if (choice == DialogChoice.Primary) TryHide();
            else if (choice == DialogChoice.Secondary) BeginExit();
            return true;
        }
        BeginExit();
        return true;
    }

    public void RequestExplicitExit()
    {
        if (_exiting || _window is null) return;
        var summary = TransferTraySummary.FromJobs(_engine.Jobs);
        if (summary.Unfinished > 0)
            _tray?.Restore();
        if (summary.Unfinished > 0 && _dialogs.ShowChoice(_window, new DialogRequest
            {
                Title = _texts["Tray_UnfinishedTitle"],
                Message = _texts["Tray_ExplicitExitMessage"],
                PrimaryText = _texts["Tray_ExitApplication"],
                SecondaryText = _texts["Dialog_Cancel"],
                Tone = DialogTone.Danger
            }) != DialogChoice.Primary)
            return;
        BeginExit();
    }

    public bool HasUnfinishedTransfers => _engine.UnfinishedCount > 0;

    public void RequestUpdateRestart(IUpdateService updater)
    {
        if (_exiting || _window is null || HasUnfinishedTransfers) return;
        _exiting = true;
        _window.BeginUpdateRestart(updater);
    }

    public void CancelExit() => _exiting = false;

    private bool TryHide()
    {
        if (_tray?.IsAvailable == true && _window is not null)
        {
            try
            {
                _window.HideToTray();
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Queue("tray", ex);
                try { _window.RestoreFromTray(); }
                catch (Exception restoreError) { DiagnosticLog.Current.Queue("tray", restoreError); }
            }
        }
        MessageBox.Show(_window, _texts["Tray_Unavailable"], _texts["Brand_Name"],
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void BeginExit()
    {
        if (_exiting || _window is null) return;
        _exiting = true;
        _window.BeginApplicationExit();
    }

    public void SystemExit()
    {
        _exiting = true;
        try { _tray?.Dispose(); }
        catch (Exception ex) { DiagnosticLog.Current.Queue("tray", ex); }
        _window?.BypassCloseForSystem();
    }

    public async Task<bool> StopServicesAsync()
    {
        var stopped = true;
        try { _tray?.Dispose(); }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Queue("tray", ex);
            stopped = false;
        }
        try { await _engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(7)); }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Queue("window-close", ex);
            stopped = false;
        }
        return stopped;
    }
}
