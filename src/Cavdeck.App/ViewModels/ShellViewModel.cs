using System.Collections.ObjectModel;
using System.Windows.Input;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Features.Places;
using Cavdeck.App.Features.Settings;
using Cavdeck.App.Features.Updates;
using Cavdeck.App.Features.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Places;
using Cavdeck.Application.Shell;
using Cavdeck.Application.Connections;

namespace Cavdeck.App.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly ApplicationState _state;
    private readonly ISettingsStore _settingsStore;
    private readonly ToastService _toasts;
    private readonly DialogService _dialogs;
    private readonly PlaceLibrary _places;
    private readonly Func<ExplorerViewModel> _explorers;
    private readonly Func<ConnectionEditorViewModel> _editors;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private ShellSession _session;
    private CancellationTokenSource? _pendingSave;
    private bool _persist = true;
    private double _sidebarWidth;
    private bool _sidebarCollapsed;
    private bool _autoCompactSidebar;
    private bool _temporarySidebarExpanded;
    private string? _statusNote;
    private readonly List<ClosedTab> _closedTabs = [];
    private readonly HashSet<Guid> _pendingCloses = [];
    private readonly Dictionary<Guid, RestorableTabLocation> _unavailableRestores = [];
    private bool _closingOtherTabs;
    private readonly ExplorerActivationLog _activation = new();
    private bool _storageMenuOpen;

    public LocalizationService Texts { get; }
    public ObservableCollection<ShellTabViewModel> Tabs { get; } = [];
    public WindowLayout Layout => _state.Preferences.Layout;
    public SettingsViewModel Settings { get; }

    public ICommand ShowSettingsCommand { get; }
    public ICommand OpenDuplicateCommand { get; }
    public ICommand NewTabCommand { get; }
    public ICommand CloseActiveCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ActivateTabCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
    public ICommand OpenConnectionCommand { get; }
    public ICommand OpenConnectionNewTabCommand { get; }
    public ICommand AddSidebarConnectionCommand { get; }
    public ICommand EditSidebarConnectionCommand { get; }
    public ICommand ToggleStorageMenuCommand { get; }
    public ICommand ShowConnectionsCommand { get; }
    public ICommand ReturnToConnectionListCommand { get; }

    public Guid? ActiveConnectionId =>
        ActivePage == ShellPage.Explorer ? ActiveExplorer?.Place?.Connection.Id : null;

    public bool IsStorageMenuOpen
    {
        get => _storageMenuOpen;
        set => SetProperty(ref _storageMenuOpen, value);
    }

    public void CloseStorageMenu() => IsStorageMenuOpen = false;

    public bool IsSettingsOpen => ActivePage == ShellPage.Settings;
    public bool ShowTabContent => !IsSettingsOpen;
    public ShellPage ActivePage => _session.Active;
    public ShellTabViewModel? ActiveTab =>
        _session.ActiveIndex >= 0 && _session.ActiveIndex < Tabs.Count ? Tabs[_session.ActiveIndex] : null;
    public string PageTitle => Texts[ShellTabViewModel.TitleKey(ActivePage)];
    public string StatusLeft => _statusNote ?? (IsSettingsOpen ? Texts["Settings_Title"] : ActivePage switch
    {
        ShellPage.Explorer => ActiveExplorer?.StatusText ?? Texts["Status_NoItems"],
        ShellPage.Favorites => string.Format(Texts["Places_StatusCount"], FavoritesPage.Items.Count),
                ShellPage.Recent => string.Format(Texts["Places_StatusCount"], RecentPage.Items.Count),
        ShellPage.Connections => string.Format(Texts["Connection_ListSummary"], Connections.Count),
        ShellPage.Transfers => PageTitle,
        _ => $"{PageTitle} · {Texts["Status_NoItems"]}"
    });
    public string StatusRight
    {
        get
        {
            var connections = Connections.Count switch
            {
                0 => Texts["Footer_NoConnections"],
                1 => Texts["Footer_OneConnection"],
                _ => string.Format(Texts["Footer_ConnectionCount"], Connections.Count)
            };
            return $"{Transfers.FooterText} · {connections}";
        }
    }
    public string CloseTip => Texts["Tab_CloseTip"];
    public bool CanAddTab => Tabs.Count < ShellSession.MaxTabs;
    public string NewTabTip => CanAddTab
        ? Texts["Tab_NewTip"]
        : string.Format(Texts["Status_TabLimit"], ShellSession.MaxTabs);
    public bool CanRestoreClosedTab => _closedTabs.Count > 0;
    public string SidebarToggleTip => Texts[EffectiveSidebarCollapsed ? "Sidebar_ExpandTip" : "Sidebar_CollapseTip"];
    public bool ShowConnections => ActivePage == ShellPage.Connections;
    public bool ShowFavorites => ActivePage == ShellPage.Favorites;
    public bool ShowRecent => ActivePage == ShellPage.Recent;
    public bool ShowTransfers => ActivePage == ShellPage.Transfers;
    public bool ShowExplorer => ActivePage == ShellPage.Explorer;
    public bool ShowPageHeading => ActivePage != ShellPage.Explorer && !IsSettingsOpen;
    public bool ShowConnectionBack =>
        !IsSettingsOpen && ActiveTab?.Workspace is ConnectionWorkspace workspace && workspace.Editor.IsOpen;
    // The persisted preference is independent of the narrow-window presentation.
    public bool SidebarCollapsed => _sidebarCollapsed;
    public bool IsAutoCompactSidebar => _autoCompactSidebar;
    public bool EffectiveSidebarCollapsed => SidebarPresentation.IsCollapsed(
        _sidebarCollapsed, _autoCompactSidebar, _temporarySidebarExpanded);
    public double SidebarColumnWidth =>
        EffectiveSidebarCollapsed ? WindowLayout.CollapsedSidebarWidth : _sidebarWidth;

    public TransfersViewModel Transfers { get; }
    public PlacesPageViewModel FavoritesPage { get; }
    public PlacesPageViewModel RecentPage { get; }

    public ShellViewModel(ApplicationState state, ISettingsStore settingsStore,
        IUpdateCoordinator updates, ThemeService theme, LocalizationService texts,
        ConnectionsViewModel connections, TransfersViewModel transfers, ToastService toasts,
        PlaceLibrary places, DialogService dialogs, GlobalHotkeyService hotkeys,
        Func<ExplorerViewModel> explorers, Func<ConnectionEditorViewModel> editors)
    {
        _state = state;
        _settingsStore = settingsStore;
        _toasts = toasts;
        _dialogs = dialogs;
        _places = places;
        _explorers = explorers;
        _editors = editors;
        Texts = texts;
        Settings = new SettingsViewModel(state, PersistAsync, updates, theme, texts,
            transfers.ApplyConcurrencyFromPreferences, hotkeys, toasts);
        Connections = connections;
        Transfers = transfers;
        transfers.OpenFailureDetails = () =>
        {
            Navigate("transfers");
            if (ActivePage == ShellPage.Transfers)
                transfers.ShowFailedTransfers();
        };
        FavoritesPage = new PlacesPageViewModel(true, places,
            () => Connections.Items.Select(item => item.Connection).ToList(),
            OpenSavedPlace, () => Navigate("connections"), dialogs, toasts, texts);
        RecentPage = new PlacesPageViewModel(false, places,
            () => Connections.Items.Select(item => item.Connection).ToList(),
            OpenSavedPlace, () => Navigate("connections"), dialogs, toasts, texts);
        places.Changed += (_, _) => OnPropertyChanged(nameof(StatusLeft));
        transfers.PropertyChanged += (_, args) =>
        {
                        if (args.PropertyName is nameof(TransfersViewModel.FooterText))
                OnPropertyChanged(nameof(StatusRight));
            if (args.PropertyName is nameof(TransfersViewModel.Concurrency))
                Settings.RefreshConcurrency();
        };
        connections.BrowseRequested += (_, connection) => BrowseConnection(connection);
        connections.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConnectionsViewModel.Count))
            {
                OnPropertyChanged(nameof(StatusRight));
                FavoritesPage.Refresh();
                RecentPage.Refresh();
                OnPropertyChanged(nameof(StatusLeft));
            }
        };
        connections.ConnectionUpdated += OnConnectionUpdated;
        connections.BeforeDeleteAsync = PrepareDeleteConnectionAsync;
        connections.DeleteImpact = ConnectionDeleteImpact;
        connections.ConnectionDeleted += (_, id) => OnConnectionDeleted(id);
        _state.Preferences.Normalize();
        _sidebarWidth = Layout.SidebarWidth;
        _sidebarCollapsed = Layout.SidebarCollapsed;
        Layout.ExcludeSettingsFromStartup(); // Also migrates layouts saved by earlier versions.
        _session = ShellSession.ForStartup(Layout, _state.Preferences.RestorePreviousTabs);

        Texts.PropertyChanged += (_, _) =>
        {
            RefreshTabState();
        };
        ShowSettingsCommand = new RelayCommand(_ => OpenReusable(ShellPage.Settings));
        OpenDuplicateCommand = new RelayCommand(_ => OpenActiveDuplicate());
        NewTabCommand = new RelayCommand(_ => Open(ShellPage.Explorer));
        CloseActiveCommand = new RelayCommand(_ => CloseActive());
        CloseTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is ShellTabViewModel tab)
                CloseTab(tab);
        });
        ActivateTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is ShellTabViewModel tab)
                Activate(tab);
        });
        ToggleSidebarCommand = new RelayCommand(_ => ToggleSidebar());
        OpenConnectionCommand = new RelayCommand(parameter => OpenConnection(parameter as Guid?, forceNew: false));
        OpenConnectionNewTabCommand = new RelayCommand(parameter => OpenConnection(parameter as Guid?, forceNew: true));
        AddSidebarConnectionCommand = new RelayCommand(_ => AddSidebarConnection());
        EditSidebarConnectionCommand = new RelayCommand(parameter =>
        {
            if (parameter is ConnectionItemViewModel item)
                EditSidebarConnection(item);
        });
        ToggleStorageMenuCommand = new RelayCommand(_ =>
        {
            IsStorageMenuOpen = !IsStorageMenuOpen;
        });
        ShowConnectionsCommand = new RelayCommand(_ => Navigate("connections"));
        ReturnToConnectionListCommand = new RelayCommand(_ => ReturnToConnectionList());
        RebuildTabs();
        RestoreTabLocations();
    }

    public ConnectionsViewModel Connections { get; }

    private bool ConfirmDiscardConnectionEditor(ConnectionWorkspace workspace)
    {
        if (!workspace.Editor.HasUnsavedChanges)
            return true;
        return _dialogs.ShowChoice(null, new DialogRequest
        {
            Title = Texts["Tab_UnsavedConnectionTitle"],
            Message = Texts["Tab_UnsavedConnectionMessage"],
            PrimaryText = Texts["Tab_DiscardChanges"],
            SecondaryText = Texts["Tab_KeepEditing"],
            Tone = DialogTone.Danger
        }) == DialogChoice.Primary;
    }

    public bool ReturnToConnectionList()
    {
        if (ActiveTab?.Workspace is not ConnectionWorkspace workspace || !workspace.Editor.IsOpen)
            return false;
        if (!ConfirmDiscardConnectionEditor(workspace))
            return false;
        workspace.Editor.Close();
        return true;
    }

    public void BrowseConnection(StorageConnection connection) => OpenConnection(connection.Id, forceNew: false);

    private void OpenSavedPlace(SavedPlace place, bool newTab)
    {
        var connection = Connections.Items.FirstOrDefault(item => item.Id == place.ConnectionId)?.Connection;
        if (connection is null)
        {
            _toasts.Show(new ToastRequest { Title = Texts["Places_MissingConnection"], Tone = ToastTone.Warning });
            return;
        }
        // Opening a saved location must not replace the Favorites/Recent workspace or
        // unexpectedly repurpose an Explorer already browsing another location.
        if (!newTab)
        {
            var match = Tabs.FirstOrDefault(tab => tab.Page == ShellPage.Explorer
                && tab.Workspace is ExplorerWorkspace w && w.Explorer.Place is { } current
                && current.Connection.Id == connection.Id
                && current.Bucket == place.Bucket && current.Prefix == place.Prefix);
            if (match is not null)
            {
                Activate(match);
                return;
            }
        }
        if (!Open(ShellPage.Explorer))
            return;
        if (ActiveTab?.Workspace is ExplorerWorkspace workspace)
            Launch(() => workspace.Explorer.OpenPlaceAsync(new ExplorerViewModel.ExplorerPlace(
                connection, place.Bucket, place.Prefix, place.Bucket is null)));
    }

    private bool BelongsToConnection(ShellTabViewModel tab, Guid id) =>
        tab.Workspace is ExplorerWorkspace workspace && workspace.Explorer.ConnectionId == id
        || _unavailableRestores.GetValueOrDefault(tab.Id)?.ConnectionId == id;

    private string ConnectionDeleteImpact(Guid id) => Environment.NewLine + Environment.NewLine
        + string.Format(Texts["Connection_DeleteImpact"], Tabs.Count(tab => BelongsToConnection(tab, id)),
            Transfers.ConnectionWorkCount(id));

    private async Task<bool> PrepareDeleteConnectionAsync(StorageConnection connection)
    {
        var original = ActiveTab;
        try
        {
            // Resolve every dirty workspace before deleting credentials or removing any tabs.
            foreach (var tab in Tabs.ToArray())
            {
                if (BelongsToConnection(tab, connection.Id) && !await ConfirmTabLeaveAsync(tab))
                    return false;
                if (tab.Workspace is ConnectionWorkspace workspace
                    && workspace.Editor.ConnectionId == connection.Id)
                {
                    if (workspace.Editor.IsBusy)
                    {
                        _toasts.Show(new ToastRequest { Title = Texts["Connection_Saving"], Tone = ToastTone.Information });
                        return false;
                    }
                    if (!ConfirmDiscardConnectionEditor(workspace))
                        return false;
                }
            }
            await Transfers.CancelConnectionAsync(connection.Id);
            return true;
        }
        finally
        {
            if (original is not null && Tabs.Contains(original))
                Activate(original);
        }
    }

    private async Task<bool> PrepareConnectionChangeAsync(ConnectionChange change)
    {
        if (change.Previous is null)
            return true;
        var writesOnly = !change.RequiresReconnect;
        var stopsTransfers = change.RequiresReconnect || change.Current.IsReadOnly && !change.Previous.IsReadOnly;
        var work = stopsTransfers ? Transfers.ConnectionWorkCount(change.Current.Id, writesOnly) : 0;
        if (work > 0 && _dialogs.Show(null, new DialogRequest
        {
            Title = Texts["Connection_ChangeTitle"],
            Message = string.Format(Texts["Connection_ChangeTransfers"], work),
            PrimaryText = Texts["Connection_Save"],
            SecondaryText = Texts["Dialog_Cancel"],
            Tone = DialogTone.Danger
        }) != true)
            return false;
        if (change.RequiresReconnect)
        {
            var original = ActiveTab;
            try
            {
                foreach (var tab in Tabs.Where(tab => BelongsToConnection(tab, change.Current.Id)).ToArray())
                    if (!await ConfirmTabLeaveAsync(tab))
                        return false;
            }
            finally
            {
                if (original is not null && Tabs.Contains(original))
                    Activate(original);
            }
        }
        // Cancel with the old credentials still present so multipart cleanup can run.
        if (stopsTransfers)
            await Transfers.CancelConnectionAsync(change.Current.Id, writesOnly);
        return true;
    }

    private async void OnConnectionUpdated(object? sender, ConnectionChange change)
    {
        try
        {
            var updates = Tabs.Select(tab => tab.Workspace).OfType<ExplorerWorkspace>()
                .Select(workspace => workspace.Explorer.ApplyConnectionChangeAsync(change)).ToArray();
            // Capture old transfer work before yielding to a newly reconnected tab.
            var transfers = change.RequiresReconnect || change.Current.IsReadOnly
                ? Transfers.CancelConnectionAsync(change.Current.Id, writesOnly: !change.RequiresReconnect)
                : Task.CompletedTask;
            await Task.WhenAll(updates.Append(transfers));
            RefreshTabState();
            ScheduleSave();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue("ui", exception);
            _toasts.Show(new ToastRequest { Title = Texts["Connection_RefreshFailed"], Tone = ToastTone.Warning });
        }
    }

    private void OnConnectionDeleted(Guid id)
    {
        _closedTabs.RemoveAll(tab => tab.Place?.Connection.Id == id);
        OnPropertyChanged(nameof(CanRestoreClosedTab));
        foreach (var tab in Tabs.Where(tab => BelongsToConnection(tab, id)).Reverse().ToArray())
            RemoveTab(tab, remember: false);
        foreach (var workspace in Tabs.Select(tab => tab.Workspace).OfType<ConnectionWorkspace>())
            if (workspace.Editor.ConnectionId == id)
                workspace.Editor.Close();
        foreach (var key in _unavailableRestores.Where(pair => pair.Value.ConnectionId == id).Select(pair => pair.Key).ToArray())
            _unavailableRestores.Remove(key);
        ScheduleSave();
        Launch(PruneDeletedConnectionsAsync);
    }

    private async Task PruneDeletedConnectionsAsync()
    {
        try
        {
            await _places.PruneConnectionsAsync(Connections.Items.Select(item => item.Id).ToHashSet());
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = Texts["Places_SaveFailed"], Tone = ToastTone.Error });
        }
    }

    public void UseDesignLayout()
    {
        _persist = false;
        _pendingSave?.Cancel();
        _session = ShellSession.CreateDefault();
        _sidebarWidth = WindowLayout.DefaultSidebarWidth;
        SetSidebarCollapsed(false);
        RebuildTabs();
    }

    public void Navigate(string? pageId) => ChangeActive(pageId, openNew: false);

    public void OpenPage(string? pageId) => ChangeActive(pageId, openNew: true);

    public void OpenActiveDuplicate() => DuplicateTab(ActiveTab);

    public void DuplicateTab(ShellTabViewModel? source)
    {
        var place = (source?.Workspace as ExplorerWorkspace)?.Explorer.Place;
        if (!Open(ShellPage.Explorer) || place is not { } saved)
            return;
        if (ActiveTab?.Workspace is ExplorerWorkspace created)
            Launch(() => created.Explorer.OpenPlaceAsync(saved));
    }

    public void CloseActive() => Launch(async () => { await CloseAtAsync(_session.ActiveIndex); });

    public void CloseTab(ShellTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index >= 0)
            Launch(async () => { await CloseAtAsync(index); });
    }

    public void CloseOtherTabs(ShellTabViewModel keep) => Launch(() => CloseOtherTabsCoreAsync(keep));

    private async Task CloseOtherTabsCoreAsync(ShellTabViewModel keep)
    {
        if (_closingOtherTabs || !Tabs.Contains(keep))
            return;
        _closingOtherTabs = true;
        try
        {
            foreach (var other in Tabs.Where(tab => tab != keep).Reverse().ToArray())
            {
                var index = Tabs.IndexOf(other);
                if (index >= 0 && !await CloseAtAsync(index))
                    return; // Stay/cancel preserves this and the remaining tabs.
            }
            Activate(keep);
        }
        finally
        {
            _closingOtherTabs = false;
        }
    }

    public void RestoreClosedTab()
    {
        if (_closedTabs.Count == 0)
            return;
        var closed = _closedTabs[^1];
        if (closed.Place is { } saved)
        {
            var current = Connections.Items.FirstOrDefault(item => item.Id == saved.Connection.Id)?.Connection;
            if (current is null)
            {
                _closedTabs.RemoveAt(_closedTabs.Count - 1);
                OnPropertyChanged(nameof(CanRestoreClosedTab));
                _toasts.Show(new ToastRequest { Title = Texts["Places_MissingConnection"], Tone = ToastTone.Warning });
                return;
            }
            closed = closed with { Place = saved with { Connection = current } };
        }
        // Tool pages are singletons: reopening a closed tool activates its current
        // instance rather than creating an indistinguishable duplicate.
        var existing = closed.Page is ShellPage.Settings or ShellPage.Transfers
            or ShellPage.Favorites or ShellPage.Recent
            ? Tabs.FirstOrDefault(tab => tab.Page == closed.Page)
            : null;
        if (existing is not null)
            Activate(existing);
        else if (!Open(closed.Page))
            return; // Keep history when the tab limit prevents reopening.
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        OnPropertyChanged(nameof(CanRestoreClosedTab));
        if (closed.Place is { } place && ActiveTab?.Workspace is ExplorerWorkspace workspace)
            Launch(() => workspace.Explorer.OpenPlaceAsync(place));
    }

    public void Activate(ShellTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0 || !_session.Activate(index))
            return;
        RefreshTabState();
        ScheduleSave();
    }

    public void ActivateIndex(int index)
    {
        if (!_session.Activate(index))
            return;
        RefreshTabState();
        ScheduleSave();
    }

    public void Cycle(int delta)
    {
        if (!_session.Cycle(delta))
            return;
        RefreshTabState();
        ScheduleSave();
    }

    public void MoveTab(ShellTabViewModel tab, int toIndex)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0 || !_session.Move(from, toIndex))
            return;
        Tabs.Move(from, toIndex);
        RefreshTabState();
        ScheduleSave();
    }

    public void ToggleSidebar()
    {
        if (_autoCompactSidebar)
        {
            // A narrow window can be expanded intentionally without changing the
            // preference that will be restored when the window becomes wide again.
            _temporarySidebarExpanded = !_temporarySidebarExpanded;
            CloseStorageMenu();
            NotifySidebarPresentation();
            return;
        }
        SetSidebarCollapsed(!_sidebarCollapsed);
        ScheduleSave();
    }

    public void SetAutoCompactSidebar(bool compact)
    {
        if (_autoCompactSidebar == compact)
            return;
        _autoCompactSidebar = compact;
        _temporarySidebarExpanded = false;
        CloseStorageMenu();
        NotifySidebarPresentation();
    }

    private void NotifySidebarPresentation()
    {
        OnPropertyChanged(nameof(EffectiveSidebarCollapsed));
        OnPropertyChanged(nameof(SidebarColumnWidth));
        OnPropertyChanged(nameof(SidebarToggleTip));
    }

    public void SetSidebarWidth(double width)
    {
        var clamped = Math.Clamp(width, WindowLayout.MinSidebarWidth, WindowLayout.MaxSidebarWidth);
        if (Math.Abs(_sidebarWidth - clamped) < 0.5)
            return;
        _sidebarWidth = clamped;
        if (!EffectiveSidebarCollapsed)
            OnPropertyChanged(nameof(SidebarColumnWidth));
        ScheduleSave();
    }

    public void ResetSidebarWidth() => SetSidebarWidth(WindowLayout.DefaultSidebarWidth);

    public void CaptureBounds(double left, double top, double width, double height, bool maximized)
    {
        Layout.Left = left;
        Layout.Top = top;
        Layout.Width = width;
        Layout.Height = height;
        Layout.IsMaximized = maximized;
    }

    public async Task FlushAsync()
    {
        if (!_persist)
            return;
        _pendingSave?.Cancel();
        await PersistAsync();
    }

    private void ChangeActive(string? pageId, bool openNew)
    {
        if (!ShellPages.TryParse(pageId, out var page))
            return;
        if (page == ShellPage.Settings || page is ShellPage.Transfers or ShellPage.Favorites or ShellPage.Recent)
        {
            OpenReusable(page);
            return;
        }
        // The connections nav item means the list. Re-selecting it leaves the editor.
        if (page == ShellPage.Connections)
        {
            if (openNew)
                Open(page);
            else
            {
                OpenReusable(page);
                ReturnToConnectionList();
            }
            return;
        }

        if (openNew)
        {
            Open(page);
            return;
        }
        if (ActivePage == page)
            return;
        var matching = Tabs.FirstOrDefault(tab => tab.Page == page);
        if (matching is not null)
            Activate(matching);
        else
            Open(page);
    }

    private bool Open(ShellPage page)
    {
        if (!_session.TryOpen(page))
        {
            ShowTabLimitWarning();
            return false;
        }

        Tabs.Add(CreateTab(_session.IdAt(_session.Count - 1)));
        RefreshTabState();
        ClearNote();
        ScheduleSave();
        return true;
    }

    private async Task<bool> CloseAtAsync(int index)
    {
        if (index < 0 || index >= Tabs.Count)
            return false;
        var closing = Tabs[index];
        // The one permanent Connections tab cannot be removed. Its close action
        // should dismiss an open editor (with protection), not show a discard
        // prompt and then appear to do nothing.
        if (_session.Count == 1 && closing.Page == ShellPage.Connections)
        {
            if (closing.Workspace is not ConnectionWorkspace workspace || !workspace.Editor.IsOpen)
                return false;
            if (!ConfirmDiscardConnectionEditor(workspace))
                return false;
            workspace.Editor.Close();
            return true;
        }
        if (!_pendingCloses.Add(closing.Id))
            return false;
        try
        {
            if (!await ConfirmTabLeaveAsync(closing))
                return false;

            // An asynchronous save can yield; identify the tab rather than using stale indexes.
            index = Tabs.IndexOf(closing);
            if (index < 0)
                return false;
            return RemoveTab(closing, remember: true);
        }
        finally
        {
            _pendingCloses.Remove(closing.Id);
        }
    }

    private bool RemoveTab(ShellTabViewModel closing, bool remember)
    {
        var index = Tabs.IndexOf(closing);
        if (index < 0)
            return false;
        var place = (closing.Workspace as ExplorerWorkspace)?.Explorer.Place;
        var lastTab = _session.Count == 1;
        if (!_session.CloseAt(index))
            return false;
        if (remember)
        {
            _closedTabs.Add(new ClosedTab(closing.Page, place));
            if (_closedTabs.Count > 10)
                _closedTabs.RemoveAt(0);
            OnPropertyChanged(nameof(CanRestoreClosedTab));
        }
        _unavailableRestores.Remove(closing.Id);
        _activation.Forget(closing.Id);
        if (lastTab)
            closing.ResetWorkspaces();
        else
        {
            Tabs.RemoveAt(index);
            closing.Dispose();
        }
        RefreshTabState();
        ScheduleSave();
        return true;
    }

    private async Task<bool> ConfirmTabLeaveAsync(ShellTabViewModel tab)
    {
        if (tab.Workspace is ExplorerWorkspace explorer && explorer.Explorer.HasUnsavedChanges)
        {
            Activate(tab);
            if (!await explorer.Explorer.ConfirmLeaveAsync())
                return false;
        }
        if (tab.Workspace is ConnectionWorkspace connections && connections.Editor.HasUnsavedChanges)
        {
            Activate(tab);
            if (!ConfirmDiscardConnectionEditor(connections))
                return false;
        }
        return true;
    }

    // Check all workspaces before stopping services. Cancelling leaves every tab alive.
    public async Task<bool> CanExitAsync()
    {
        foreach (var tab in Tabs.ToArray())
            if (!await ConfirmTabLeaveAsync(tab))
                return false;
        return true;
    }

    private sealed record ClosedTab(ShellPage Page, ExplorerViewModel.ExplorerPlace? Place);

    private void SetSidebarCollapsed(bool collapsed)
    {
        if (!SetProperty(ref _sidebarCollapsed, collapsed, nameof(SidebarCollapsed)))
            return;
        CloseStorageMenu();
        NotifySidebarPresentation();
    }

    public bool ReturnFromSettings()
    {
        if (!IsSettingsOpen)
            return false;
        Launch(async () => { await CloseAtAsync(_session.ActiveIndex); });
        return true;
    }

    private void OpenConnection(Guid? id, bool forceNew)
    {
        if (id is not Guid connectionId)
            return;
        var item = Connections.Items.FirstOrDefault(candidate => candidate.Id == connectionId);
        if (item is null)
            return;

        CloseStorageMenu();
        var explorers = new List<OpenExplorer>();
        for (var index = 0; index < Tabs.Count && index < _session.Count; index++)
        {
            if (_session.PageAt(index) != ShellPage.Explorer)
                continue;
            var place = (Tabs[index].Workspace as ExplorerWorkspace)?.Explorer.Place;
            explorers.Add(new OpenExplorer(Tabs[index].Id, place?.Connection.Id));
        }

        var decision = ConnectionOpener.Choose(
            connectionId,
            explorers,
            _activation.RememberedTab(connectionId),
            _activation.ActivatedAt,
            _session.Count,
            ShellSession.MaxTabs,
            forceNew);

        if (decision.Kind == ConnectionOpenKind.Rejected)
        {
            ShowTabLimitWarning();
            return;
        }

        if (decision.Kind == ConnectionOpenKind.Activate && decision.TabId is Guid tabId)
        {
            var tab = Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (tab is not null)
            {
                Activate(tab);
                ClearNote();
            }
            return;
        }

        Open(ShellPage.Explorer);
        if (ActiveTab?.Workspace is ExplorerWorkspace workspace)
            Launch(() => workspace.Explorer.OpenAsync(item.Connection));
    }

    private void AddSidebarConnection()
    {
        CloseStorageMenu();
        if (!OpenReusable(ShellPage.Connections))
            return;
        if (ActiveTab?.Workspace is ConnectionWorkspace workspace && ConfirmDiscardConnectionEditor(workspace))
            workspace.Editor.BeginCreate();
    }

    private void EditSidebarConnection(ConnectionItemViewModel item)
    {
        CloseStorageMenu();
        if (!OpenReusable(ShellPage.Connections))
            return;
        if (ActiveTab?.Workspace is ConnectionWorkspace workspace && ConfirmDiscardConnectionEditor(workspace))
            Launch(() => Connections.EditIntoAsync(item, workspace.Editor));
    }

    private bool OpenReusable(ShellPage page)
    {
        if (ActivePage == page)
            return true;

        var before = _session.Count;
        if (!_session.ActivateOrOpen(page))
        {
            ShowTabLimitWarning();
            return false;
        }
        if (_session.Count > before)
            Tabs.Add(CreateTab(_session.IdAt(_session.ActiveIndex)));
        RefreshTabState();
        ClearNote();
        ScheduleSave();
        return true;
    }

    private void ShowTabLimitWarning()
    {
        var title = Texts["Tab_LimitReached"];
        // Avoid stacking duplicate warnings when the user retries from different entry points.
        if (_toasts.Items.Any(item => !item.IsClosing && item.Title == title))
            return;
        _toasts.Show(new ToastRequest
        {
            Title = title,
            Message = string.Format(Texts["Status_TabLimit"], ShellSession.MaxTabs),
            Tone = ToastTone.Warning
        });
    }

    private void RebuildTabs()
    {
        foreach (var tab in Tabs)
            tab.Dispose();
        Tabs.Clear();
        for (var i = 0; i < _session.Count; i++)
            Tabs.Add(CreateTab(_session.IdAt(i)));
        RefreshTabState();
    }

    private void RestoreTabLocations()
    {
        if (!_persist || !_state.Preferences.RestorePreviousTabs)
            return;
        var missingConnection = false;
        for (var i = 0; i < Tabs.Count && i < Layout.TabLocations.Count; i++)
        {
            if (Tabs[i].Page != ShellPage.Explorer || Layout.TabLocations[i] is not { } saved)
                continue;
            var connection = Connections.Items.FirstOrDefault(item => item.Id == saved.ConnectionId)?.Connection;
            if (connection is null)
            {
                // If the catalog failed to load, leave a blank Explorer rather than
                // deleting its persisted location on the next layout save.
                missingConnection = true;
                _unavailableRestores[Tabs[i].Id] = saved;
                continue;
            }
            if (Tabs[i].Workspace is ExplorerWorkspace workspace)
                Launch(() => workspace.Explorer.OpenPlaceAsync(new ExplorerViewModel.ExplorerPlace(
                    connection, saved.Bucket, saved.Prefix, saved.Bucket is null)));
        }
        if (missingConnection && !Connections.HasLoadError)
            _toasts.Show(new ToastRequest
            {
                Title = Texts["Tab_RestoreMissingConnection"],
                Tone = ToastTone.Warning
            });
    }

    private void RefreshTabState()
    {
        for (var i = 0; i < Tabs.Count && i < _session.Count; i++)
        {
            var tab = Tabs[i];
            tab.Update(_session.PageAt(i), i, i == _session.ActiveIndex, Tabs.Count == 1);
            EnsureWorkspace(tab);
        }

        NotifyPage();
    }

    private void NotifyPage()
    {
        OnPropertyChanged(nameof(ActivePage));
        OnPropertyChanged(nameof(IsSettingsOpen));
        OnPropertyChanged(nameof(ShowTabContent));
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(StatusLeft));
        OnPropertyChanged(nameof(StatusRight));
        OnPropertyChanged(nameof(CloseTip));
        OnPropertyChanged(nameof(CanAddTab));
        OnPropertyChanged(nameof(NewTabTip));
        OnPropertyChanged(nameof(ShowConnections));
        OnPropertyChanged(nameof(ShowFavorites));
        OnPropertyChanged(nameof(ShowRecent));
        OnPropertyChanged(nameof(ShowTransfers));
        OnPropertyChanged(nameof(ShowExplorer));
        OnPropertyChanged(nameof(ShowPageHeading));
        OnPropertyChanged(nameof(ShowConnectionBack));
        OnPropertyChanged(nameof(ActiveConnectionId));
        NoteActiveExplorer();
    }

    private void NoteActiveExplorer()
    {
        if (ActiveTab is not { } tab || tab.Page != ShellPage.Explorer)
            return;
        _activation.NoteActive(tab.Id, ActiveExplorer?.Place?.Connection.Id);
        OnPropertyChanged(nameof(ActiveConnectionId));
    }

    private ExplorerViewModel? ActiveExplorer =>
        (ActiveTab?.Workspace as ExplorerWorkspace)?.Explorer;

    private ShellTabViewModel CreateTab(Guid id)
    {
        var tab = new ShellTabViewModel(Texts, id);
        tab.ActivityChanged += (_, args) =>
        {
            if (ActiveTab != tab)
                return;
            if (args.PropertyName == nameof(ExplorerViewModel.StatusText))
                OnPropertyChanged(nameof(StatusLeft));
            if (args.PropertyName == nameof(ExplorerViewModel.Heading))
                NoteActiveExplorer();
            if (args.PropertyName == nameof(ExplorerViewModel.PathText))
                ScheduleSave();
            if (args.PropertyName == nameof(ConnectionEditorViewModel.IsOpen))
                OnPropertyChanged(nameof(ShowConnectionBack));
        };
        return tab;
    }

    private void EnsureWorkspace(ShellTabViewModel tab)
    {
        if (tab.Page == ShellPage.Explorer && !tab.HasExplorer)
        {
            var explorer = _explorers();
            explorer.OpenFailedTransfersRequested += (_, _) =>
            {
                Navigate("transfers");
                if (ActivePage == ShellPage.Transfers)
                    Transfers.ShowFailedTransfers();
            };
            explorer.OpenNewTabRequested += (_, place) =>
            {
                if (Open(ShellPage.Explorer) && ActiveTab?.Workspace is ExplorerWorkspace created)
                    Launch(() => created.Explorer.OpenPlaceAsync(place));
            };
            tab.AttachExplorer(new ExplorerWorkspace(explorer, Texts, ShowConnectionsCommand));
        }
        else if (tab.Page == ShellPage.Connections && !tab.HasConnections)
        {
            var editor = _editors();
            editor.BeforeSaveAsync = PrepareConnectionChangeAsync;
            tab.AttachConnections(new ConnectionWorkspace(Connections, editor, Texts, _toasts));
        }
    }

    // Event/RelayCommand entry points are void; observe their async work without
    // making tab navigation dependent on a background task's completion.
    private void Launch(Func<Task> operation) =>
        _ = UiAsyncTask.RunAsync(operation, _ =>
        {
            var title = Texts["Ui_ActionFailed"];
            if (_toasts.Items.Any(item => !item.IsClosing && item.Title == title))
                return;
            _toasts.Show(new ToastRequest { Title = title, Tone = ToastTone.Error });
        });

    private void ClearNote()
    {
        if (_statusNote is null)
            return;
        _statusNote = null;
        OnPropertyChanged(nameof(StatusLeft));
    }

    private void ScheduleSave()
    {
        if (!_persist)
            return;
        _pendingSave?.Cancel();
        _pendingSave = new CancellationTokenSource();
        var token = _pendingSave.Token;
        _ = DelayedSaveAsync(token);
    }

    private async Task DelayedSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await PersistAsync();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
    }

    private async Task PersistAsync()
    {
        if (!_persist)
            return;
        await _saveGate.WaitAsync();
        try
        {
            if (_persist)
                CopySessionIntoLayout();
            await _settingsStore.SaveAsync(_state.Preferences);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void CopySessionIntoLayout()
    {
        Layout.SidebarWidth = _sidebarWidth;
        Layout.SidebarCollapsed = _sidebarCollapsed;
        if (!_state.Preferences.RestorePreviousTabs)
        {
            // Keep the current tabs open, but do not retain them for the next startup.
            Layout.Tabs = [ShellPages.Connections];
            Layout.TabLocations = [];
            Layout.ActiveTab = 0;
        }
        else
        {
            Layout.Tabs = _session.TabIds.ToList();
            Layout.TabLocations = Tabs.Select(tab =>
            {
                if (tab.Page != ShellPage.Explorer)
                    return null;
                if (tab.Workspace is ExplorerWorkspace workspace && workspace.Explorer.Place is { } place)
                    return new RestorableTabLocation
                    {
                        ConnectionId = place.Connection.Id,
                        Bucket = place.Bucket,
                        Prefix = place.Prefix
                    };
                // Preserve an unavailable startup location when saving other tabs.
                return _unavailableRestores.GetValueOrDefault(tab.Id);
            }).ToList();
            Layout.ActiveTab = _session.RestorableActiveIndex;
        }
        Layout.ExcludeSettingsFromStartup();
        Layout.Clamp();
    }

    private void SetError(Exception ex)
    {
        DiagnosticLog.Current.Queue("ui", ex);
        _statusNote = Texts["Settings_SaveFailed"];
        OnPropertyChanged(nameof(StatusLeft));
    }
}

