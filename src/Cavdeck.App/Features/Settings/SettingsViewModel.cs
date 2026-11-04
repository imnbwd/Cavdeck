using System.Windows.Input;
using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Features.Updates;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;

namespace Cavdeck.App.Features.Settings;

public sealed class LanguageOption(string code, string displayName) : ViewModelBase
{
    private string _displayName = displayName;

    public string Code { get; } = code;
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }
}

public sealed class SettingsViewModel : ViewModelBase
{
    private const int AboutTabIndex = 4;
    private int _selectedTabIndex;
    private readonly ApplicationState _state;
    private readonly Func<Task> _save;
    private readonly IUpdateCoordinator _updates;
    private readonly ThemeService _theme;
    private readonly Action<int>? _applyConcurrency;
    private readonly GlobalHotkeyService? _hotkeys;
    private readonly ToastService? _toasts;
    private bool _hotkeySaving;
    private bool _capturingHotkey;
    private string? _hotkeyErrorKey;
    private string? _saveError;
    private string? _diagnosticStatus;
    private bool _diagnosticBusy;
    private bool _resourceOpenFailed;

    public SettingsViewModel(ApplicationState state, Func<Task> save, IUpdateCoordinator updates,
        ThemeService theme, LocalizationService texts, Action<int>? applyConcurrency = null,
        GlobalHotkeyService? hotkeys = null, ToastService? toasts = null)
    {
        _state = state;
        _save = save;
        _updates = updates;
        _theme = theme;
        _applyConcurrency = applyConcurrency;
        _hotkeys = hotkeys;
        _toasts = toasts;
        if (_hotkeys is not null) _hotkeys.Changed += (_, _) => RefreshGlobalHotkeyStatus();
        Texts = texts;
        LanguageOptions =
        [
            new(AppLanguages.System, Texts["Settings_FollowSystem"]),
            .. AppLanguages.Supported.Select(language =>
                new LanguageOption(language.Code, language.NativeName))
        ];
        CheckUpdatesCommand = CreateAsyncCommand(_ => _updates.CheckAsync());
        DownloadUpdateCommand = CreateAsyncCommand(_ => _updates.DownloadAsync());
        RestartUpdateCommand = new RelayCommand(_ => _updates.RequestRestart());
        _updates.Changed += (_, _) => RefreshUpdateState();
        CopyDiagnosticCommand = CreateAsyncCommand(_ => CopyDiagnosticAsync());
        OpenLogsCommand = CreateAsyncCommand(_ => OpenLogsAsync());
        OpenProjectPageCommand = new RelayCommand(_ => OpenExternalResource(AboutResource.ProjectPage));
        OpenDocumentationCommand = new RelayCommand(_ => OpenExternalResource(AboutResource.Documentation));
        ReportIssueCommand = new RelayCommand(_ => OpenExternalResource(AboutResource.ReportIssue));
        OpenLicenseCommand = new RelayCommand(_ => OpenExternalResource(AboutResource.License));
        ChooseDownloadDirectoryCommand = CreateAsyncCommand(_ => ChooseDownloadDirectoryAsync());
        _state.ExplorerDisplayChanged += (_, _) => RefreshExplorerChoices();
        Texts.PropertyChanged += (_, _) => RefreshLabels();
        _theme.Changed += (_, _) => OnPropertyChanged(nameof(ThemeDetail));
    }

    private AsyncRelayCommand CreateAsyncCommand(Func<object?, Task> action) =>
        new(action, ReportUnexpectedUiError);

    private void Observe(Func<Task> operation) =>
        _ = UiAsyncTask.RunAsync(operation, ReportUnexpectedUiError);

    private void ReportUnexpectedUiError(Exception exception)
    {
        if (_toasts is null)
        {
            ShowSaveError();
            return;
        }
        var title = Texts["Ui_ActionFailed"];
        if (_toasts.Items.Any(item => !item.IsClosing && item.Title == title))
            return;
        _toasts.Show(new ToastRequest { Title = title, Tone = ToastTone.Error });
    }

    public LocalizationService Texts { get; }
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (_selectedTabIndex == value) return;
            _selectedTabIndex = value;
            OnPropertyChanged();
        }
    }

    public void ShowUpdates() => SelectedTabIndex = AboutTabIndex;

    public ICommand CheckUpdatesCommand { get; }
    public ICommand DownloadUpdateCommand { get; }
    public ICommand RestartUpdateCommand { get; }
    public bool CanCheckUpdates => !_updates.IsBusy;
    public bool CanDownloadUpdate => _updates.CanDownload;
    public bool CanRestartUpdate => _updates.CanRestart;
    public bool AutomaticUpdateChecks
    {
        get => _state.Preferences.CheckForUpdates;
        set => Observe(() => SaveChoiceAsync(_state.Preferences.CheckForUpdates, value,
            choice => _state.Preferences.CheckForUpdates = choice, nameof(AutomaticUpdateChecks)));
    }
    public bool BackgroundUpdateDownloads
    {
        get => _state.Preferences.DownloadUpdatesInBackground;
        set => Observe(() => SaveChoiceAsync(_state.Preferences.DownloadUpdatesInBackground, value,
            choice => _state.Preferences.DownloadUpdatesInBackground = choice, nameof(BackgroundUpdateDownloads)));
    }
    public ICommand CopyDiagnosticCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenProjectPageCommand { get; }
    public ICommand OpenDocumentationCommand { get; }
    public ICommand ReportIssueCommand { get; }
    public ICommand OpenLicenseCommand { get; }
    public string? ResourceStatus => _resourceOpenFailed ? Texts["About_OpenLinkFailed"] : null;
    public ICommand ChooseDownloadDirectoryCommand { get; }
    public IReadOnlyList<int> ConcurrencyChoices { get; } = [1, 2, 3, 4, 6, 8];
    public IReadOnlyList<ShortcutGroup> ShortcutGroups => ShortcutCatalog.Create(Texts);
    public bool GlobalHotkeyEnabled
    {
        get => _state.Preferences.GlobalHotkeyEnabled;
        set => Observe(() => ConfigureGlobalHotkeyAsync(value, _state.Preferences.GlobalHotkeyGesture));
    }
    public bool IsCapturingGlobalHotkey => _capturingHotkey;
    public bool CanEditGlobalHotkey => !_hotkeySaving;
    public bool CanToggleGlobalHotkey => !_hotkeySaving && !_capturingHotkey;
    public string GlobalHotkeyButtonLabel => _capturingHotkey
        ? Texts["Shortcuts_Recording"] : _state.Preferences.GlobalHotkeyGesture.Replace("+", " + ");
    private bool IsConfiguredHotkeyActive =>
        _hotkeys?.IsActive == true &&
        GlobalHotkeyGesture.TryParse(_state.Preferences.GlobalHotkeyGesture, out var parsed) &&
        _hotkeys.RegisteredGesture == parsed.Text;
    public bool HasGlobalHotkeyWarning =>
        _hotkeyErrorKey is not null ||
        (_state.Preferences.GlobalHotkeyEnabled && !IsConfiguredHotkeyActive);
    public string GlobalHotkeyStatus => _hotkeyErrorKey is not null ? Texts[_hotkeyErrorKey]
        : !_state.Preferences.GlobalHotkeyEnabled ? Texts["Shortcuts_StatusDisabled"]
        : IsConfiguredHotkeyActive ? Texts["Shortcuts_StatusActive"]
        : Texts["Shortcuts_StatusUnavailable"];

    public void BeginGlobalHotkeyCapture()
    {
        if (_hotkeySaving) return;
        _capturingHotkey = true;
        _hotkeyErrorKey = null;
        RefreshGlobalHotkeyStatus();
    }

    public void CancelGlobalHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        RefreshGlobalHotkeyStatus();
    }

    public void ReportInvalidGlobalHotkey()
    {
        _hotkeyErrorKey = "Shortcuts_InvalidGesture";
        RefreshGlobalHotkeyStatus();
    }

    public Task SetGlobalHotkeyGestureAsync(string gesture) =>
        ConfigureGlobalHotkeyAsync(_state.Preferences.GlobalHotkeyEnabled, gesture);

    public void RefreshGlobalHotkeyStatus()
    {
        OnPropertyChanged(nameof(GlobalHotkeyEnabled));
        OnPropertyChanged(nameof(GlobalHotkeyButtonLabel));
        OnPropertyChanged(nameof(GlobalHotkeyStatus));
        OnPropertyChanged(nameof(HasGlobalHotkeyWarning));
        OnPropertyChanged(nameof(CanToggleGlobalHotkey));
        OnPropertyChanged(nameof(CanEditGlobalHotkey));
        OnPropertyChanged(nameof(IsCapturingGlobalHotkey));
    }

    public async Task ConfigureGlobalHotkeyAsync(bool enabled, string gesture)
    {
        if (_hotkeySaving) return;
        if (!GlobalHotkeyGesture.TryParse(gesture, out var parsed))
        {
            _hotkeyErrorKey = "Shortcuts_InvalidGesture";
            RefreshGlobalHotkeyStatus();
            return;
        }
        var current = _state.Preferences;
        var previousEnabled = current.GlobalHotkeyEnabled;
        var previousGesture = current.GlobalHotkeyGesture;
        if (enabled == previousEnabled && gesture == previousGesture &&
            (!enabled || _hotkeys?.IsActive == true)) return;

        _hotkeySaving = true;
        _hotkeyErrorKey = null;
        RefreshGlobalHotkeyStatus();
        try
        {
            var result = _hotkeys?.TryApply(enabled, parsed.Text) ??
                (enabled ? GlobalHotkeyResult.Unavailable : GlobalHotkeyResult.Success);
            if (result != GlobalHotkeyResult.Success)
            {
                _hotkeyErrorKey = result switch
                {
                    GlobalHotkeyResult.Invalid => "Shortcuts_InvalidGesture",
                    GlobalHotkeyResult.Conflict => "Shortcuts_RegistrationConflict",
                    _ => "Shortcuts_StatusUnavailable"
                };
                return;
            }

            ClearSaveError();
            current.GlobalHotkeyEnabled = enabled;
            current.GlobalHotkeyGesture = parsed.Text;
            RefreshGlobalHotkeyStatus();
            await _save();
        }
        catch (Exception)
        {
            current.GlobalHotkeyEnabled = previousEnabled;
            current.GlobalHotkeyGesture = previousGesture;
            _hotkeys?.TryApply(previousEnabled, previousGesture);
            ShowSaveError();
        }
        finally
        {
            _hotkeySaving = false;
            RefreshGlobalHotkeyStatus();
        }
    }

    public string? DiagnosticStatus => _diagnosticStatus;
    public bool IsDiagnosticBusy => _diagnosticBusy;
    public string ThemeChoice
    {
        get => _state.Preferences.Theme.ToString();
        set => Observe(() => SetThemeAsync(value));
    }

    public string LanguageChoice
    {
        get => _state.Preferences.Language;
        set => Observe(() => SetLanguageAsync(value));
    }
    public string CloseBehaviorChoice
    {
        get => _state.Preferences.WindowCloseBehavior.ToString();
        set => Observe(() => SetCloseBehaviorAsync(value));
    }
    public bool RestorePreviousTabs
    {
        get => _state.Preferences.RestorePreviousTabs;
        set => Observe(() => SetRestorePreviousTabsAsync(value));
    }
    public bool NotifyTransferCompletion
    {
        get => _state.Preferences.NotifyTransferCompletion;
        set => Observe(() => SetNotifyTransferCompletionAsync(value));
    }


    public string BrowserViewChoice
    {
        get => _state.Preferences.ObjectBrowser.ToString();
        set => Observe(() => SetBrowserViewAsync(value));
    }
    public string GridDensityChoice
    {
        get => _state.Preferences.GridDensity.ToString();
        set => Observe(() => SetGridDensityAsync(value));
    }
    public bool ShowDetailsPane
    {
        get => _state.Preferences.ShowDetailsPane;
        set => Observe(() => SetShowDetailsPaneAsync(value));
    }
    public int Concurrency
    {
        get => _state.Preferences.MaxConcurrentTransfers;
        set => Observe(() => SetConcurrencyAsync(value));
    }
    public string DownloadDestinationChoice
    {
        get => _state.Preferences.DownloadDestination.ToString();
        set => Observe(() => SetDownloadDestinationAsync(value));
    }
    public bool HasDownloadDirectory => _state.Preferences.AvailableDownloadDirectory is not null;
    public string DownloadDirectoryDetail => string.IsNullOrWhiteSpace(_state.Preferences.DefaultDownloadDirectory)
        ? Texts["Settings_DownloadNotSelected"] : _state.Preferences.DefaultDownloadDirectory;
    public string TransferConflictChoice
    {
        get => _state.Preferences.TransferConflict.ToString();
        set => Observe(() => SetTransferConflictAsync(value));
    }

    public void RefreshConcurrency() => OnPropertyChanged(nameof(Concurrency));
    private void RefreshExplorerChoices()
    {
        OnPropertyChanged(nameof(TransferConflictChoice));
        OnPropertyChanged(nameof(BrowserViewChoice));
        OnPropertyChanged(nameof(GridDensityChoice));
        OnPropertyChanged(nameof(ShowDetailsPane));
    }

    private async Task SaveChoiceAsync<T>(T previous, T selected, Action<T> apply,
        string propertyName, bool notifyExplorer = false)
    {
        if (EqualityComparer<T>.Default.Equals(previous, selected)) return;
        ClearSaveError();
        try
        {
            apply(selected);
            OnPropertyChanged(propertyName);
            if (notifyExplorer) _state.NotifyExplorerDisplayChanged();
            await _save();
        }
        catch (Exception)
        {
            apply(previous);
            OnPropertyChanged(propertyName);
            if (notifyExplorer) _state.NotifyExplorerDisplayChanged();
            ShowSaveError();
        }
    }

    public Task SetBrowserViewAsync(string? value) =>
        Enum.TryParse<ObjectBrowserView>(value, true, out var selected) && Enum.IsDefined(selected)
            ? SaveChoiceAsync(_state.Preferences.ObjectBrowser, selected,
                v => _state.Preferences.ObjectBrowser = v, nameof(BrowserViewChoice), true)
            : Task.CompletedTask;

    public Task SetGridDensityAsync(string? value) =>
        Enum.TryParse<ObjectGridDensity>(value, true, out var selected) && Enum.IsDefined(selected)
            ? SaveChoiceAsync(_state.Preferences.GridDensity, selected,
                v => _state.Preferences.GridDensity = v, nameof(GridDensityChoice), true)
            : Task.CompletedTask;

    public Task SetRestorePreviousTabsAsync(bool value) =>
        SaveChoiceAsync(_state.Preferences.RestorePreviousTabs, value,
            v => _state.Preferences.RestorePreviousTabs = v, nameof(RestorePreviousTabs));

    public Task SetNotifyTransferCompletionAsync(bool value) =>
        SaveChoiceAsync(_state.Preferences.NotifyTransferCompletion, value,
            v => _state.Preferences.NotifyTransferCompletion = v, nameof(NotifyTransferCompletion));

    public Task SetShowDetailsPaneAsync(bool value) =>
        SaveChoiceAsync(_state.Preferences.ShowDetailsPane, value,
            v => _state.Preferences.ShowDetailsPane = v, nameof(ShowDetailsPane), true);

    public Task SetConcurrencyAsync(int value)
    {
        if (!ConcurrencyChoices.Contains(value)) return Task.CompletedTask;
        return SaveChoiceAsync(_state.Preferences.MaxConcurrentTransfers, value,
            v => { _state.Preferences.MaxConcurrentTransfers = v; _applyConcurrency?.Invoke(v); },
            nameof(Concurrency));
    }

    public Task SetTransferConflictAsync(string? value) =>
        Enum.TryParse<TransferConflictPreference>(value, true, out var selected) && Enum.IsDefined(selected)
            ? SaveChoiceAsync(_state.Preferences.TransferConflict, selected,
                v => _state.Preferences.TransferConflict = v, nameof(TransferConflictChoice))
            : Task.CompletedTask;

    public Task SetDownloadDestinationAsync(string? value)
    {
        if (!Enum.TryParse<DownloadDestinationPreference>(value, true, out var selected)
            || !Enum.IsDefined(selected)) return Task.CompletedTask;
        if (selected == DownloadDestinationPreference.DefaultFolder && !HasDownloadDirectory)
            return Task.CompletedTask;
        return SaveChoiceAsync(_state.Preferences.DownloadDestination, selected,
            v => _state.Preferences.DownloadDestination = v, nameof(DownloadDestinationChoice));
    }

    public async Task SetDownloadDirectoryAsync(string? directory)
    {
        string selected;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)
                || !Directory.Exists(directory))
                throw new ArgumentException("Invalid directory");
            selected = Path.GetFullPath(directory);
        }
        catch (Exception)
        {
            _saveError = Texts["Settings_InvalidDownloadDirectory"];
            OnPropertyChanged(nameof(SaveError));
            return;
        }
        var previousPath = _state.Preferences.DefaultDownloadDirectory;
        var previousMode = _state.Preferences.DownloadDestination;
        ClearSaveError();
        try
        {
            _state.Preferences.DefaultDownloadDirectory = selected;
            _state.Preferences.DownloadDestination = DownloadDestinationPreference.DefaultFolder;
            NotifyDownloadChanged();
            await _save();
        }
        catch (Exception)
        {
            _state.Preferences.DefaultDownloadDirectory = previousPath;
            _state.Preferences.DownloadDestination = previousMode;
            NotifyDownloadChanged();
            ShowSaveError();
        }
    }

    private Task ChooseDownloadDirectoryAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Texts["Transfer_PickDestination"]
        };
        if (_state.Preferences.AvailableDownloadDirectory is { } current)
            dialog.InitialDirectory = current;
        return dialog.ShowDialog() == true
            ? SetDownloadDirectoryAsync(dialog.FolderName) : Task.CompletedTask;
    }

    private void NotifyDownloadChanged()
    {
        OnPropertyChanged(nameof(HasDownloadDirectory));
        OnPropertyChanged(nameof(DownloadDirectoryDetail));
        OnPropertyChanged(nameof(DownloadDestinationChoice));
    }

    public async Task SetCloseBehaviorAsync(string? value)
    {
        if (!Enum.TryParse<WindowCloseBehavior>(value, true, out var selected) ||
            !Enum.IsDefined(selected) || selected == _state.Preferences.WindowCloseBehavior)
            return;
        var previous = _state.Preferences.WindowCloseBehavior;
        ClearSaveError();
        try
        {
            _state.Preferences.WindowCloseBehavior = selected;
            OnPropertyChanged(nameof(CloseBehaviorChoice));
            await _save();
        }
        catch (Exception)
        {
            if (_state.Preferences.WindowCloseBehavior == selected)
            {
                _state.Preferences.WindowCloseBehavior = previous;
                OnPropertyChanged(nameof(CloseBehaviorChoice));
            }
            ShowSaveError();
        }
    }
    public string ThemeDetail => string.Format(Texts["Settings_CurrentValue"],
        Texts[_theme.IsDark ? "Theme_Dark" : "Theme_Light"]);
    public string LanguageDetail => string.Format(Texts["Settings_CurrentValue"],
        AppLanguages.Find(Texts.CurrentLanguage).NativeName);
    public string UpdateStatusText => _updates.Status switch
    {
        UpdateStatus.Checking => Texts["Update_Checking"],
        UpdateStatus.Downloading => string.Format(Texts["Update_Downloading"], _updates.Progress),
        UpdateStatus.Available => string.Format(Texts["Update_Available"], _updates.Version ?? ""),
        UpdateStatus.Downloaded => string.Format(Texts["Update_Ready"], _updates.Version ?? ""),
        UpdateStatus.UpToDate => Texts["Update_UpToDate"],
        UpdateStatus.NotInstalled => Texts["Update_NotInstalled"],
        UpdateStatus.NotConfigured => Texts["Update_NotConfigured"],
        UpdateStatus.Failed => Texts["Update_Failed"],
        _ => Texts["Update_Idle"]
    };
    public string VersionText => string.Format(Texts["About_VersionFormat"],
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ??
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "—");
    public string? UpdateDiagnostic => _updates.Diagnostic;
    public string? SaveError => _saveError;

    public async Task SetThemeAsync(string? value)
    {
        if (!Enum.TryParse<AppTheme>(value, true, out var selected) ||
            selected == _state.Preferences.Theme)
            return;

        var previous = _state.Preferences.Theme;
        ClearSaveError();
        try
        {
            _theme.Apply(selected);
            _state.Preferences.Theme = selected;
            OnPropertyChanged(nameof(ThemeChoice));
            await _save();
        }
        catch (Exception)
        {
            if (_state.Preferences.Theme == selected)
            {
                _state.Preferences.Theme = previous;
                _theme.Apply(previous);
                OnPropertyChanged(nameof(ThemeChoice));
            }
            ShowSaveError();
        }
    }

    public async Task SetLanguageAsync(string? value)
    {
        if (!AppLanguages.IsSupportedSelection(value)) return;
        value = AppLanguages.NormalizeSelection(value);
        if (value == _state.Preferences.Language) return;

        var previous = _state.Preferences.Language;
        ClearSaveError();
        try
        {
            Texts.ChangeLanguage(value);
            _state.Preferences.Language = value;
            OnPropertyChanged(nameof(LanguageChoice));
            await _save();
        }
        catch (Exception)
        {
            if (_state.Preferences.Language == value)
            {
                _state.Preferences.Language = previous;
                Texts.ChangeLanguage(previous);
                OnPropertyChanged(nameof(LanguageChoice));
            }
            ShowSaveError();
        }
    }

    private void RefreshUpdateState()
    {
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(UpdateDiagnostic));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanRestartUpdate));
    }

    private void RefreshLabels()
    {
        LanguageOptions[0].DisplayName = Texts["Settings_FollowSystem"];
        OnPropertyChanged(nameof(ThemeDetail));
        OnPropertyChanged(nameof(LanguageDetail));
        OnPropertyChanged(nameof(DownloadDirectoryDetail));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(ResourceStatus));
        OnPropertyChanged(nameof(ShortcutGroups));
        RefreshGlobalHotkeyStatus();
        if (_saveError is not null)
            ShowSaveError();
    }

    private void OpenExternalResource(AboutResource resource)
    {
        try
        {
            // Known HTTPS URLs only; never open untrusted object paths or presigned URLs here.
            var url = AboutResourceLinks.GetUri(resource);
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            _resourceOpenFailed = false;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue("other", exception);
            _resourceOpenFailed = true;
        }
        OnPropertyChanged(nameof(ResourceStatus));
    }

    private async Task CopyDiagnosticAsync()
    {
        if (_diagnosticBusy) return;
        SetDiagnosticBusy(true);
        SetDiagnosticStatus(Texts["Diagnostics_Preparing"]);
        try
        {
            var report = await Task.Run(DiagnosticLog.Current.CreateReport);
            Clipboard.SetText(report);
            SetDiagnosticStatus(Texts["Diagnostics_Copied"]);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue("copy-diagnostic", exception);
            SetDiagnosticStatus(Texts["Diagnostics_CopyFailed"]);
        }
        finally { SetDiagnosticBusy(false); }
    }

    private async Task OpenLogsAsync()
    {
        if (_diagnosticBusy) return;
        SetDiagnosticBusy(true);
        SetDiagnosticStatus(Texts["Diagnostics_Opening"]);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(DiagnosticLog.Current.DirectoryPath);
                Process.Start(new ProcessStartInfo(DiagnosticLog.Current.DirectoryPath) { UseShellExecute = true });
            });
            SetDiagnosticStatus(null);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue("open-logs", exception);
            SetDiagnosticStatus(Texts["Diagnostics_OpenFailed"]);
        }
        finally { SetDiagnosticBusy(false); }
    }

    private void SetDiagnosticStatus(string? value)
    {
        _diagnosticStatus = value;
        OnPropertyChanged(nameof(DiagnosticStatus));
    }

    private void SetDiagnosticBusy(bool value)
    {
        _diagnosticBusy = value;
        OnPropertyChanged(nameof(IsDiagnosticBusy));
    }

    private void ClearSaveError()
    {
        _saveError = null;
        OnPropertyChanged(nameof(SaveError));
    }

    private void ShowSaveError()
    {
        _saveError = Texts["Settings_SaveFailed"];
        OnPropertyChanged(nameof(SaveError));
    }
}
