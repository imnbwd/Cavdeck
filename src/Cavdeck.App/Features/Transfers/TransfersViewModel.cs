using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Transfers;

public sealed class TransfersViewModel : ViewModelBase
{
    private readonly TransferEngine _engine;
    private readonly LocalizationService _texts;
    private readonly ApplicationState _state;
    private readonly ISettingsStore _settings;
    private readonly ToastService _toasts;
    private readonly DialogService _dialogs;
    // Shell supplies navigation without coupling transfer logic to window controls.
    public Action? OpenFailureDetails { get; set; }
    private bool _cleaningHistory;
    private bool _announce;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<Guid, TransferRowViewModel> _rows = [];
    private readonly Dictionary<Guid, CrossBatchRowViewModel> _batchRows = [];
    private readonly HashSet<Guid> _pendingBatchRefresh = [];
    private readonly DispatcherTimer _batchRefreshTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly TransferNoticeBuffer _noticeBuffer = new();
    private bool _rebuilding;
    private TransferFilterChoice _selectedCurrentFilter = null!;
    private TransferFilterChoice _selectedHistoryFilter = null!;
    private readonly Dictionary<Guid, SpeedSample> _speeds = [];
    private bool _showHistory;
    private int _concurrency;
    private int _concurrencyChangeVersion;

    public TransfersViewModel(
        TransferEngine engine,
        LocalizationService texts,
        ApplicationState state,
        ISettingsStore settings,
        ToastService toasts,
        DialogService dialogs)
    {
        _engine = engine;
        _texts = texts;
        _state = state;
        _settings = settings;
        _toasts = toasts;
        _dialogs = dialogs;
        CurrentFilters = [
            new(TransferStatusFilter.All, "Transfer_FilterAll", texts),
            new(TransferStatusFilter.Running, "Transfer_FilterRunning", texts),
            new(TransferStatusFilter.Queued, "Transfer_FilterQueued", texts),
            new(TransferStatusFilter.Paused, "Transfer_FilterPaused", texts),
            new(TransferStatusFilter.Failed, "Transfer_FilterFailed", texts)
        ];
        HistoryFilters = [
            new(TransferStatusFilter.All, "Transfer_FilterAll", texts),
            new(TransferStatusFilter.Completed, "Transfer_FilterCompleted", texts),
            new(TransferStatusFilter.Cancelled, "Transfer_FilterCancelled", texts)
        ];
        _selectedCurrentFilter = CurrentFilters[0];
        _selectedHistoryFilter = HistoryFilters[0];
        _concurrency = Math.Clamp(state.Preferences.MaxConcurrentTransfers, 1, 8);
        engine.SetMaxConcurrent(_concurrency);
        engine.Changed += OnChanged;
        engine.Removed += OnRemoved;
        engine.BatchRemoved += OnBatchRemoved;
        engine.BatchChanged += OnBatchChanged;
        _batchRefreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            OnBatchRefreshTick, _dispatcher) { IsEnabled = false };
        _noticeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1800), DispatcherPriority.Background,
            OnNoticeTick, _dispatcher) { IsEnabled = false };
        texts.PropertyChanged += (_, _) =>
        {
            foreach (var row in _rows.Values)
                row.RefreshText();
            foreach (var row in _batchRows.Values)
                row.RefreshText();
            foreach (var choice in CurrentFilters.Concat(HistoryFilters))
                choice.Refresh();
            NotifySummary();
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyDescription));
        };
        PauseCommand = new RelayCommand(parameter => Run(engine.PauseAsync, parameter));
        ResumeCommand = new RelayCommand(parameter => Run(engine.ResumeAsync, parameter));
        CancelCommand = new RelayCommand(parameter => Run(engine.CancelAsync, parameter));
        RetryCommand = new RelayCommand(parameter => Run(engine.RetryAsync, parameter));
        RetryBatchCommand = new RelayCommand(parameter => Run(engine.RetryBatchAsync, parameter));
        RemoveRecordCommand = new RelayCommand(parameter =>
        {
            if (parameter is Guid id)
                _ = RemoveRecordAsync(id);
        });
        ClearFailedCommand = new RelayCommand(_ => _ = ClearHistoryAsync(TransferHistoryScope.Failed));
        ClearFinishedCommand = new RelayCommand(_ => _ = ClearHistoryAsync(TransferHistoryScope.Finished));
        CopyFailureCommand = new RelayCommand(CopyFailure);
        ToggleBatchCommand = new RelayCommand(parameter =>
        {
            if (parameter is Guid id && _batchRows.TryGetValue(id, out var batch))
            {
                batch.IsExpanded = !batch.IsExpanded;
                RebuildDisplayItems();
            }
        });
        ToggleDetailsCommand = new RelayCommand(parameter =>
        {
            if (parameter is Guid id && _rows.TryGetValue(id, out var row))
                row.IsDetailsExpanded = !row.IsDetailsExpanded;
        });
        Visible.CollectionChanged += (_, _) => { if (!_rebuilding) RebuildDisplayItems(); };
        VisibleBatches.CollectionChanged += (_, _) => { if (!_rebuilding) RebuildDisplayItems(); };
        ShowActiveCommand = new RelayCommand(_ => ShowHistory = false);
        ShowHistoryCommand = new RelayCommand(_ => ShowHistory = true);
        foreach (var job in engine.Jobs)
            Apply(job, 0);
        foreach (var batch in engine.Batches)
            ApplyBatch(batch);
        _announce = true;
        Rebuild();
    }

    public LocalizationService Texts => _texts;
    public ObservableCollection<TransferRowViewModel> Visible { get; } = [];
    public ObservableCollection<CrossBatchRowViewModel> VisibleBatches { get; } = [];
    public ObservableCollection<object> DisplayItems { get; } = [];
    public int[] ConcurrencyChoices { get; } = [1, 2, 3, 4, 6, 8];
    public IReadOnlyList<TransferFilterChoice> CurrentFilters { get; }
    public IReadOnlyList<TransferFilterChoice> HistoryFilters { get; }

    public TransferFilterChoice SelectedCurrentFilter
    {
        get => _selectedCurrentFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedCurrentFilter, value))
                Rebuild();
        }
    }

    public TransferFilterChoice SelectedHistoryFilter
    {
        get => _selectedHistoryFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedHistoryFilter, value))
                Rebuild();
        }
    }

    private TransferStatusFilter ActiveFilter => ShowHistory
        ? _selectedHistoryFilter.Filter : _selectedCurrentFilter.Filter;
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RetryBatchCommand { get; }
    public ICommand RemoveRecordCommand { get; }
    public ICommand ClearFailedCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand CopyFailureCommand { get; }
    public ICommand ToggleBatchCommand { get; }
    public ICommand ToggleDetailsCommand { get; }
    public ICommand ShowActiveCommand { get; }
    public ICommand ShowHistoryCommand { get; }

    public bool ShowHistory
    {
        get => _showHistory;
        private set
        {
            if (!SetProperty(ref _showHistory, value))
                return;
            Rebuild();
            OnPropertyChanged(nameof(ShowActive));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyDescription));
            NotifySummary();
        }
    }

    public void ShowFailedTransfers()
    {
        ShowHistory = false;
        SelectedCurrentFilter = CurrentFilters.First(choice =>
            choice.Filter == TransferStatusFilter.Failed);
        // Reveal failed items immediately when navigating from a batch toast.
        foreach (var row in _batchRows.Values.Where(row =>
            row.Batch.State == CrossBatchState.Failed || row.Progress.Failed > 0))
            row.IsExpanded = true;
        RebuildDisplayItems();
    }

    public bool ShowActive => !_showHistory;
    public bool IsEmpty => DisplayItems.Count == 0;
    public string EmptyTitle => _texts[ActiveFilter != TransferStatusFilter.All
        ? "Empty_TransferFilteredTitle"
        : ShowHistory ? "Empty_TransferHistoryTitle" : "Empty_TransfersTitle"];
    public string EmptyDescription => _texts[ActiveFilter != TransferStatusFilter.All
        ? "Empty_TransferFilteredDescription"
        : ShowHistory ? "Empty_TransferHistoryDescription" : "Empty_TransfersDescription"];
    public int UnfinishedCount => _engine.UnfinishedCount;
    public int PendingTransferCount => Badge.PendingCount;
    public bool ShowPendingTransferCount => Badge.ShowCount;
    public int ConnectionWorkCount(Guid id, bool writesOnly = false) => _engine.ConnectionWorkCount(id, writesOnly);
    public Task CancelConnectionAsync(Guid id, bool writesOnly = false) => _engine.CancelConnectionAsync(id, writesOnly);

    public bool HasFailedTransfers => Badge.HasFailures;
    public bool CanClearFailed => !_cleaningHistory && _engine.Jobs.Any(job =>
        TransferHistoryRules.CanRemove(job, TransferHistoryScope.Failed));
    public bool CanClearFinished => !_cleaningHistory && _engine.Jobs.Any(job =>
        TransferHistoryRules.CanRemove(job, TransferHistoryScope.Finished));
    public bool ShowClearFailed => ShowActive && CanClearFailed;
    public bool ShowClearFinished => ShowHistory && CanClearFinished;

    private TransferBadge Badge => TransferBadges.From(_engine.Jobs.Select(job => job.State));

    // Used by settings to keep this selector and the live engine synchronized.
    public void ApplyConcurrencyFromPreferences(int value)
    {
        // Changes made in Settings also supersede an in-flight save from this page.
        ++_concurrencyChangeVersion;
        var next = Math.Clamp(value, 1, 8);
        SetProperty(ref _concurrency, next);
        _engine.SetMaxConcurrent(next);
    }

    public int Concurrency
    {
        get => _concurrency;
        set
        {
            var next = Math.Clamp(value, 1, 8);
            if (_concurrency == next)
                return;
            var previous = _concurrency;
            ApplyConcurrencyFromPreferences(next);
            _state.Preferences.MaxConcurrentTransfers = next;
            _ = SaveConcurrencyAsync(previous, _concurrencyChangeVersion);
        }
    }

    private async Task SaveConcurrencyAsync(int previous, int changeVersion)
    {
        try
        {
            await _settings.SaveAsync(_state.Preferences);
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Queue("transfer-settings", error);
            // Do not undo a newer choice if an earlier write failed.
            if (changeVersion != _concurrencyChangeVersion)
                return;

            _state.Preferences.MaxConcurrentTransfers = previous;
            ApplyConcurrencyFromPreferences(previous);
            _toasts.Show(new ToastRequest { Title = _texts["Transfer_ConcurrencySaveFailed"], Tone = ToastTone.Error });
        }
    }

    public string SummaryText
    {
        get
        {
            var jobs = _engine.Jobs;
            var active = jobs.Count(job => job.State == TransferState.Running);
            var queued = jobs.Count(job => job.State is TransferState.Queued or TransferState.WaitingRetry);
            if (ShowHistory)
            {
                var completed = jobs.Count(job => job.State == TransferState.Completed && !job.Skipped);
                var skipped = jobs.Count(job => job.State == TransferState.Completed && job.Skipped);
                var history = completed + " " + _texts["Transfer_Completed"]
                    + " · " + jobs.Count(job => job.State == TransferState.Cancelled) + " " + _texts["Transfer_Cancelled"];
                if (skipped > 0)
                    history += " · " + string.Format(_texts["Transfer_NoticeSkipped"], skipped);
                return history;
            }
            var paused = jobs.Count(job => job.State == TransferState.Paused);
            var failed = jobs.Count(job => job.State == TransferState.Failed);
            var summary = string.Format(_texts["Transfer_Summary"], active, queued);
            if (paused > 0)
                summary += $" · {paused} {_texts["Transfer_Paused"]}";
            if (failed > 0)
                summary += $" · {failed} {_texts["Transfer_Failed"]}";
            return summary;
        }
    }

    public string FooterText
    {
        get
        {
            var jobs = _engine.Jobs;
            var active = jobs.Count(job => job.State == TransferState.Running);
            var queued = jobs.Count(job => job.State is TransferState.Queued or TransferState.WaitingRetry);
            if (active == 0 && queued == 0)
                return _texts["Status_NoTransfers"];
            return string.Format(_texts["Transfer_Footer"], active, queued);
        }
    }

    private void OnChanged(TransferJob job)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var latest = _engine.TryCurrent(job.Id);
            if (latest is not null)
                Apply(latest, NoteSpeed(latest));
            if (job.Kind == TransferKind.Copy)
                ScheduleBatchRefresh(job.BatchId);
            NotifySummary();
        });
    }

    private void OnRemoved(Guid id) => _dispatcher.BeginInvoke(() =>
    {
        if (_rows.TryGetValue(id, out var row) && row.Kind == TransferKind.Copy)
            ScheduleBatchRefresh(row.BatchId);
        if (_rows.Remove(id))
            Rebuild();
        _speeds.Remove(id);
        NotifySummary();
    });

    private void OnBatchRemoved(Guid id) => _dispatcher.BeginInvoke(() =>
    {
        _pendingBatchRefresh.Remove(id);
        if (_batchRows.Remove(id))
            RebuildBatches();
        NotifySummary();
    });

    private void OnBatchChanged(CrossTransferBatch batch) => _dispatcher.BeginInvoke(() => ApplyBatch(batch));

    private void ApplyBatch(CrossTransferBatch batch)
    {
        if (!_batchRows.TryGetValue(batch.Id, out var row))
        {
            row = new CrossBatchRowViewModel(batch, _texts);
            _batchRows[batch.Id] = row;
        }
        else
            row.Apply(batch, row.Progress);
        if (_announce)
            RebuildBatches();
        ScheduleBatchRefresh(batch.Id);
    }

    private void ScheduleBatchRefresh(Guid id)
    {
        _pendingBatchRefresh.Add(id);
        _batchRefreshTimer.Start();
    }

    private async void OnBatchRefreshTick(object? sender, EventArgs e)
    {
        _batchRefreshTimer.Stop();
        var ids = _pendingBatchRefresh.ToArray();
        _pendingBatchRefresh.Clear();
        foreach (var id in ids)
        {
            try
            {
                var progress = await _engine.GetBatchProgressAsync(id);
                if (_batchRows.TryGetValue(id, out var row))
                    row.Apply(row.Batch, progress);
            }
            catch (Exception error)
            {
                DiagnosticLog.Current.Queue("transfer", error);
            }
        }
        RebuildBatches();
        NotifySummary();
    }

    private void Apply(TransferJob job, double bytesPerSecond)
    {
        TransferState? previous = null;
        if (!_rows.TryGetValue(job.Id, out var row))
        {
            row = new TransferRowViewModel(job, _texts);
            _rows[job.Id] = row;
        }
        else
        {
            previous = row.State;
        }

        row.Apply(job, bytesPerSecond);
        if (previous != job.State && job.State == TransferState.Failed && job.Failure is { } failure)
            DiagnosticLog.Current.QueueTransferFailure(failure.Kind);
        if (_announce && previous != job.State)
        {
            var completed = job.State == TransferState.Completed && !job.Skipped;
            var skipped = job.State == TransferState.Completed && job.Skipped;
            var failed = job.State == TransferState.Failed && job.Failure?.Retryable != true;
            if (completed)
                _noticeBuffer.AddCompleted(job.DisplayName, _state.Preferences.NotifyTransferCompletion);
            if (skipped)
                _noticeBuffer.AddSkipped(_state.Preferences.NotifyTransferCompletion);
            if (failed)
                _noticeBuffer.AddFailed(job.DisplayName);
            if (((completed || skipped) && _state.Preferences.NotifyTransferCompletion) || failed)
            {
                // Restart the quiet window instead of issuing a toast for each file.
                _noticeTimer.Stop();
                _noticeTimer.Start();
            }
        }

        if (_announce && (previous is null || previous != job.State))
            Rebuild();
    }

    private void OnNoticeTick(object? sender, EventArgs e)
    {
        _noticeTimer.Stop();
        var snapshot = _noticeBuffer.Drain(_state.Preferences.NotifyTransferCompletion);
        if (snapshot.Completed > 0 || snapshot.Skipped > 0)
        {
            var title = snapshot.Completed == 0
                ? string.Format(_texts["Transfer_NoticeSkipped"], snapshot.Skipped)
                : snapshot.Completed == 1
                    ? string.Format(_texts["Transfer_Finished"], snapshot.CompletedName)
                    : string.Format(_texts["Transfer_NoticeCompleted"], snapshot.Completed);
            if (snapshot.Completed > 0 && snapshot.Skipped > 0)
                title += " · " + string.Format(_texts["Transfer_NoticeSkipped"], snapshot.Skipped);
            _toasts.Show(new ToastRequest
            {
                Title = title,
                Tone = snapshot.Completed > 0 ? ToastTone.Success : ToastTone.Information
            });
        }
        if (snapshot.Failed > 0)
            _toasts.Show(new ToastRequest
            {
                Title = snapshot.Failed == 1
                    ? string.Format(_texts["Transfer_CouldNotFinish"], snapshot.FailedName)
                    : string.Format(_texts["Transfer_NoticeFailed"], snapshot.Failed),
                Tone = ToastTone.Error,
                ActionLabelKey = OpenFailureDetails is null ? null : "Toast_ViewFailedTransfers",
                Action = OpenFailureDetails
            });
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            SyncCollection(Visible, _rows.Values
                .Where(row => TransferViewRules.Matches(row.State, ShowHistory, ActiveFilter))
                .OrderByDescending(row => row.CreatedAt).ToList());
            UpdateVisibleBatches();
        }
        finally
        {
            _rebuilding = false;
        }
        RebuildDisplayItems();
    }

    private void RebuildBatches()
    {
        _rebuilding = true;
        try
        {
            UpdateVisibleBatches();
        }
        finally
        {
            _rebuilding = false;
        }
        RebuildDisplayItems();
    }

    private void UpdateVisibleBatches()
    {
        var batches = _batchRows.Values
            .Where(row =>
            {
                var finished = row.Batch.State == CrossBatchState.Completed
                    && row.Progress.Open == 0 && row.Progress.Failed == 0;
                if (ShowHistory != finished)
                    return false;
                return ActiveFilter switch
                {
                    TransferStatusFilter.All => true,
                    TransferStatusFilter.Failed => row.CanRetry
                        || _rows.Values.Any(job => job.BatchId == row.Id &&
                            TransferViewRules.Matches(job.State, ShowHistory, ActiveFilter)),
                    TransferStatusFilter.Completed => row.Progress.Completed + row.Progress.Skipped > 0,
                    TransferStatusFilter.Cancelled => row.Progress.Cancelled > 0,
                    _ => _rows.Values.Any(job => job.BatchId == row.Id &&
                        TransferViewRules.Matches(job.State, ShowHistory, ActiveFilter))
                };
            })
            .OrderByDescending(row => row.Batch.UpdatedAt).ToList();
        SyncCollection(VisibleBatches, batches);
    }

    private void RebuildDisplayItems()
    {
        // One virtualized, flat list: batch headers and optionally their loaded jobs.
        // Completed jobs from a still-active batch stay discoverable in History.
        var desired = new List<object>(VisibleBatches.Count + Visible.Count);
        var grouped = new HashSet<Guid>();
        foreach (var batch in VisibleBatches)
        {
            grouped.Add(batch.Id);
            desired.Add(batch);
            batch.LoadedItemCount = Visible.Count(job => job.BatchId == batch.Id);
            if (!batch.IsExpanded)
                continue;
            foreach (var item in Visible.Where(job => job.BatchId == batch.Id))
            {
                item.IsGroupedChild = true;
                desired.Add(item);
            }
        }
        foreach (var item in Visible)
        {
            if (grouped.Contains(item.BatchId))
                continue;
            item.IsGroupedChild = false;
            desired.Add(item);
        }

        // Preserve item instances and realized containers instead of resetting the
        // ListBox on each transfer event. This also retains the user's scroll position.
        SyncCollection(DisplayItems, desired);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        // Remove hidden/finished rows before reordering the survivors. Otherwise
        // one removal near the top can cause thousands of ListBoxItem.Move events.
        var needed = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!needed.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;
            var existing = target.IndexOf(desired[i]);
            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(i, desired[i]);
        }
    }

    private double NoteSpeed(TransferJob job)
    {
        var now = DateTime.UtcNow;
        if (!_speeds.TryGetValue(job.Id, out var sample))
        {
            _speeds[job.Id] = new SpeedSample(now, job.BytesTransferred, 0);
            return 0;
        }

        var seconds = (now - sample.At).TotalSeconds;
        if (job.BytesTransferred < sample.Bytes)
        {
            // A retry or multipart reconciliation can legitimately move the
            // visible byte count back to the last durable checkpoint.
            _speeds[job.Id] = new SpeedSample(now, job.BytesTransferred, 0);
            return 0;
        }
        if (seconds < 0.4)
            return sample.Speed;
        var instant = (job.BytesTransferred - sample.Bytes) / seconds;
        var speed = sample.Speed <= 0 ? instant : sample.Speed * 0.65 + instant * 0.35;
        _speeds[job.Id] = new SpeedSample(now, job.BytesTransferred, speed);
        return speed;
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(UnfinishedCount));
        OnPropertyChanged(nameof(PendingTransferCount));
        OnPropertyChanged(nameof(ShowPendingTransferCount));
        OnPropertyChanged(nameof(HasFailedTransfers));
        OnPropertyChanged(nameof(CanClearFailed));
        OnPropertyChanged(nameof(CanClearFinished));
        OnPropertyChanged(nameof(ShowClearFailed));
        OnPropertyChanged(nameof(ShowClearFinished));
    }

    private void Run(Func<Guid, CancellationToken, Task> action, object? parameter)
    {
        if (parameter is Guid id)
            _ = RunObservedAsync(action, id);
    }

    private async Task RunObservedAsync(Func<Guid, CancellationToken, Task> action, Guid id)
    {
        try
        {
            await action(id, CancellationToken.None);
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Queue("transfer", error);
            _toasts.Show(new ToastRequest { Title = _texts["Transfer_ActionFailed"], Tone = ToastTone.Error });
        }
    }

    private async Task RemoveRecordAsync(Guid id)
    {
        if (_cleaningHistory || _engine.TryCurrent(id) is not { } job
            || !TransferHistoryRules.CanRemove(job, TransferHistoryScope.Single, id))
            return;
        _cleaningHistory = true;
        NotifySummary();
        try
        {
            var result = await _engine.RemoveTerminalJobAsync(id);
            ShowRemovalFeedback(result.JobIds.Count);
        }
        catch (Exception error)
        {
            ShowCleanupError(error);
        }
        finally
        {
            _cleaningHistory = false;
            NotifySummary();
        }
    }

    private async Task ClearHistoryAsync(TransferHistoryScope scope)
    {
        if (_cleaningHistory || scope is not (TransferHistoryScope.Failed or TransferHistoryScope.Finished))
            return;
        if (scope == TransferHistoryScope.Failed ? !CanClearFailed : !CanClearFinished)
            return;
        if (_dialogs.Show(null, new DialogRequest
            {
                Title = _texts[scope == TransferHistoryScope.Failed
                    ? "Transfer_ClearFailedTitle" : "Transfer_ClearHistoryTitle"],
                Message = _texts["Transfer_ClearRecordsMessage"],
                PrimaryText = _texts["Transfer_RemoveRecords"],
                SecondaryText = _texts["Dialog_Cancel"],
                Tone = DialogTone.Danger
            }) != true)
            return;

        _cleaningHistory = true;
        NotifySummary();
        try
        {
            var result = scope == TransferHistoryScope.Failed
                ? await _engine.ClearFailedAsync()
                : await _engine.ClearFinishedAsync();
            foreach (var batch in _batchRows.Keys)
                ScheduleBatchRefresh(batch);
            ShowRemovalFeedback(result.JobIds.Count);
        }
        catch (Exception error)
        {
            ShowCleanupError(error);
        }
        finally
        {
            _cleaningHistory = false;
            NotifySummary();
        }
    }

    private void ShowRemovalFeedback(int count) =>
        _toasts.Show(new ToastRequest
        {
            Title = count > 0
                ? string.Format(_texts["Transfer_RemovedCount"], count)
                : _texts["Transfer_NothingRemovable"],
            Tone = ToastTone.Information
        });

    private void ShowCleanupError(Exception error)
    {
        DiagnosticLog.Current.Queue("transfer", error);
        _toasts.Show(new ToastRequest
        {
            Title = _texts["Transfer_ActionFailed"],
            Tone = ToastTone.Error
        });
    }

    private void CopyFailure(object? parameter)
    {
        if (parameter is not Guid id || _engine.TryCurrent(id) is not { Failure: { } failure } job)
            return;
        try
        {
            Clipboard.SetText(TransferFailureDiagnostics.CopyText(failure, job.Attempt));
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Transfer_FailureCopied"],
                Tone = ToastTone.Information
            });
        }
        catch (Exception error)
        {
            ShowCleanupError(error);
        }
    }

    private readonly record struct SpeedSample(DateTime At, long Bytes, double Speed);
}

public sealed class CrossBatchRowViewModel : ViewModelBase
{
    private readonly LocalizationService _texts;
    public CrossTransferBatch Batch { get; private set; }
    public CrossBatchProgress Progress { get; private set; } = new(0, 0, 0, 0, 0, 0);

    public CrossBatchRowViewModel(CrossTransferBatch batch, LocalizationService texts)
    {
        Batch = batch;
        _texts = texts;
    }

    private bool _isExpanded;
    private int _loadedItemCount;
    public int LoadedItemCount
    {
        get => _loadedItemCount;
        set
        {
            if (SetProperty(ref _loadedItemCount, value))
                OnPropertyChanged(nameof(ShowUnavailableHint));
        }
    }
    public bool ShowUnavailableHint => IsExpanded && LoadedItemCount == 0;
    public Guid Id => Batch.Id;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandHint));
                OnPropertyChanged(nameof(ShowUnavailableHint));
            }
        }
    }
    public string ExpandHint => _texts[IsExpanded ? "Transfer_HideItems" : "Transfer_ShowItems"];
    public string SourcePath => Batch.SourceBucket;
    public string DestinationPath => string.IsNullOrEmpty(Batch.DestinationPrefix)
        ? Batch.DestinationBucket : $"{Batch.DestinationBucket}/{Batch.DestinationPrefix}";
    public string RouteText => $"{SourcePath} → {DestinationPath}";
    public string Title => _texts[Batch.Move ? "Transfer_MoveBatchTitle" : "Transfer_BatchTitle"];
    public string Detail => string.Format(_texts[Batch.Move ? "Transfer_MoveBatchProgress" : "Transfer_BatchProgress"],
        Progress.Queued, Progress.Completed, Progress.Skipped, Progress.Failed, Progress.Open);
    // A failed scan may leave unenumerated objects; its queued-job percentage
    // must not be represented as completion of the entire requested folder.
    public bool ShowBatchProgress => Batch.State != CrossBatchState.Failed;
    public bool IsProgressIndeterminate => Batch.State == CrossBatchState.Scanning;
    public double ProgressPercent => Batch.State == CrossBatchState.Scanning ? 0 : Progress.Percent;
    public string ScanStatus => _texts[Batch.State switch
    {
        CrossBatchState.Scanning => "Transfer_BatchScanning",
        CrossBatchState.Failed => "Transfer_BatchScanFailed",
        _ => "Transfer_BatchScanComplete"
    }];
    public bool CanRetry => Batch.State == CrossBatchState.Failed || Progress.Failed > 0;
    // Batch.Error is a persisted technical identifier, not localized UI copy.
    public string? Error
    {
        get
        {
            if (Batch.State != CrossBatchState.Failed || string.IsNullOrWhiteSpace(Batch.Error))
                return null;
            var kindName = Batch.Error.Split(':', 2)[0];
            return Enum.TryParse<StorageFailureKind>(kindName, out var kind) && Enum.IsDefined(kind)
                ? _texts["Connection_Failure_" + kind]
                : _texts["Transfer_BatchScanError"];
        }
    }
    public string? TechnicalError
    {
        get
        {
            if (Batch.State != CrossBatchState.Failed || string.IsNullOrWhiteSpace(Batch.Error))
                return null;
            var parts = Batch.Error.Split(':', 2);
            var type = TransferFailureDiagnostics.SafeCode(new TransferFailure(
                TransferFailureKind.Unknown, false, parts[0], null));
            var code = parts.Length == 2
                ? TransferFailureDiagnostics.SafeCode(new TransferFailure(
                    TransferFailureKind.Unknown, false, parts[1], null))
                : null;
            return type is null ? null : code is null ? type : type + ": " + code;
        }
    }

    public void Apply(CrossTransferBatch batch, CrossBatchProgress progress)
    {
        Batch = batch;
        Progress = progress;
        RefreshText();
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ExpandHint));
        OnPropertyChanged(nameof(RouteText));
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(DestinationPath));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ShowBatchProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ScanStatus));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(TechnicalError));
    }
}

public sealed class TransferRowViewModel : ViewModelBase
{
    private readonly LocalizationService _texts;
    private TransferJob _job;
    private double _bytesPerSecond;

    public TransferRowViewModel(TransferJob job, LocalizationService texts)
    {
        _job = job;
        _texts = texts;
    }

    private bool _isDetailsExpanded;
    private bool _isGroupedChild;
    public Guid Id => _job.Id;
    public Guid BatchId => _job.BatchId;
    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set
        {
            if (SetProperty(ref _isDetailsExpanded, value))
                OnPropertyChanged(nameof(DetailsHint));
        }
    }
    public bool IsGroupedChild
    {
        get => _isGroupedChild;
        set
        {
            if (SetProperty(ref _isGroupedChild, value))
                OnPropertyChanged(nameof(ChildMargin));
        }
    }
    public Thickness ChildMargin => IsGroupedChild ? new Thickness(18, 0, 0, 0) : new Thickness(0);
    public string DetailsHint => _texts[IsDetailsExpanded ? "Transfer_HideDetails" : "Transfer_ShowDetails"];
    public string SourcePath => _job.Kind switch
    {
        TransferKind.Upload => _job.LocalPath,
        TransferKind.Copy or TransferKind.Move when _job.CrossTransfer is { } copy =>
            RemotePath(copy.Source.Bucket, copy.Source.Key),
        _ => RemotePath(_job.Bucket, _job.Key)
    };
    public string DestinationPath => _job.Kind switch
    {
        TransferKind.Download => _job.LocalPath,
        TransferKind.Copy or TransferKind.Move when _job.CrossTransfer is { } copy =>
            RemotePath(copy.Destination.Bucket, copy.Destination.Key),
        _ => RemotePath(_job.Bucket, _job.Key)
    };
    public string RouteText => $"{SourcePath} → {DestinationPath}";
    private static string RemotePath(string bucket, string key) =>
        string.IsNullOrEmpty(key) ? bucket : $"{bucket}/{key}";
    public DateTimeOffset CreatedAt => _job.CreatedAt;
    public TransferState State => _job.State;
    public TransferKind Kind => _job.Kind;
    public string Name => _job.DisplayName;
    public string StatusText => BuildStatus();
    public string DetailText => BuildDetail();
    public double Progress => _job.TotalBytes is > 0 ? Math.Clamp(_job.BytesTransferred * 100d / _job.TotalBytes.Value, 0, 100) : 0;
    public bool ShowPercentage => ShowProgress && !IsIndeterminate;
    public bool IsFailure => _job.State == TransferState.Failed;
    public bool IsIndeterminate => _job.State == TransferState.Running
        && (_job.TotalBytes is null or 0 || (_job.Kind == TransferKind.Upload && _job.BytesTransferred == 0));
    public bool ShowProgress => _job.State is TransferState.Running or TransferState.Paused or TransferState.WaitingRetry
        || (_job.State == TransferState.Completed && !_job.Skipped);
    public bool CanPause => _job.State is TransferState.Queued or TransferState.Running or TransferState.WaitingRetry;
    public bool CanResume => _job.State == TransferState.Paused;
    public bool CanRetry => _job.State == TransferState.Failed;
    public bool CanCancel => _job.IsOpen;
    public bool CanRemoveRecord => TransferHistoryRules.CanRemove(
        _job, TransferHistoryScope.Single, _job.Id);
    public string FailureCategory => _job.Failure?.Kind.ToString() ?? nameof(TransferFailureKind.Unknown);
    public string FailureCode => TransferFailureDiagnostics.SafeCode(_job.Failure)
        ?? _texts["Transfer_ErrorUnavailable"];
    public string FailureAttempts => _job.Attempt.ToString();
    public string FailureRetryable => _texts[_job.Failure?.Retryable == true ? "Transfer_Yes" : "Transfer_No"];
    public string FailureAdvice => _texts[_job.Failure?.Kind switch
    {
        TransferFailureKind.AccessDenied or TransferFailureKind.ReadOnly =>
            "Transfer_AdvicePermission",
        TransferFailureKind.Network or TransferFailureKind.Throttled =>
            "Transfer_AdviceNetwork",
        TransferFailureKind.LocalMissing or TransferFailureKind.InvalidPath or TransferFailureKind.Busy =>
            "Transfer_AdviceLocal",
        TransferFailureKind.Conflict or TransferFailureKind.Integrity =>
            "Transfer_AdviceConflict",
        _ => "Transfer_AdviceGeneral"
    }];

    public void Apply(TransferJob job, double bytesPerSecond)
    {
        _job = job;
        _bytesPerSecond = bytesPerSecond;
        RefreshText();
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(DestinationPath));
        OnPropertyChanged(nameof(RouteText));
        OnPropertyChanged(nameof(DetailsHint));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ShowPercentage));
        OnPropertyChanged(nameof(IsFailure));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRemoveRecord));
        OnPropertyChanged(nameof(FailureCategory));
        OnPropertyChanged(nameof(FailureCode));
        OnPropertyChanged(nameof(FailureAttempts));
        OnPropertyChanged(nameof(FailureRetryable));
        OnPropertyChanged(nameof(FailureAdvice));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Kind));
    }

    private string BuildStatus()
    {
        var state = _job.State switch
        {
            TransferState.Queued => _texts["Transfer_QueuedState"],
            TransferState.Running when _job.Kind == TransferKind.Copy =>
                _texts[_job.CrossTransfer?.Stage switch
                {
                    CrossTransferStage.Removing => "Transfer_CrossRemoving",
                    CrossTransferStage.Verifying => "Transfer_CrossVerifying",
                    _ => _job.CrossTransfer?.Move == true
                        ? "Transfer_CrossMoving" : "Transfer_CrossCopying"
                }],
            TransferState.Running => _texts[_job.Kind == TransferKind.Download ? "Transfer_Downloading" : "Transfer_Uploading"],
            TransferState.Paused => _texts["Transfer_Paused"],
            TransferState.WaitingRetry => _texts["Transfer_Waiting"],
            TransferState.Completed when _job.Skipped => _texts["Transfer_Skipped"],
            TransferState.Completed => _texts["Transfer_Completed"],
            TransferState.Failed => _texts["Transfer_Failed"],
            TransferState.Cancelled => _texts["Transfer_Cancelled"],
            _ => _texts["Transfer_UnknownState"]
        };
        var direction = _texts[_job.Kind switch
        {
            TransferKind.Download => "Transfer_Download",
            TransferKind.Copy => _job.CrossTransfer?.Move == true
                ? "Transfer_CrossMove" : "Transfer_CrossCopy",
            _ => "Transfer_Upload"
        }];
        // Running already says Uploading / Downloading / Copying; avoid redundant
        // combinations such as "Upload · Uploading".
        return _job.State == TransferState.Running ? state : $"{direction} · {state}";
    }

    private string BuildDetail()
    {
        var size = _job.TotalBytes is long total
            ? $"{ExplorerText.FormatBytes(_job.BytesTransferred)} / {ExplorerText.FormatBytes(total)}"
            : ExplorerText.FormatBytes(_job.BytesTransferred);
        if (_job.State == TransferState.Failed)
            return FailureText();
        if (_job.State == TransferState.Completed && !_job.Skipped)
            return $"{size} · {_texts[_job.Kind == TransferKind.Copy && _job.CrossTransfer?.Verification == "SHA256"
                ? "Transfer_CrossChecksumVerified"
                : _job.Kind == TransferKind.Download && _job.Remote?.ETagIsMd5 == true
                    ? "Transfer_ChecksumVerified" : "Transfer_SizeVerified"]}";
        if (_job.State == TransferState.Running && _bytesPerSecond > 1)
            return $"{size} · {ExplorerText.FormatBytes((long)_bytesPerSecond)}/s";
        if (_job.State is TransferState.Running or TransferState.Paused or TransferState.WaitingRetry or TransferState.Completed)
            return size;
        return _job.Key;
    }

    private string FailureText() => _job.Failure?.Kind switch
    {
        TransferFailureKind.Network => _texts["Transfer_FailureNetwork"],
        TransferFailureKind.Throttled => _texts["Transfer_FailureThrottled"],
        TransferFailureKind.AccessDenied => _texts["Transfer_FailureDenied"],
        TransferFailureKind.NotFound => _texts["Transfer_FailureMissing"],
        TransferFailureKind.Conflict => _texts["Transfer_FailureConflict"],
        TransferFailureKind.Integrity => _texts["Transfer_FailureIntegrity"],
        TransferFailureKind.LocalMissing => _texts["Transfer_FailureLocal"],
        TransferFailureKind.Busy => _texts["Transfer_FailureBusy"],
        TransferFailureKind.Disk => _texts["Transfer_FailureDisk"],
        TransferFailureKind.ReadOnly => _texts["Transfer_ReadOnly"],
        TransferFailureKind.ConnectionMissing => _texts["Transfer_FailureConnection"],
        TransferFailureKind.InvalidPath => _texts["Transfer_FailurePath"],
        _ => _texts["Transfer_FailureUnknown"]
    };

}

public sealed class TransferFilterChoice : ViewModelBase
{
    private readonly LocalizationService _texts;
    private readonly string _key;

    internal TransferFilterChoice(TransferStatusFilter filter, string key, LocalizationService texts)
    {
        Filter = filter;
        _key = key;
        _texts = texts;
    }

    internal TransferStatusFilter Filter { get; }
    public string Label => _texts[_key];
    internal void Refresh() => OnPropertyChanged(nameof(Label));
}
