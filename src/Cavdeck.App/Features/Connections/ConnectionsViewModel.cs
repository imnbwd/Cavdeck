using System.Collections.ObjectModel;
using Cavdeck.App.Features.Diagnostics;
using System.Windows.Input;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Connections;

public sealed class ConnectionsViewModel : ViewModelBase
{
    private readonly ConnectionService _connections;
    private readonly LocalizationService _texts;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private bool _isBusy;
    private bool _isLoading;
    private string _loadError = "";
    private Guid? _selectedId;

    public ConnectionsViewModel(
        ConnectionService connections,
        LocalizationService texts,
        DialogService dialogs,
        ToastService toasts)
    {
        _connections = connections;
        _texts = texts;
        _dialogs = dialogs;
        _toasts = toasts;
        _texts.PropertyChanged += (_, _) =>
        {
            foreach (var item in Items)
                item.Refresh(_texts);
        };

        DeleteCommand = new AsyncRelayCommand(DeleteAsync, ReportUnexpectedUiError);
        TestStoredCommand = new AsyncRelayCommand(TestStoredAsync, ReportUnexpectedUiError);
        ReloadCommand = new AsyncRelayCommand(_ => LoadAsync(), ReportUnexpectedUiError);
        OpenCommand = new RelayCommand(Open);
    }

    private void ReportUnexpectedUiError(Exception exception)
    {
        var title = _texts["Ui_ActionFailed"];
        if (_toasts.Items.Any(item => !item.IsClosing && item.Title == title))
            return;
        _toasts.Show(new ToastRequest { Title = title, Tone = ToastTone.Error });
    }

    public event EventHandler<StorageConnection>? BrowseRequested;
    public event EventHandler<Guid>? ConnectionDeleted;
    public event EventHandler<ConnectionChange>? ConnectionUpdated;
    public Func<StorageConnection, Task<bool>>? BeforeDeleteAsync { get; set; }
    public Func<Guid, string>? DeleteImpact { get; set; }

    public void PublishSaved(ConnectionChange change)
    {
        var items = Items.Select(item => item.Connection).ToList();
        var index = items.FindIndex(item => item.Id == change.Current.Id);
        if (index < 0)
            items.Add(change.Current);
        else
            items[index] = change.Current;
        Replace(items);
        ConnectionUpdated?.Invoke(this, change);
    }

    public LocalizationService Texts => _texts;
    public ObservableCollection<ConnectionItemViewModel> Items { get; } = [];
    public ICommand DeleteCommand { get; }
    public ICommand TestStoredCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand OpenCommand { get; }

    public int Count => Items.Count;
    public bool HasConnections => Items.Count > 0;
    public bool IsBusy => _isBusy;
    public bool IsIdle => !_isBusy;
    public string LoadError => _loadError;
    public bool HasLoadError => _loadError.Length > 0;
    public bool IsLoading => _isLoading;
    public bool ShowSidebarLoading => _isLoading;
    public bool ShowSidebarEmpty => !_isLoading && !HasLoadError && !HasConnections;
    public bool ShowSidebarError => !_isLoading && HasLoadError && !HasConnections;
    public Guid? SelectedId => _selectedId;

    public async Task LoadAsync()
    {
        SetLoading(true);
        try
        {
            var connections = await Task.Run(() => _connections.ListAsync());
            Replace(connections);
            SetLoadError("");
        }
        catch (Exception exception)
        {
            // Retain any already visible connections when a reload fails.
            // In particular, never present damaged on-disk metadata as an empty catalog.
            DiagnosticLog.Current.Queue("config-recovery", exception);
            SetLoadError(_texts["Connection_LoadFailed"]);
            if (Items.Count > 0)
                _toasts.Show(new ToastRequest
                {
                    Title = _texts["Connection_LoadFailed"],
                    Tone = ToastTone.Error
                });
        }
        finally
        {
            SetLoading(false);
        }
    }

    public void Select(Guid id)
    {
        _selectedId = id;
        foreach (var item in Items)
            item.IsSelected = item.Id == id;
        OnPropertyChanged(nameof(SelectedId));
    }

    public void ReportLoadFailure() => SetLoadError(_texts["Connection_SaveFailed"]);

    public async Task EditIntoAsync(ConnectionItemViewModel item, ConnectionEditorViewModel editor)
    {
        if (!IsIdle)
            return;
        SetBusy(true);
        try
        {
            var accessKey = await _connections.ReadAccessKeyIdAsync(item.Id);
            editor.BeginEdit(item.Connection, accessKey);
            Select(item.Id);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Open(object? parameter)
    {
        if (parameter is not ConnectionItemViewModel item || !IsIdle)
            return;
        Select(item.Id);
        BrowseRequested?.Invoke(this, item.Connection);
    }

    private async Task DeleteAsync(object? parameter)
    {
        if (parameter is not ConnectionItemViewModel item || !IsIdle)
            return;
        var accepted = _dialogs.Show(null, new DialogRequest
        {
            Title = _texts["Connection_DeleteTitle"],
            Message = string.Format(_texts["Connection_DeleteMessage"], item.Name)
                + (DeleteImpact?.Invoke(item.Id) ?? ""),
            PrimaryText = _texts["Connection_Delete"],
            SecondaryText = _texts["Dialog_Cancel"],
            Tone = DialogTone.Danger
        });
        if (accepted != true)
            return;

        SetBusy(true);
        try
        {
            if (BeforeDeleteAsync is { } beforeDelete && !await beforeDelete(item.Connection))
                return;
            await _connections.DeleteAsync(item.Id);
            Replace(Items.Where(candidate => candidate.Id != item.Id)
                .Select(candidate => candidate.Connection).ToList());
            if (_selectedId == item.Id)
            {
                _selectedId = null;
                OnPropertyChanged(nameof(SelectedId));
            }
            // Deletion is committed. A subsequent list failure must never suppress this event.
            ConnectionDeleted?.Invoke(this, item.Id);
            await LoadAsync();
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Connection_SaveFailed"],
                Tone = ToastTone.Error
            });
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TestStoredAsync(object? parameter)
    {
        if (parameter is not ConnectionItemViewModel item || !IsIdle)
            return;
        SetBusy(true);
        item.BeginTest(_texts);
        try
        {
            var attempt = await _connections.TestStoredAsync(item.Id);
            var result = attempt.Probe ?? StorageProbeResult.Failed(new StorageFailure(
                StorageFailureKind.Unknown, null, null, null, null));
            item.ApplyTestResult(result, item.Connection.KeyPrefix, _texts);
            _toasts.Show(new ToastRequest
            {
                Title = item.Status,
                Message = item.StatusDetail,
                Tone = item.StatusLevel switch
                {
                    "Success" => ToastTone.Success,
                    "Warning" => ToastTone.Warning,
                    "Danger" => ToastTone.Error,
                    _ => ToastTone.Information
                }
            });
        }
        catch (Exception exception)
        {
            // Exception messages may be English or contain paths and endpoints.
            // Keep the diagnostic category in the safe log, not in the toast.
            DiagnosticLog.Current.Queue("connection-test", exception);
            item.ApplyUnexpectedTestFailure(_texts);
            _toasts.Show(new ToastRequest
            {
                Title = item.Status,
                Message = item.StatusDetail,
                Tone = ToastTone.Error
            });
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Replace(IReadOnlyList<StorageConnection> connections)
    {
        Items.Clear();
        foreach (var connection in connections)
        {
            var item = new ConnectionItemViewModel(connection);
            item.Refresh(_texts);
            item.IsSelected = item.Id == _selectedId;
            Items.Add(item);
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasConnections));
        RefreshSidebarStates();
    }

    private void SetBusy(bool busy)
    {
        if (!SetProperty(ref _isBusy, busy, nameof(IsBusy)))
            return;
        OnPropertyChanged(nameof(IsIdle));
    }

    private void SetLoading(bool loading)
    {
        if (SetProperty(ref _isLoading, loading, nameof(IsLoading)))
            RefreshSidebarStates();
    }

    private void RefreshSidebarStates()
    {
        OnPropertyChanged(nameof(ShowSidebarLoading));
        OnPropertyChanged(nameof(ShowSidebarEmpty));
        OnPropertyChanged(nameof(ShowSidebarError));
    }

    private void SetLoadError(string message)
    {
        if (SetProperty(ref _loadError, message, nameof(LoadError)))
        {
            OnPropertyChanged(nameof(HasLoadError));
            RefreshSidebarStates();
        }
    }
}

public sealed class ConnectionItemViewModel : ViewModelBase
{
    private string _subtitle = "";
    private string _providerLabel = "";
    private string _badges = "";
    private string _status = "";
    private string _statusLevel = "Muted";
    private string _statusDetail = "";
    private bool _isSelected;
    private StorageProbeResult? _lastTestResult;
    private string? _lastTestPrefix;
    private bool _unexpectedTestFailure;
    private bool _isTesting;

    public ConnectionItemViewModel(StorageConnection connection) => Connection = connection;

    public StorageConnection Connection { get; private set; }
    public Guid Id => Connection.Id;
    public string Name => Connection.Name;
    public string Subtitle => _subtitle;
    public string ProviderLabel => _providerLabel;
    public string Badges => _badges;
    public bool HasBadges => _badges.Length > 0;
    public bool HasStatus => _status.Length > 0;
    public bool HasStatusDetail => _statusDetail.Length > 0;

    public string StatusLevel
    {
        get => _statusLevel;
        set => SetProperty(ref _statusLevel, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set
        {
            if (SetProperty(ref _statusDetail, value))
                OnPropertyChanged(nameof(HasStatusDetail));
        }
    }

    public void BeginTest(LocalizationService texts)
    {
        _isTesting = true;
        _lastTestResult = null;
        _unexpectedTestFailure = false;
        RefreshTestState(texts);
    }

    public void ApplyTestResult(StorageProbeResult result, string? prefix, LocalizationService texts)
    {
        _lastTestResult = result;
        _lastTestPrefix = prefix;
        _unexpectedTestFailure = false;
        _isTesting = false;
        RefreshTestState(texts);
    }

    public void ApplyUnexpectedTestFailure(LocalizationService texts)
    {
        _lastTestResult = null;
        _unexpectedTestFailure = true;
        _isTesting = false;
        RefreshTestState(texts);
    }

    private void RefreshTestState(LocalizationService texts)
    {
        if (_isTesting)
        {
            Status = texts["Connection_Testing"];
            StatusDetail = "";
            StatusLevel = "Muted";
        }
        else if (_unexpectedTestFailure)
        {
            Status = texts["Connection_Failure_Unknown"];
            StatusDetail = texts["Connection_TestUnexpectedHint"];
            StatusLevel = "Danger";
        }
        else if (_lastTestResult is { } result)
        {
            Status = ConnectionText.Title(texts, result, _lastTestPrefix);
            StatusDetail = ConnectionText.Suggestion(texts, result);
            StatusLevel = ConnectionText.Level(result);
        }
    }

    public void Refresh(LocalizationService texts)
    {
        RefreshTestState(texts);
        var host = string.IsNullOrEmpty(Connection.Endpoint)
            ? Connection.Region
            : Uri.TryCreate(Connection.Endpoint, UriKind.Absolute, out var uri)
                ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
                : Connection.Endpoint;
        SetProperty(ref _subtitle, $"{ConnectionText.ProviderName(texts, Connection.Provider)} · {host}", nameof(Subtitle));
        SetProperty(ref _providerLabel, ConnectionText.ProviderName(texts, Connection.Provider), nameof(ProviderLabel));

        var parts = new List<string>();
        if (Connection.IsReadOnly)
            parts.Add(texts["Connection_BadgeReadOnly"]);
        if (!string.IsNullOrEmpty(Connection.RestrictedBucket))
            parts.Add(string.Format(texts["Connection_BadgeBucket"], Connection.RestrictedBucket));
        if (!string.IsNullOrEmpty(Connection.KeyPrefix))
            parts.Add(string.Format(texts["Connection_BadgePrefix"], Connection.KeyPrefix));
        if (SetProperty(ref _badges, string.Join(" · ", parts), nameof(Badges)))
            OnPropertyChanged(nameof(HasBadges));
    }
}
