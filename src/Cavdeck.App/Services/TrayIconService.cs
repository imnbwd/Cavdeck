using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Features.Updates;
using Cavdeck.Application.Abstractions;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.App.Views;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace Cavdeck.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly TransferEngine _engine;
    private readonly LocalizationService _texts;
    private readonly ThemeService _theme;
    private readonly IUpdateCoordinator _updates;
    private readonly ShellViewModel _shell;
    private readonly ApplicationLifetimeCoordinator _lifetime;
    private readonly DateTimeOffset _sessionStarted = DateTimeOffset.UtcNow;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly TrayMenuViewModel _menuModel;
    private ShellWindow? _window;
    private TaskbarIcon? _icon;
    private ContextMenu? _menu;
    private ResourceDictionary? _menuPalette;
    private TransferTraySummary _last;
    private int _refreshPending;
    private bool _disposed;

    public TrayIconService(TransferEngine engine, LocalizationService texts,
        ShellViewModel shell, ApplicationLifetimeCoordinator lifetime, ThemeService theme,
        IUpdateCoordinator updates)
    {
        _engine = engine;
        _texts = texts;
        _theme = theme;
        _updates = updates;
        _shell = shell;
        _lifetime = lifetime;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _menuModel = new TrayMenuViewModel(texts,
            () => Dispatch(Restore),
            () => Dispatch(() => Navigate("transfers")),
            () => Dispatch(() => Navigate("connections")),
            () => Dispatch(() => Navigate("favorites")),
            () => Dispatch(() => Navigate("settings")),
            () => Dispatch(_lifetime.RequestExplicitExit),
            () => Dispatch(OpenUpdateDetails));
    }

    public bool IsAvailable => !_disposed && _icon is { IsCreated: true };
    public bool IsMainWindowVisible => _window is { IsVisible: true, WindowState: not WindowState.Minimized };
    public bool IsViewingUpdateDetails => IsMainWindowVisible && _shell.IsSettingsOpen &&
        _shell.Settings.SelectedTabIndex == 4;

    public void OpenUpdateDetails()
    {
        _shell.Settings.ShowUpdates();
        Navigate("settings");
    }

    // Native notifications are shown only when the window is hidden/minimized.
    // The tray menu retains a discoverable update action if Windows suppresses the balloon.
    public void NotifyUpdate(string title, string message)
    {
        if (!IsAvailable) return;
        try { _icon!.TrayIcon.ShowNotification(title, message, NotificationIcon.Info, sound: false); }
        catch (Exception ex) { DiagnosticLog.Current.Queue("tray", ex); }
    }

    public bool Initialize(ShellWindow window)
    {
        if (_disposed) return false;
        if (_icon is not null) return IsAvailable;
        _window = window;
        try
        {
            var iconSource = TrayIconSource.Load();
            _menu = (ContextMenu)System.Windows.Application.Current.FindResource("TrayContextMenu");
            _menu.DataContext = _menuModel;
            SyncMenuPalette();
            _menu.Opened += OnMenuOpened;
            _icon = new TaskbarIcon
            {
                DataContext = _menuModel,
                IconSource = iconSource,
                ContextMenu = _menu,
                MenuActivation = PopupActivationMode.RightClick,
                LeftClickCommand = _menuModel.OpenCommand,
                DoubleClickCommand = _menuModel.OpenCommand
            };
            _icon.ForceCreate(enablesEfficiencyMode: false);
            if (!_icon.IsCreated) throw new InvalidOperationException("Tray icon registration failed.");
            _engine.Changed += OnTransferChanged;
            _updates.Changed += OnUpdatesChanged;
            _texts.PropertyChanged += OnTextsChanged;
            _theme.Changed += OnThemeChanged;
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Queue("tray", ex);
            Dispose();
            return false;
        }
    }

    // The tray ContextMenu is hosted in a popup separate from ShellWindow.
    // Give it an explicit, replaceable palette so DynamicResource stays correct
    // even when no application window is visible.
    private void SyncMenuPalette()
    {
        if (_menu is null || _disposed) return;
        _menuPalette = ReplaceMenuPalette(_menu, _menuPalette, _theme.CreateColorsDictionary());
    }

    internal static ResourceDictionary ReplaceMenuPalette(
        ContextMenu menu, ResourceDictionary? previous, ResourceDictionary replacement)
    {
        var dictionaries = menu.Resources.MergedDictionaries;
        var index = previous is null ? -1 : dictionaries.IndexOf(previous);
        if (index >= 0)
            dictionaries[index] = replacement;
        else
            dictionaries.Add(replacement);
        return replacement;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
            SyncMenuPalette();
        else
            Dispatch(SyncMenuPalette);
    }

    private void OnMenuOpened(object? sender, RoutedEventArgs e) =>
        _theme.RefreshSystemTheme();

    private void Dispatch(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            try { action(); }
            catch (Exception ex) { DiagnosticLog.Current.Queue("tray", ex); }
        });
    }

    public void Restore()
    {
        if (_window is null) return;
        var dialog = _window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsVisible);
        _window.RestoreFromTray();
        dialog?.Activate();
    }

    private void Navigate(string page)
    {
        Restore();
        _shell.Navigate(page);
    }

    private void OnTransferChanged(TransferJob _)
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) != 0) return;
        Dispatch(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            Refresh(changedOnly: true);
        });
    }
    private void OnUpdatesChanged(object? sender, EventArgs e) => Dispatch(() => Refresh());
    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e) => Dispatch(() => Refresh());

    private void Refresh(bool changedOnly = false)
    {
        if (_icon is null) return;
        var current = TransferTraySummary.FromJobs(_engine.Jobs.Where(job =>
            job.State != TransferState.Failed || job.UpdatedAt >= _sessionStarted));
        if (changedOnly && current == _last) return;
        _last = current;
        _menuModel.Refresh(current);
        _menuModel.SetUpdate(_updates.Status, _updates.Version);
        _icon.ToolTipText = _menuModel.Tooltip;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Changed -= OnTransferChanged;
        _updates.Changed -= OnUpdatesChanged;
        _texts.PropertyChanged -= OnTextsChanged;
        _theme.Changed -= OnThemeChanged;
        try { _icon?.Dispose(); }
        catch (Exception ex) { DiagnosticLog.Current.Queue("tray", ex); }
        if (_menu is not null)
        {
            try
            {
                _menu.Opened -= OnMenuOpened;
                _menu.IsOpen = false;
                _menu.DataContext = null;
                if (_menuPalette is not null)
                    _menu.Resources.MergedDictionaries.Remove(_menuPalette);
            }
            catch (Exception ex) { DiagnosticLog.Current.Queue("tray", ex); }
        }
        _icon = null;
        _menu = null;
        _menuPalette = null;
    }
}
