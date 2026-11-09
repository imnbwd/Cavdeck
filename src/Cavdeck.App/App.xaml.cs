#if DEBUG
using System.IO;
#endif
using System.Windows;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.App.ViewModels;
using Cavdeck.App.Views;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Features.Transfers;
using Cavdeck.App.Features.Updates;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Places;
using Cavdeck.Infrastructure.Connections;
using Cavdeck.Infrastructure.Configuration;
using Cavdeck.Infrastructure.S3;
using Cavdeck.Infrastructure.DependencyInjection;
using Cavdeck.Infrastructure.Settings;
using Cavdeck.Infrastructure.Places;
using Cavdeck.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Velopack;
using System.Windows.Threading;

namespace Cavdeck.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private ServiceProvider Services => _services ?? throw new InvalidOperationException(
        "Application services have not been initialized.");
    private int _handlingFatal;
    private SingleInstanceCoordinator? _instance;
    private int _activationPending;
    private TrayIconService? _tray;

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                DiagnosticLog.Current.Record("process", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            DiagnosticLog.Current.Record("background-task", eventArgs.Exception);
            eventArgs.SetObserved();
        };
        // Must run before WPF initialization. Never restart automatically:
        // active transfer tasks must be coordinated with the update UX.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            base.OnStartup(e);
#if DEBUG
            var designMode = HasFlag(e, "--design-preview") || HasFlag(e, "--design-snapshot")
                || HasFlag(e, "--demo-snapshot");
#else
            var designMode = false;
#endif
            if (!designMode)
            {
                _instance = new SingleInstanceCoordinator();
                if (!_instance.IsPrimary)
                {
                    if (!await _instance.ActivatePrimaryAsync())
                    {
                        var texts = new LocalizationService();
                        MessageBox.Show(texts["Tray_ActivationFailed"],
                            texts["Brand_Name"], MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    Shutdown();
                    return;
                }
                _instance.Listen(() => Dispatcher.BeginInvoke(() =>
                {
                    if (_tray is null) Interlocked.Exchange(ref _activationPending, 1);
                    else _tray.Restore();
                }));
            }
            FocusCue.Install();
#if DEBUG
            RegisterServices(demoSnapshots: HasFlag(e, "--demo-snapshot"));
#else
            RegisterServices();
#endif

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

#if DEBUG
            if (HasFlag(e, "--demo-snapshot"))
            {
                await DemoScreenshots.CaptureAsync(_services!);
                Shutdown(0);
                return;
            }
#endif

            var appState = Services.GetRequiredService<ApplicationState>();
            var settingsStore = _services.GetRequiredService<JsonSettingsStore>();
            appState.Preferences = await Task.Run(() => settingsStore.LoadAsync());
            appState.Preferences.Normalize();
            await Task.Run(() => Services.GetRequiredService<PlaceLibrary>().LoadAsync());

            var engine = _services.GetRequiredService<TransferEngine>();
            engine.BackgroundFaulted += exception => DiagnosticLog.Current.Queue("background-task", exception);
            engine.SetMaxConcurrent(appState.Preferences.MaxConcurrentTransfers);
            await Task.Run(() => engine.StartAsync());

            _services.GetRequiredService<LocalizationService>()
                .ChangeLanguage(appState.Preferences.Language);
            var theme = _services.GetRequiredService<ThemeService>();
            theme.Apply(appState.Preferences.Theme);
            var connections = _services.GetRequiredService<ConnectionsViewModel>();
            var connectionCatalog = _services.GetRequiredService<JsonConnectionCatalog>();
            var clients = _services.GetRequiredService<S3ClientLeasePool>();
            connections.ConnectionDeleted += (_, id) => clients.Invalidate(id);
            connections.ConnectionUpdated += (_, change) =>
            {
                if (change.RequiresReconnect)
                    clients.Invalidate(change.Current.Id);
            };
            await connections.LoadAsync();
            // Missing metadata may be a first launch or an accidental file loss.
            // Do not prune saved places unless a real connection catalog was read.
            if (!connections.HasLoadError && System.IO.File.Exists(connectionCatalog.FilePath))
            {
                var activeIds = connections.Items.Select(item => item.Id).ToHashSet();
                await Task.Run(() => _services.GetRequiredService<PlaceLibrary>()
                    .PruneConnectionsAsync(activeIds));
            }

#if DEBUG
            if (HasFlag(e, "--design-preview") || HasFlag(e, "--design-snapshot"))
            {
                if (HasFlag(e, "--design-snapshot"))
                {
                    // The shell is shown only to capture chrome. Closing it must not end the process.
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    theme.Apply(AppTheme.Light);
                    var shell = _services.GetRequiredService<ShellWindow>();
                    shell.UseDesignLayout();
                    shell.Show();
                    var directory = Path.Combine(Path.GetTempPath(), "cavdeck-design");
                    DesignCatalogWindow.SaveElement(shell, Path.Combine(directory, "shell-light.png"));
                    theme.Apply(AppTheme.Dark);
                    shell.UpdateLayout();
                    DesignCatalogWindow.SaveElement(shell, Path.Combine(directory, "shell-dark.png"));
                    var shellViewModel = _services.GetRequiredService<ShellViewModel>();
                    shellViewModel.ShowSettingsCommand.Execute(null);
                    theme.Apply(AppTheme.Light);
                    await shellViewModel.Settings.SetThemeAsync("Light");
                    shell.UpdateLayout();
                    DesignCatalogWindow.SaveElement(shell, Path.Combine(directory, "settings-light.png"));
                    await shellViewModel.Settings.SetThemeAsync("Dark");
                    shell.UpdateLayout();
                    DesignCatalogWindow.SaveElement(shell, Path.Combine(directory, "settings-dark.png"));
                    var previewLanguage = appState.Preferences.Language;
                    await shellViewModel.Settings.SetLanguageAsync(AppLanguages.DefaultLanguage);
                    shell.UpdateLayout();
                    DesignCatalogWindow.SaveElement(shell, Path.Combine(directory, "settings-english.png"));
                    await shellViewModel.Settings.SetLanguageAsync(previewLanguage);
                    shellViewModel.CloseActive();
                    shell.WindowState = WindowState.Maximized;
                    shell.UpdateLayout();
                    DesignCatalogWindow.SaveElement(shell, Path.Combine(directory, "shell-max.png"));
                    shell.Close();
                    theme.Apply(AppTheme.Light);
                }

                var catalog = _services.GetRequiredService<DesignCatalogWindow>();
                catalog.EnableSnapshot = HasFlag(e, "--design-snapshot");
                MainWindow = catalog;
                catalog.Show();
                return;
            }

#endif

            var window = _services.GetRequiredService<ShellWindow>();
            MainWindow = window;
            window.RestoreLayout();
            window.Show();
            var lifetime = _services.GetRequiredService<ApplicationLifetimeCoordinator>();
            _tray = _services.GetRequiredService<TrayIconService>();
            lifetime.Attach(window, _tray);
            _tray.Initialize(window);
            var hotkeys = _services.GetRequiredService<GlobalHotkeyService>();
            hotkeys.Initialize(window, _tray.Restore,
                appState.Preferences.GlobalHotkeyEnabled, appState.Preferences.GlobalHotkeyGesture);
            _services.GetRequiredService<ShellViewModel>().Settings.RefreshGlobalHotkeyStatus();
            if (Interlocked.Exchange(ref _activationPending, 0) != 0)
                _tray.Restore();

            ShowConfigurationRecoveryNotice(window, settingsStore, connectionCatalog, connections);
            _services.GetRequiredService<UpdateNoticeService>().Start();
            _ = _services.GetRequiredService<UpdateCoordinator>().CheckOnStartupAsync();
        }
        catch (Exception ex)
        {
            var id = DiagnosticLog.Current.Record("startup", ex);
            var texts = _services?.GetService<LocalizationService>() ?? new LocalizationService();
            MessageBox.Show(string.Format(texts["Diagnostics_StartupFailedMessage"], id,
                    DiagnosticLog.Current.DirectoryPath), texts["Diagnostics_StartupFailedTitle"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
        finally
        {
#if DEBUG
            if (MainWindow is not null && HasFlag(e, "--design-preview"))
                ShutdownMode = ShutdownMode.OnMainWindowClose;
#endif
        }
    }

    private void ShowConfigurationRecoveryNotice(ShellWindow window,
        JsonSettingsStore settings, JsonConnectionCatalog catalog, ConnectionsViewModel connections)
    {
        var texts = _services!.GetRequiredService<LocalizationService>();
        var messages = new List<string>();
        if (settings.LastRecovery is { } settingsRecovery)
        {
            var key = settingsRecovery.Kind == ConfigurationRecoveryKind.BackupRestored
                ? "Config_SettingsBackupRestored" : "Config_SettingsDefaults";
            messages.Add(string.Format(texts[key], settingsRecovery.PreservedPath));
        }

        var places = Services.GetRequiredService<PlaceLibrary>();
        var missingCatalogWithSavedPlaces = !System.IO.File.Exists(catalog.FilePath) &&
            !System.IO.File.Exists(catalog.FilePath + ".bak") &&
            (places.Favorites.Count > 0 || places.Recent.Count > 0);
        if (connections.HasLoadError || missingCatalogWithSavedPlaces)
        {
            // Missing metadata alongside saved locations is not a fresh installation.
            messages.Add(string.Format(texts["Config_ConnectionsUnavailable"], catalog.FilePath));
        }
        else if (catalog.LastRecovery is { } connectionRecovery)
        {
            messages.Add(string.Format(texts["Config_ConnectionsBackupRestored"],
                connectionRecovery.PreservedPath));
        }

        if (messages.Count == 0)
            return;

        MessageBox.Show(window, string.Join(Environment.NewLine + Environment.NewLine, messages),
            texts["Config_RecoveryTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RegisterServices(bool demoSnapshots = false)
    {
        var registrations = new ServiceCollection();
        registrations.AddCavdeckCore();
        registrations.AddSingleton<ApplicationState>();
        registrations.AddSingleton<JsonSettingsStore>();
        registrations.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<JsonSettingsStore>());
        registrations.AddSingleton<IPlaceStore, JsonPlaceStore>();
        registrations.AddSingleton<PlaceLibrary>();
        registrations.AddSingleton<ExplorerClipboard>();
        registrations.AddSingleton<Cavdeck.Application.Preview.PreviewService>();
#if DEBUG
        if (demoSnapshots)
            registrations.Replace(ServiceDescriptor.Singleton<ITransferJournal, MemoryTransferJournal>());
#endif
        registrations.AddSingleton<ITransferActivity>(sp => sp.GetRequiredService<TransferEngine>());
        registrations.AddSingleton<ConnectionsViewModel>();
        registrations.AddTransient<ExplorerViewModel>();
        registrations.AddTransient<ConnectionEditorViewModel>();
        registrations.AddSingleton<Func<ExplorerViewModel>>(sp => () => sp.GetRequiredService<ExplorerViewModel>());
        registrations.AddSingleton<Func<ConnectionEditorViewModel>>(sp => () => sp.GetRequiredService<ConnectionEditorViewModel>());
        registrations.AddSingleton<TransfersViewModel>();
        registrations.AddSingleton<LocalizationService>();
        registrations.AddSingleton<ThemeService>();
        registrations.AddSingleton<DialogService>();
        registrations.AddSingleton<ToastService>();
        registrations.AddSingleton<ApplicationLifetimeCoordinator>();
        registrations.AddSingleton<TrayIconService>();
        registrations.AddSingleton<GlobalHotkeyService>();
#if DEBUG
        registrations.AddTransient<DesignCatalogWindow>();
#endif
        registrations.AddSingleton<IUpdateService, VelopackUpdateService>();
        registrations.AddSingleton<UpdateCoordinator>();
        registrations.AddSingleton<UpdateNoticeService>();
        registrations.AddSingleton<IUpdateCoordinator>(sp => sp.GetRequiredService<UpdateCoordinator>());
        registrations.AddSingleton<ShellViewModel>();
        registrations.AddSingleton<ShellWindow>();
        _services = registrations.BuildServiceProvider();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var id = DiagnosticLog.Current.Record("ui", e.Exception);
        if (Interlocked.Exchange(ref _handlingFatal, 1) != 0)
            return;
        e.Handled = true;
        try
        {
            var texts = _services?.GetService<LocalizationService>() ?? new LocalizationService();
            var message = string.Format(texts["Diagnostics_FatalMessage"], id);
            MessageBox.Show(message, texts["Diagnostics_FatalTitle"],
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _services?.GetService<ApplicationLifetimeCoordinator>()?.SystemExit();
            Shutdown(-1);
        }
    }

#if DEBUG
    private static bool HasFlag(StartupEventArgs e, string flag) =>
        e.Args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
#endif

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); }
        catch (Exception ex) { DiagnosticLog.Current.Queue("tray", ex); }
        try { _instance?.Dispose(); }
        catch (Exception ex) { DiagnosticLog.Current.Queue("instance-activation", ex); }
        try { _services?.Dispose(); }
        catch (Exception ex) { DiagnosticLog.Current.Queue("window-close", ex); }
        finally { base.OnExit(e); }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _services?.GetService<ApplicationLifetimeCoordinator>()?.SystemExit();
        base.OnSessionEnding(e);
    }
}
