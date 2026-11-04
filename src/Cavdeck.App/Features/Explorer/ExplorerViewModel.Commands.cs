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
    private async Task NewFolderAsync()
    {
        if (!CanWrite || _connection is null || _bucket is null)
            return;
        var snapshot = _connection;
        var name = ExplorerPrompt.Ask(
            _dialogs,
            _texts["Explorer_NewFolderTitle"],
            _texts["Explorer_NewFolderMessage"],
            _texts["Explorer_NameLabel"],
            "",
            _texts["Explorer_Create"],
            _texts["Dialog_Cancel"],
            raw => ValidateName(raw, folder: true, ignore: null));
        if (name is null || !CanWrite || !ReferenceEquals(snapshot, _connection))
            return;

        await RunTrackedMutation(token => RunMutation(
            _ => _explorer.CreateFolderAsync(snapshot, _bucket, _prefix, name, token),
            resolveConflicts: false));
    }

    private async Task NewTextFileAsync()
    {
        if (!CanWrite || _connection is null || _bucket is null)
            return;
        var snapshot = _connection;
        var name = ExplorerPrompt.Ask(
            _dialogs,
            _texts["Explorer_NewTextFile"],
            _texts["Explorer_NewTextFileMessage"],
            _texts["Explorer_NameLabel"],
            "untitled.txt",
            _texts["Explorer_Create"],
            _texts["Dialog_Cancel"],
            raw => ValidateName(raw, folder: false, ignore: null));
        if (name is null || !CanWrite || !ReferenceEquals(snapshot, _connection))
            return;

        await RunTrackedMutation(token => RunMutation(
            _ => _explorer.CreateTextFileAsync(snapshot, _bucket, _prefix, name, token),
            resolveConflicts: false));
    }

    private async Task DeleteAsync()
    {
        if (!CanDelete || _connection is null || _bucket is null)
            return;
        if (_preview.IsDirty)
        {
            _afterLeave = DeleteAsync;
            _preview.MarkLeavePending();
            return;
        }
        var connection = _connection;
        var bucket = _bucket;
        var generation = _generation;
        var entries = SelectedEntries();
        DeletePreview preview;
        using (var operation = new CancellationTokenSource())
        {
            _operation = operation;
            SetPreparing(true);
            SetBusy(true, more: false);
            try
            {
                preview = await _explorer.PreviewDeleteAsync(connection, bucket, entries,
                    operation.Token, OnOperationProgress);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Surface(exception, keepList: true);
                return;
            }
            finally
            {
                _operation = null;
                SetPreparing(false);
                SetBusy(false, more: false);
            }
        }
        if (generation != _generation || _connection?.Id != connection.Id || _bucket != bucket)
            return;
        if (preview.Keys.Count == 0)
            return;
        var title = entries.Count == 1
            ? string.Format(_texts["Explorer_DeleteOneTitle"], entries[0].Name)
            : string.Format(_texts["Explorer_DeleteManyTitle"], entries.Count);
        var message = string.Format(_texts["Explorer_DeletePreview"], preview.Keys.Count,
            ObjectLayout.ObjectUri(preview.Bucket,
                entries.Count == 1 ? entries[0].Key : _searchMode ? Root : _prefix), preview.ListRequests);
        var accepted = _dialogs.Show(null, new DialogRequest
        {
            Title = title,
            Message = message,
            PrimaryText = _texts["Explorer_Delete"],
            SecondaryText = _texts["Dialog_Cancel"],
            Tone = DialogTone.Danger
        });
        if (accepted != true || !CanWrite || _disposed
            || generation != _generation || !ReferenceEquals(connection, _connection))
            return;

        await RunTrackedMutation(token => RunMutation(
            _ => _explorer.DeletePreparedAsync(connection, preview, token, OnCopyProgress),
            resolveConflicts: false, previewed: true));
    }

    private async Task RenameAsync()
    {
        if (!CanRename || _connection is null || _bucket is null)
            return;
        if (_preview.IsDirty)
        {
            _afterLeave = RenameAsync;
            _preview.MarkLeavePending();
            return;
        }
        var connection = _connection;
        var bucket = _bucket;
        var generation = _generation;
        var entry = _selected[0].Entry;
        var snapshot = _connection;
        var name = ExplorerPrompt.Ask(
            _dialogs,
            _texts["Explorer_RenameTitle"],
            _texts["Explorer_RenameMessage"],
            _texts["Explorer_NameLabel"],
            entry.Name,
            _texts["Explorer_Rename"],
            _texts["Dialog_Cancel"],
            raw => ValidateName(raw, entry.Kind == ObjectEntryKind.Folder, entry,
                ObjectLayout.ParentPrefix(entry.Key) ?? Root));
        if (name is null || !CanWrite || !ReferenceEquals(snapshot, _connection)
            || string.Equals(name, entry.Name, StringComparison.Ordinal))
            return;

        RenamePreview preview;
        using (var operation = new CancellationTokenSource())
        {
            _operation = operation;
            SetPreparing(true);
            SetBusy(true, more: false);
            try
            {
                preview = await _explorer.PreviewRenameAsync(connection, bucket, entry, name,
                    operation.Token, OnOperationProgress);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Surface(exception, keepList: true);
                return;
            }
            finally
            {
                _operation = null;
                SetPreparing(false);
                SetBusy(false, more: false);
            }
        }
        if (generation != _generation || _connection?.Id != connection.Id || _bucket != bucket)
            return;
        if (preview.Issues.Count > 0 || preview.Items.Count == 0)
        {
            Present(new MutationReport(0, 0, preview.Issues.Count, false, 0, preview.Issues));
            return;
        }
        var choice = _dialogs.ShowChoice(null, new DialogRequest
        {
            Title = _texts["Explorer_RenameTitle"],
            Message = string.Format(_texts["Explorer_RenamePreview"], preview.Items.Count,
                ObjectLayout.ObjectUri(preview.Bucket, preview.TargetKey), preview.ConflictCount,
                preview.ListRequests, preview.ExistenceChecks),
            Content = preview.ConflictCount == 0 ? null
                : CreateReportContent(string.Join(Environment.NewLine, preview.ConflictKeys),
                    _texts["Explorer_ConflictDetails"]),
            PrimaryText = preview.ConflictCount > 0 ? _texts["Explorer_Overwrite"] : _texts["Explorer_Rename"],
            SecondaryText = preview.ConflictCount > 0 ? _texts["Explorer_Skip"] : _texts["Dialog_Cancel"],
            Tone = preview.ConflictCount > 0 ? DialogTone.Danger : DialogTone.Neutral
        });
        if (choice == DialogChoice.Dismiss || choice == DialogChoice.Secondary && preview.ConflictCount == 0)
            return;
        if (!CanWrite || _disposed || generation != _generation || !ReferenceEquals(connection, _connection))
            return;
        var policy = preview.ConflictCount == 0 ? ConflictPolicy.FailIfExists
            : choice == DialogChoice.Primary ? ConflictPolicy.Overwrite : ConflictPolicy.SkipExisting;
        await RunTrackedMutation(token => RunMutation(
            _ => _explorer.RenamePreparedAsync(connection, preview, policy, token, OnCopyProgress),
            resolveConflicts: false, previewed: true));
    }

    private async Task PasteAsync()
    {
        var snapshot = WindowsFileClipboard.Read();
        if (!CanPasteSnapshot(snapshot) || _connection is null || _bucket is null)
            return;
        if (_clipboard.Resolve(snapshot) == PasteSource.LocalFiles)
        {
            await PasteLocalFilesAsync(snapshot);
            return;
        }
        if (_clipboard.Current is not { } clip)
            return;
        var connection = _connection;
        var bucket = _bucket;
        var prefix = _prefix;
        if (clip.ConnectionId != connection.Id)
        {
            var source = await _transferConnections.FindAsync(clip.ConnectionId, CancellationToken.None);
            if (source is null)
            {
                _toasts.Show(new ToastRequest { Title = _texts["Transfer_Rejected"],
                    Message = _texts["Explorer_SourceConnectionMissing"], Tone = ToastTone.Error });
                return;
            }
            try
            {
                var items = clip.Entries.Select(entry => new DownloadItem(entry.Key,
                    entry.Kind == ObjectEntryKind.Folder, entry.SizeBytes)).ToList();
                var generation = _generation;
                var policy = await ResolveTransferConflictAsync(token =>
                    _transfers.FindCrossCopyConflictAsync(source, connection,
                        clip.SourceBucket, items, bucket, prefix, token));
                if (policy is null || _disposed || _generation != generation
                    || !ReferenceEquals(connection, _connection) || _bucket != bucket
                    || _prefix != prefix || !ReferenceEquals(clip, _clipboard.Current))
                    return;
                var jobs = await _transfers.EnqueueCrossCopiesAsync(source, connection,
                    clip.SourceBucket, items, bucket, prefix, policy.Value,
                    move: clip.Cut, batchCreated: TrackCopyBatchResult);
                ReportCrossQueued(jobs, clip.Cut);
                TrackSelectionForJobs(jobs);
                if (clip.Cut && jobs.Count > 0)
                    _clipboard.ClearIfCurrent(clip);
            }
            catch (Exception exception)
            {
                _toasts.Show(new ToastRequest { Title = _texts["Transfer_Rejected"],
                    Message = CrossCopyError(exception), Tone = ToastTone.Error });
            }
            return;
        }
        var committed = await RunTrackedMutation(token => RunMutation(policy => _explorer.CopyAsync(
                connection, clip.SourceBucket, clip.Entries, bucket, prefix,
                clip.Cut, policy, token, OnCopyProgress, clip.Attempted), resolveConflicts: true,
            selectSucceeded: true, completed: report =>
            {
                if (report.Succeeded == 0)
                    return;
                _clipboard.PublishStorageChanged(this, connection.Id, bucket);
                if (clip.Cut && clip.SourceBucket != bucket)
                    _clipboard.PublishStorageChanged(this, connection.Id, clip.SourceBucket);
            }));
        if (clip.Cut)
            clip.MarkAttempted();
        if (clip.Cut && committed)
            _clipboard.ClearIfCurrent(clip);
    }

    private async Task PasteLocalFilesAsync(FileClipboardSnapshot snapshot)
    {
        if (_connection is null || _bucket is null)
            return;
        var connection = _connection;
        var bucket = _bucket;
        var prefix = _prefix;
        var generation = _generation;
        if (snapshot.Operation == LocalClipboardOperation.Move)
        {
            var choice = _dialogs.ShowChoice(null, new DialogRequest
            {
                Title = _texts["Explorer_CutUploadTitle"],
                Message = _texts["Explorer_CutUploadMessage"],
                PrimaryText = _texts["Explorer_UploadCopies"],
                SecondaryText = _texts["Dialog_Cancel"],
                Tone = DialogTone.Neutral
            });
            if (choice != DialogChoice.Primary)
                return;
        }
        // The modal and conflict picker can reenter WPF. Do not silently retarget.
        if (!CanWrite || _generation != generation || !ReferenceEquals(_connection, connection)
            || _bucket != bucket || _prefix != prefix
            || WindowsFileClipboard.SequenceNumber() != snapshot.SequenceNumber)
            return;
        await QueueUploadsToAsync(connection, bucket, prefix, snapshot.Paths, snapshot.SequenceNumber);
    }

    private async Task CopyToAsync()
    {
        if (!CanCopyTo || _connection is null || _bucket is null)
            return;
        var source = _connection;
        var bucket = _bucket;
        var entries = SelectedEntries();
        IReadOnlyList<StorageConnection> connections;
        try
        {
            connections = await _transferConnections.ListAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Transfer_Rejected"],
                Message = CrossCopyError(exception), Tone = ToastTone.Error });
            return;
        }
        using var picker = new CopyDestinationPicker(_explorer, _texts, connections,
            entries.Count(entry => entry.Kind == ObjectEntryKind.File),
            entries.Count(entry => entry.Kind == ObjectEntryKind.Folder),
            _state.Preferences.TransferConflict);
        var choice = _dialogs.ShowChoice(null, new DialogRequest
        {
            Title = _texts["Explorer_CopyTo"],
            Content = picker,
            PrimaryText = _texts["Transfer_Start"],
            SecondaryText = _texts["Dialog_Cancel"]
        });
        if (choice != DialogChoice.Primary || picker.Destination is not { } destination
            || picker.Bucket is not { } destinationBucket)
            return;
        var currentSource = await _transferConnections.FindAsync(source.Id, CancellationToken.None);
        var currentDestination = await _transferConnections.FindAsync(destination.Id, CancellationToken.None);
        if (_disposed || currentSource is null || currentDestination is null || currentDestination.IsReadOnly
            || new Cavdeck.Application.Connections.ConnectionChange(source, currentSource).RequiresReconnect
            || new Cavdeck.Application.Connections.ConnectionChange(destination, currentDestination).RequiresReconnect)
            return;
        source = currentSource;
        destination = currentDestination;
        if (destination.Id == source.Id)
        {
            var policy = picker.Conflict switch
            {
                TransferConflictPolicy.Skip => ConflictPolicy.SkipExisting,
                TransferConflictPolicy.Overwrite => ConflictPolicy.Overwrite,
                _ => ConflictPolicy.Rename
            };
            try
            {
                await RunTrackedMutation(async token =>
                {
                    SetBusy(true, more: false);
                    MutationReport report;
                    try
                    {
                        report = await _explorer.CopyAsync(source, bucket, entries,
                            destinationBucket, picker.Prefix, false, policy, token, OnCopyProgress);
                    }
                    finally
                    {
                        SetBusy(false, more: false);
                    }
                    Present(report);
                    if (report.Succeeded > 0)
                        _clipboard.PublishStorageChanged(this, source.Id, destinationBucket);
                    await RefreshAsync();
                    return report.IsFullSuccess;
                });
            }
            catch (Exception exception)
            {
                Surface(exception, keepList: true);
            }
            return;
        }
        try
        {
            var jobs = await _transfers.EnqueueCrossCopiesAsync(source, destination,
                bucket, entries.Select(entry => new DownloadItem(entry.Key,
                    entry.Kind == ObjectEntryKind.Folder, entry.SizeBytes)).ToList(),
                destinationBucket, picker.Prefix, picker.Conflict,
                batchCreated: TrackCopyBatchResult);
            ReportCrossQueued(jobs);
            TrackSelectionForJobs(jobs);
        }
        catch (Exception exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Transfer_Rejected"],
                Message = CrossCopyError(exception), Tone = ToastTone.Error });
        }
    }

    private async Task<bool> RunTrackedMutation(Func<CancellationToken, Task<bool>> action)
    {
        if (_mutation is not null || _disposed || IsReadOnly)
            return false;
        using var mutation = new CancellationTokenSource();
        _mutation = mutation;
        _copyProgress = null;
        _lastCopyUpdate = 0;
        OnPropertyChanged(nameof(ShowMutationStatus));
        OnPropertyChanged(nameof(MutationStatusText));
        try { return await action(mutation.Token); }
        finally
        {
            _mutation = null;
            _copyProgress = null;
            OnPropertyChanged(nameof(ShowMutationStatus));
        }
    }

    private void Remember(bool cut)
    {
        if (!(cut ? CanCut : CanCopy) || _connection is null || _bucket is null)
            return;
        var clip = new ObjectClipboard(_connection.Id, _bucket, cut, SelectedEntries());
        _clipboard.Set(clip);
        _toasts.Show(new ToastRequest
        {
            Title = _texts[cut ? "Explorer_CutReady" : "Explorer_CopyReady"],
            Message = string.Format(_texts["Explorer_ClipboardCount"], clip.Entries.Count),
            Tone = ToastTone.Information
        });
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ShowPaste));
        OnPropertyChanged(nameof(PasteLabel));
        NotifyChrome();
        RaiseCommands();
    }

    private void OnCopyProgress(CopyProgress progress)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnCopyProgress(progress));
            return;
        }
        if (_mutation is null || _copyProgress is { } previous &&
            ((int)progress.Stage < (int)previous.Stage ||
             progress.Stage == previous.Stage && progress.Completed < previous.Completed))
            return;
        _copyProgress = progress;
        var now = Environment.TickCount64;
        if (now - _lastCopyUpdate < 100 && progress.Completed < progress.Total)
            return;
        _lastCopyUpdate = now;
        OnPropertyChanged(nameof(MutationStatusText));
    }

    private void CopyText(bool keys)
    {
        if (_selected.Count == 0 || _bucket is null)
            return;
        var text = string.Join(Environment.NewLine, _selected.Select(row =>
            keys ? row.Entry.Key : ObjectLayout.ObjectUri(_bucket, row.Entry.Key)));
        try
        {
            Clipboard.SetText(text);
            ShowClipboardCopiedToast();
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Explorer_CopyFailed"],
                Tone = ToastTone.Error
            });
        }
    }

    private void CopyName()
    {
        if (_selected.Count != 1)
            return;
        try
        {
            Clipboard.SetText(_selected[0].Entry.Name);
            ShowClipboardCopiedToast();
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Explorer_CopyFailed"], Tone = ToastTone.Error });
        }
    }

    // All details-pane clipboard actions use the same, localized success feedback.
    private void ShowClipboardCopiedToast() =>
        _toasts.Show(new ToastRequest { Title = _texts["Explorer_ValueCopied"], Tone = ToastTone.Success });

    private void CopyDetailValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        try
        {
            Clipboard.SetText(value);
            ShowClipboardCopiedToast();
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Explorer_CopyFailed"], Tone = ToastTone.Error });
        }
    }

    private async Task ShareLinkAsync()
    {
        if (!CanShareLink || _connection is null || _bucket is null)
            return;
        var connection = _connection;
        var bucket = _bucket;
        var key = _selected[0].Entry.Key;
        var settingKey = ShareLinks.BucketSettingKey(connection.Id, bucket);
        _state.Preferences.PublicBucketUrls.TryGetValue(settingKey, out var publicRoot);
        var choice = ShareLinkDialog.Choose(_dialogs, _texts, publicRoot);
        if (choice is null)
            return;

        try
        {
            ShareLink link;
            if (choice.IsPublic)
            {
                link = ShareLinks.Public(choice.PublicBase!, key);
                _state.Preferences.PublicBucketUrls[settingKey] = choice.PublicBase!;
                await _settings.SaveAsync(_state.Preferences);
            }
            else
            {
                link = await _linkSigner.SignDownloadAsync(connection, bucket, key,
                    choice.Lifetime, CancellationToken.None);
            }
            ShareLinkDialog.ShowResult(_dialogs, _texts, _toasts, link);
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Share_Failed"],
                Tone = ToastTone.Error
            });
        }
    }

    private async Task<bool> RunMutation(Func<ConflictPolicy, Task<MutationReport>> action,
        bool resolveConflicts, bool previewed = false, bool selectSucceeded = false,
        Action<MutationReport>? completed = null)
    {
        if (_connection is null || _disposed || IsReadOnly || _mutation?.IsCancellationRequested == true)
            return false;
        var snapshot = _connection;
        var complete = false;
        IReadOnlyList<string> succeededKeys = [];
        var selectionConnection = _connection.Id;
        var selectionBucket = _bucket;
        var selectionPrefix = _prefix;
        SetBusy(true, more: false);
        try
        {
            var report = await action(ConflictPolicy.FailIfExists);
            if (report.BlockedOnConflict)
            {
                SetBusy(false, more: false);
                if (!resolveConflicts)
                {
                    _dialogs.Show(null, new DialogRequest
                    {
                        Title = _texts[previewed ? "Explorer_ConflictTitle" : "Explorer_NameExists"],
                        Message = _texts[previewed ? "Explorer_PreviewChanged" : "Explorer_NameExistsMessage"],
                        PrimaryText = _texts["Dialog_Ok"]
                    });
                    return false;
                }

                var choice = await ChooseCopyConflictAsync(
                    string.Format(_texts["Explorer_ConflictMessage"], report.ConflictCount), summary: true);
                if (choice is null || _disposed || IsReadOnly
                    || !ReferenceEquals(snapshot, _connection) || _mutation?.IsCancellationRequested == true)
                    return false;
                _copyProgress = null;
                OnPropertyChanged(nameof(MutationStatusText));
                SetBusy(true, more: false);
                report = await action(choice.Value switch
                {
                    TransferConflictPolicy.Skip => ConflictPolicy.SkipExisting,
                    TransferConflictPolicy.Overwrite => ConflictPolicy.Overwrite,
                    _ => ConflictPolicy.Rename
                });
            }

            Present(report);
            if (report.Succeeded > 0 && !report.BlockedOnConflict)
                completed?.Invoke(report);
            complete = report.IsFullSuccess;
            if (selectSucceeded)
                succeededKeys = report.SucceededKeys;
        }
        catch (OperationCanceledException) when (_mutation?.IsCancellationRequested == true)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Explorer_CopyCanceled"],
                Tone = ToastTone.Warning
            });
        }
        catch (Exception exception)
        {
            Surface(exception, keepList: true);
        }
        finally
        {
            SetBusy(false, more: false);
        }

        if (_disposed || !ReferenceEquals(snapshot, _connection))
            return complete;
        if (_bucket is null)
            await RefreshAsync();
        else
            await GoToAsync(_bucket, _prefix, buckets: false, userMove: false);
        if (selectSucceeded && _connection?.Id == selectionConnection && _bucket == selectionBucket
            && _prefix == selectionPrefix)
            SelectVisibleKeys(succeededKeys);
        return complete;
    }

    private void Present(MutationReport report)
    {
        if (report.BlockedOnConflict)
        {
            _dialogs.Show(null, new DialogRequest
            {
                Title = _texts["Explorer_ConflictTitle"],
                Message = _texts["Explorer_PreviewChanged"],
                PrimaryText = _texts["Dialog_Ok"]
            });
            return;
        }
        if (report.Failed > 0)
        {
            var lines = report.Issues.Select(issue =>
                (issue.Key == "*" ? "" : issue.Key + " — ") + ExplorerText.FailureTitle(_texts, issue.Failure));
            var detail = report.Issues.Count == 1
                ? ExplorerText.FailureDetail(_texts, report.Issues[0].Failure)
                : "";
            var summary = string.Format(_texts["Explorer_ResultPartial"], report.Succeeded, report.Skipped, report.Failed);
            var body = string.Join(Environment.NewLine, lines.Concat(detail.Length == 0 ? [] : [detail]));
            _dialogs.Show(null, new DialogRequest
            {
                Title = _texts["Explorer_PartialTitle"],
                Message = summary,
                Content = CreateReportContent(body, _texts["Explorer_FailureDetails"]),
                PrimaryText = _texts["Dialog_Ok"],
                Tone = DialogTone.Danger
            });
            return;
        }

        if (report.Succeeded == 0 && report.Skipped == 0)
            return;

        _toasts.Show(new ToastRequest
        {
            Title = string.Format(_texts["Explorer_ResultDone"], report.Succeeded, report.Skipped),
            Tone = ToastTone.Success
        });
    }

    private System.Windows.Controls.StackPanel CreateReportContent(string body, string automationName)
    {
        var details = new System.Windows.Controls.TextBox
        {
            Text = body,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            MaxHeight = 280,
            MinHeight = 80
        };
        System.Windows.Automation.AutomationProperties.SetName(details, automationName);
        var copy = new System.Windows.Controls.Button
        {
            Content = _texts["Explorer_CopyReport"],
            Style = (Style)System.Windows.Application.Current.FindResource("SecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(body); }
            catch (Exception) { _toasts.Show(new ToastRequest { Title = _texts["Explorer_CopyFailed"], Tone = ToastTone.Error }); }
        };
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(details);
        content.Children.Add(copy);
        return content;
    }

}
