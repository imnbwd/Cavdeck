using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.App.Features.Preview;
using Cavdeck.Application.Preview;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Places;
using Cavdeck.Application.Sharing;
using Cavdeck.App.Features.Sharing;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.App.Features.Explorer;

public sealed partial class ExplorerViewModel
{
    private string? _dropPrefix;
    private ExplorerRow? _dropRow;

    public void SetDropTarget(bool active, string? prefix = null, ExplorerRow? folder = null)
    {
        if (!active || !CanWriteToCurrentDirectory)
        {
            active = false;
            prefix = null;
            folder = null;
        }
        if (IsDropTarget == active && _dropPrefix == prefix && ReferenceEquals(_dropRow, folder))
            return;

        _dropRow?.SetDropTarget(false);
        _dropRow = folder;
        _dropRow?.SetDropTarget(true);
        _dropPrefix = prefix;
        IsDropTarget = active;
        OnPropertyChanged(nameof(IsDropTarget));
        OnPropertyChanged(nameof(DropDestination));
    }

    public async Task QueueDroppedUploadsAsync(IReadOnlyList<string> paths, string destinationPrefix)
    {
        if (!CanWriteToCurrentDirectory || _connection is null || _bucket is null || paths.Count == 0)
            return;
        // Only the current directory or a visible folder in the current listing.
        if (destinationPrefix != _prefix && !Rows.Any(row =>
                row.IsFolder && row.Entry.Key == destinationPrefix))
            return;
        try
        {
            await QueueUploadsToAsync(_connection, _bucket, destinationPrefix, paths);
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Transfer_Rejected"],
                Message = _texts["Transfer_FailureUnknown"],
                Tone = ToastTone.Error
            });
        }
    }

    public async Task QueueUploadsAsync(IReadOnlyList<string> paths)
    {
        if (!CanWrite || _connection is null || _bucket is null || paths.Count == 0)
            return;
        await QueueUploadsToAsync(_connection, _bucket, _prefix, paths);
    }

    private async Task QueueUploadsToAsync(StorageConnection connection, string bucket, string prefix,
        IReadOnlyList<string> paths, uint? clipboardSequence = null)
    {
        var generation = _generation;
        var browsingPrefix = _prefix;
        var conflict = await ResolveTransferConflictAsync(token =>
            _transfers.FindUploadConflictAsync(connection, bucket, prefix, paths, token));
        if (conflict is null)
            return;
        if (!CanWrite || _generation != generation || !ReferenceEquals(_connection, connection)
            || _bucket != bucket || _prefix != browsingPrefix
            || clipboardSequence is { } sequence && WindowsFileClipboard.SequenceNumber() != sequence)
            return;
        try
        {
            var jobs = await _transfers.EnqueueUploadsAsync(
                connection, bucket, prefix, paths.ToArray(), conflict.Value);
            ReportQueued(jobs);
            TrackSelectionForJobs(jobs);
        }
        catch (StorageException exception)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Transfer_Rejected"],
                Message = exception.Failure.Kind == StorageFailureKind.ReadOnly
                    ? _texts["Transfer_ReadOnly"]
                    : _texts["Transfer_FailureUnknown"],
                Tone = ToastTone.Error
            });
        }
    }

    public Task QueueDownloadsAsync(string directory)
    {
        if (!CanDownload || _connection is null || _bucket is null)
            return Task.CompletedTask;
        var items = _selected.Select(row => new DownloadItem(
            row.Entry.Key, row.IsFolder, row.IsFolder ? null : row.Entry.SizeBytes)).ToArray();
        return EnqueueDownloadsAsync(directory, items);
    }

    public Task DownloadOpenObjectAsync(string directory)
    {
        if (_reconnectPending || _connection is null || _bucket is null || !_preview.IsOpen)
            return Task.CompletedTask;
        return EnqueueDownloadsAsync(directory, [new DownloadItem(_preview.Key, false, _preview.Size)]);
    }

    private async Task EnqueueDownloadsAsync(string directory, IReadOnlyList<DownloadItem> items)
    {
        if (_connection is null || _bucket is null || string.IsNullOrWhiteSpace(directory) || items.Count == 0)
            return;
        var connection = _connection;
        var bucket = _bucket;
        var generation = _generation;
        var conflict = await ResolveTransferConflictAsync(token =>
            _transfers.FindDownloadConflictAsync(connection, bucket, items, directory, token));
        if (conflict is null || _generation != generation || !ReferenceEquals(_connection, connection)
            || _bucket != bucket)
            return;
        try
        {
            var jobs = await _transfers.EnqueueDownloadsAsync(
                connection, bucket, items, directory, conflict.Value);
            ReportQueued(jobs);
        }
        catch (StorageException)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Transfer_Rejected"],
                Tone = ToastTone.Error
            });
        }
    }

    private void ReportQueued(IReadOnlyList<TransferJob> jobs)
    {
        var failed = jobs.Count(job => job.State == TransferState.Failed);
        var queued = jobs.Count - failed;
        if (queued > 0)
        {
            _toasts.Show(new ToastRequest
            {
                Title = string.Format(_texts["Transfer_Queued"], queued),
                Tone = ToastTone.Success
            });
        }

        if (failed > 0)
        {
            _toasts.Show(new ToastRequest
            {
                Title = string.Format(_texts["Transfer_NotQueued"], failed),
                Tone = ToastTone.Warning
            });
        }
    }

    private void ReportCrossQueued(IReadOnlyList<TransferJob> jobs, bool move = false)
    {
        if (jobs.Count == 0)
            return;
        _toasts.Show(new ToastRequest
        {
            Title = _texts[move ? "Explorer_CrossMoveBatchQueued" : "Explorer_CrossBatchQueued"],
            Message = _texts[move ? "Explorer_CrossMoveQueuedHint" : "Explorer_CrossBatchQueuedHint"],
            Tone = ToastTone.Information
        });
    }

    private void TrackCopyBatchResult(Guid batchId)
    {
        if (_disposed)
            return;
        _copyBatchesAwaitingResult.Add(batchId);
        RunUiOperation(() => ReportCopyBatchResultAsync(batchId));
    }

    private void OnCopyBatchChanged(CrossTransferBatch batch)
    {
        if (_disposed)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && _copyBatchesAwaitingResult.Contains(batch.Id)
                && batch.State != CrossBatchState.Scanning)
                RunUiOperation(() => ReportCopyBatchResultAsync(batch.Id));
        });
    }

    private async Task ReportCopyBatchResultAsync(Guid batchId)
    {
        if (_disposed || !_copyBatchesAwaitingResult.Contains(batchId))
            return;

        // Scanning completion and transfer completion are independent.
            // Do not report the result before every descendant was queued.
            var batch = _transfers.Batches.FirstOrDefault(item => item.Id == batchId);
            if (batch is null || batch.State == CrossBatchState.Scanning)
                return;

            var progress = await _transfers.GetBatchProgressAsync(batchId);
            if (_disposed || progress.Open > 0 || !_copyBatchesAwaitingResult.Remove(batchId))
                return;

            var failures = progress.Failed + progress.Cancelled
                + (batch.State == CrossBatchState.Failed ? 1 : 0);
            if (progress.Queued == 0 && failures == 0)
            {
                _toasts.Show(new ToastRequest
                {
                    Title = _texts["Explorer_CopyNothingToDo"],
                    Tone = ToastTone.Information
                });
            }
            else if (failures > 0)
            {
                _toasts.Show(new ToastRequest
                {
                    Title = _texts["Explorer_PartialTitle"],
                    Message = string.Format(_texts["Explorer_ResultPartial"],
                        progress.Completed, progress.Skipped, failures),
                    Tone = ToastTone.Warning,
                    ActionLabelKey = "Toast_ViewFailedTransfers",
                    Action = () => OpenFailedTransfersRequested?.Invoke(this, EventArgs.Empty)
                });
            }
            else
            {
                _toasts.Show(new ToastRequest
                {
                    Title = string.Format(_texts["Explorer_ResultDone"],
                        progress.Completed, progress.Skipped),
                    Tone = progress.Skipped > 0 ? ToastTone.Warning : ToastTone.Success
                });
            }
    }

    private string CrossCopyError(Exception exception) => exception is StorageException storage
        ? ExplorerText.FailureTitle(_texts, storage.Failure)
        : exception is InvalidOperationException ? _texts["Transfer_FailureConflict"]
        : _texts["Transfer_FailureUnknown"];

    private async Task<TransferConflictPolicy?> ResolveTransferConflictAsync(
        Func<CancellationToken, Task<string?>> probe)
    {
        // Explicit remembered policies do not require a network preflight.
        if (_state.Preferences.TransferConflict == TransferConflictPreference.Skip)
            return TransferConflictPolicy.Skip;
        if (_state.Preferences.TransferConflict == TransferConflictPreference.Rename)
            return TransferConflictPolicy.Rename;

        if (_operation is not null)
            return null;

        string? existingName;
        using (var operation = new CancellationTokenSource())
        {
            _operation = operation;
            _checkingTransferConflicts = true;
            SetPreparing(true);
            SetBusy(true, more: false);
            try
            {
                existingName = await probe(operation.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception)
            {
                // A failed check is not permission to overwrite an unknown target.
                _toasts.Show(new ToastRequest
                {
                    Title = _texts["Transfer_Rejected"],
                    Message = _texts["Transfer_ConflictCheckFailed"],
                    Tone = ToastTone.Error
                });
                return null;
            }
            finally
            {
                _operation = null;
                _checkingTransferConflicts = false;
                SetPreparing(false);
                SetBusy(false, more: false);
            }
        }

        // Re-checks at execution time will use Keep both to handle late collisions
        // when the preflight found no matching object.
        if (existingName is null)
            return TransferConflictPolicy.Rename;
        return await ChooseCopyConflictAsync(existingName);
    }

    private async Task<TransferConflictPolicy?> ChooseCopyConflictAsync(string existingName, bool summary = false)
    {
        if (_state.Preferences.TransferConflict == TransferConflictPreference.Skip)
            return TransferConflictPolicy.Skip;
        if (_state.Preferences.TransferConflict == TransferConflictPreference.Rename)
            return TransferConflictPolicy.Rename;

        var choice = AskConflict(existingName, summary);
        if (choice is null)
            return null;

        if (choice.Value.Remember && choice.Value.Policy != TransferConflictPolicy.Overwrite)
        {
            var previous = _state.Preferences.TransferConflict;
            _state.Preferences.TransferConflict = choice.Value.Policy == TransferConflictPolicy.Skip
                ? TransferConflictPreference.Skip : TransferConflictPreference.Rename;
            try
            {
                await _settings.SaveAsync(_state.Preferences);
                _state.NotifyExplorerDisplayChanged();
            }
            catch (Exception)
            {
                _state.Preferences.TransferConflict = previous;
                _toasts.Show(new ToastRequest
                {
                    Title = _texts["Transfer_ConflictPreferenceNotSaved"],
                    Tone = ToastTone.Warning
                });
            }
        }

        return choice.Value.Policy;
    }

    private (TransferConflictPolicy Policy, bool Remember)? AskConflict(string existingName, bool summary)
    {
        var skip = new System.Windows.Controls.RadioButton
        {
            Content = _texts["Transfer_Skip"],
            GroupName = "TransferConflict",
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 32
        };
        var rename = new System.Windows.Controls.RadioButton
        {
            Content = _texts["Transfer_Rename"],
            IsChecked = true,
            GroupName = "TransferConflict",
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 32
        };
        var overwrite = new System.Windows.Controls.RadioButton
        {
            Content = _texts["Transfer_Overwrite"],
            GroupName = "TransferConflict",
            MinHeight = 32
        };
        var remember = new System.Windows.Controls.CheckBox
        {
            Content = _texts["Transfer_RememberChoice"],
            Margin = new Thickness(0, 12, 0, 0),
            MinHeight = 28
        };
        overwrite.Checked += (_, _) => { remember.IsChecked = false; remember.IsEnabled = false; };
        rename.Checked += (_, _) => remember.IsEnabled = true;
        skip.Checked += (_, _) => remember.IsEnabled = true;

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(skip);
        panel.Children.Add(rename);
        panel.Children.Add(overwrite);
        panel.Children.Add(remember);
        var choice = _dialogs.ShowChoice(null, new DialogRequest
        {
            Title = _texts["Transfer_ConflictTitle"],
            Message = summary ? existingName : string.Format(_texts["Transfer_ConflictDetectedMessage"], existingName),
            Content = panel,
            PrimaryText = _texts["Transfer_Start"]
        });
        if (choice != DialogChoice.Primary)
            return null;
        if (overwrite.IsChecked == true)
            return (TransferConflictPolicy.Overwrite, false);
        if (rename.IsChecked == true)
            return (TransferConflictPolicy.Rename, remember.IsChecked == true);
        return (TransferConflictPolicy.Skip, remember.IsChecked == true);
    }

    private void OnTransferChanged(TransferJob job)
    {
        if (job.Kind is not (TransferKind.Upload or TransferKind.Copy)
            || job.State is not (TransferState.Completed or TransferState.Failed or TransferState.Cancelled))
            return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_copyBatchesAwaitingResult.Contains(job.BatchId))
                RunUiOperation(() => ReportCopyBatchResultAsync(job.BatchId));
            var select = _selectOnCompletion.Remove(job.Id);
            if (job.State != TransferState.Completed || job.Skipped)
                return;
            var destinationVisible = _connection is { } connection && _bucket is { } bucket
                && ExplorerTransferRefreshRules.DestinationAffected(job, connection.Id, bucket, _prefix);
            var sourceVisible = _connection is { } sourceConnection && _bucket is { } sourceBucket
                && ExplorerTransferRefreshRules.SourceAffected(job, sourceConnection.Id, sourceBucket, _prefix);
            if (!destinationVisible && !sourceVisible)
                return;
            if (select && destinationVisible)
                _pendingTransferKeys.Add(job.Key);
            ScheduleTransferRefresh();
        });
    }

    private void OnStorageChanged(object? sender, ExplorerStorageChanged change)
    {
        if (_disposed || ReferenceEquals(sender, this))
            return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && _connection?.Id == change.ConnectionId && _bucket == change.Bucket)
                ScheduleTransferRefresh();
        });
    }

    private void ScheduleTransferRefresh()
    {
        _refreshDelay?.Cancel();
        _refreshDelay?.Dispose();
        _refreshDelay = new CancellationTokenSource();
        var token = _refreshDelay.Token;
        RunUiOperation(() => RefreshAfterTransferAsync(token));
    }

    private async Task RefreshAfterTransferAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(400, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!CanRefresh)
            return;
        await RefreshAsync();
        if (cancellationToken.IsCancellationRequested)
            return;
        var keys = _pendingTransferKeys.ToArray();
        SelectVisibleKeys(keys);
        if (_selectOnCompletion.Count == 0)
            _pendingTransferKeys.ExceptWith(keys);
    }

    private void TrackSelectionForJobs(IReadOnlyList<TransferJob> jobs)
    {
        foreach (var job in jobs.Where(job => job.Kind is TransferKind.Upload or TransferKind.Copy
                     && job.State is not (TransferState.Failed or TransferState.Cancelled) && !job.Skipped))
        {
            _selectOnCompletion.Add(job.Id);
            // A small upload can finish before EnqueueUploadsAsync returns.
            if (_transfers.TryCurrent(job.Id) is { State: TransferState.Completed } completed)
                OnTransferChanged(completed);
        }
    }

    private void SelectVisibleKeys(IEnumerable<string> keys)
    {
        if (_bucket is null)
            return;
        var visible = keys.Select(key => VisibleKey(key, _prefix))
            .Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
        if (HasFilter && _loaded.Any(entry => visible.Contains(entry.Key)
            && Rows.All(row => row.Entry.Key != entry.Key)))
            Filter = string.Empty;
        var rows = Rows.Where(row => visible.Contains(row.Entry.Key)).ToArray();
        if (rows.Length == 0)
            return;
        if (RequestSelection(rows))
            SelectionSyncRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string? VisibleKey(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length)
            return null;
        var remainder = key[prefix.Length..];
        var slash = remainder.IndexOf('/');
        return slash < 0 ? key : prefix + remainder[..(slash + 1)];
    }

}
