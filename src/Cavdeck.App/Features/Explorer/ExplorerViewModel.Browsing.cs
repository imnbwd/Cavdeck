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
    private async Task RefreshAsync()
    {
        if (_reconnectPending)
        {
            await RetryReconnectAsync();
            return;
        }
        if (_connection is null || _busy)
            return;
        if (_searchMode && _searchScopeIndex != 0 && HasFilter)
        {
            CancelSearch();
            await RunSearchAsync();
        }
        else if (_bucket is null)
            await GoToAsync(null, Root, buckets: true, userMove: false);
        else
            await GoToAsync(_bucket, _prefix, buckets: false, userMove: false);
    }

    private async Task LoadMoreAsync()
    {
        if (!CanLoadMore || _connection is null || _bucket is null)
            return;
        var generation = _generation;
        SetBusy(true, more: true);
        try
        {
            await LoadObjectsCoreAsync(generation, _browse.Token, append: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation == _generation)
                Surface(exception, keepList: true);
        }
        finally
        {
            if (generation == _generation)
                SetBusy(false, more: false);
        }
    }

    private Task UpAsync()
    {
        if (!CanGoUp || _bucket is null)
            return Task.CompletedTask;
        ClearFilter();
        if (_prefix != Root)
        {
            var parent = ObjectLayout.ParentPrefix(_prefix) ?? Root;
            if (Root.Length > 0 && !parent.StartsWith(Root, StringComparison.Ordinal))
                parent = Root;
            return GoToAsync(_bucket, parent, buckets: false);
        }

        return GoToAsync(null, Root, buckets: true);
    }

    private void OpenRowInNewTab(object? parameter)
    {
        if (_connection is null || _busy)
            return;
        if (parameter is BucketRow bucket && ShowBuckets)
        {
            OpenNewTabRequested?.Invoke(this, new ExplorerPlace(_connection, bucket.Name, Root, false));
            return;
        }
        var row = parameter as ExplorerRow ?? (_selected.Count == 1 ? _selected[0] : null);
        if (row is not { IsFolder: true } || _bucket is null || !ShowObjects)
            return;
        OpenNewTabRequested?.Invoke(this, new ExplorerPlace(_connection, _bucket, row.Entry.Key, false));
    }

    private Task OpenRowAsync(object? parameter)
    {
        if (parameter is BucketRow bucket)
        {
            ClearFilter();
            return GoToAsync(bucket.Name, Root, buckets: false);
        }

        if (parameter is ExplorerRow row)
            return OpenEntryAsync(row);
        if (_selected.Count == 1)
            return OpenEntryAsync(_selected[0]);
        return Task.CompletedTask;
    }

    private Task OpenCrumbAsync(object? parameter)
    {
        if (parameter is not CrumbViewModel crumb || crumb.IsCurrent || _busy)
            return Task.CompletedTask;
        ClearFilter();
        return GoToAsync(crumb.Location.Bucket, crumb.Location.Prefix, crumb.Location.Buckets);
    }

    private async Task GoToAsync(string? bucket, string prefix, bool buckets, bool userMove = true)
    {
        if (_connection is null || _disposed || _reconnectPending)
            return;
        if (userMove && _preview.IsDirty)
        {
            _afterLeave = () => GoToAsync(bucket, prefix, buckets, userMove: true);
            _preview.MarkLeavePending();
            return;
        }

        if (userMove)
            _preview.Close();
        // Navigation invalidates any highlighted upload destination, even if still writable.
        SetDropTarget(false);
        _operation?.Cancel();
        CancelSearch();
        var generation = ++_generation;
        var token = ResetBrowse();
        _bucket = buckets ? null : bucket;
        _prefix = buckets ? Root : prefix;
        _continuation = null;
        _selected.Clear();
        CancelHeaderLoad();
        RefreshContextDetail();
        SetError("");
        // Rows from the previous view must not mask the current view's empty/no-match
        // state when switching between a bucket list and an object list.
        _loaded.Clear();
        Rows.Clear();
        _buckets.Clear();
        BucketRows.Clear();

        RebuildCrumbs();
        NotifyChrome();
        SetBusy(true, more: false);
        try
        {
            if (buckets)
                await LoadBucketsCoreAsync(generation, token);
            else
                await LoadObjectsCoreAsync(generation, token, append: false);
            if (generation == _generation && userMove && _error.Length == 0)
            {
                try
                {
                    await _places.RecordVisitAsync(_connection.Id, _bucket, _prefix, token);
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    _toasts.Show(new ToastRequest { Title = _texts["Places_SaveFailed"], Tone = ToastTone.Error });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation == _generation)
                Surface(exception, keepList: false);
        }
        finally
        {
            if (generation == _generation)
                SetBusy(false, more: false);
        }
    }

    private async Task LoadBucketsCoreAsync(int generation, CancellationToken token)
    {
        var buckets = await _explorer.ListBucketsAsync(_connection!, token);
        if (generation != _generation)
            return;
        _buckets.Clear();
        _buckets.AddRange(buckets.OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase));
        PublishBuckets();
    }

    private async Task LoadObjectsCoreAsync(int generation, CancellationToken token, bool append)
    {
        var result = await _explorer.BrowseAsync(_connection!, _bucket!, _prefix, append ? _continuation : null, token);
        if (generation != _generation)
            return;
        _prefix = result.Prefix;
        _continuation = result.ContinuationToken;
        if (!append)
            _loaded.Clear();
        _loaded.AddRange(result.Entries);
        // Keep the default Name-ascending view incremental: do not reset existing
        // row instances (and therefore selection) every time another page loads.
        if (append && _sorted && _sort == ListingSort.Name && !_descending && !HasFilter)
            ExplorerNameSortedPage.Append(Rows, result.Entries, _texts);
        else
            PublishRows(append && !_sorted && !HasFilter);
        RebuildCrumbs();
        if (_selected.Count == 0)
            RefreshContextDetail();
    }

    private void PublishBuckets()
    {
        var needle = _filter.Trim();
        BucketRows.ReplaceWith(_buckets
            .Where(bucket => needle.Length == 0 || bucket.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(bucket => new BucketRow(bucket, _texts,
                _connection is not null && _places.IsFavorite(_connection.Id, bucket.Name, Root))));
        OnPropertyChanged(nameof(ShowBucketRegion));
    }

    private void PublishRows(bool appendOnlyNewPage)
    {
        if (!appendOnlyNewPage)
        {
            IReadOnlyList<ObjectEntry> source = _searchMode
                ? _searchResults : _searchScopeIndex == 0
                    ? ObjectLayout.Filter(_loaded, _filter)
                    : HasFilter ? [] : _loaded;
            if (_sorted)
                source = ObjectLayout.Sort(source, _sort, _descending);
            Rows.ReplaceWith(source.Select(entry => new ExplorerRow(entry, _texts,
                _searchMode ? entry.Key[_searchDisplayPrefix.Length..] : null)));
            return;
        }

        var seen = Rows.Select(row => row.Entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in _loaded)
        {
            if (seen.Add(entry.Key))
                Rows.Add(new ExplorerRow(entry, _texts));
        }
    }

    private void RebuildCrumbs()
    {
        Crumbs.Clear();
        if (_connection is not null)
        {
            var root = Root;
            var prefixCrumbs = ObjectLayout.Crumbs(_prefix);
            AddCrumb(_connection.Name, _bucket is null,
                new ExplorerLocation(CanListBuckets, CanListBuckets ? null : _connection.RestrictedBucket, root));

            if (_bucket is not null)
            {
                AddCrumb(_bucket, _prefix == root && prefixCrumbs.Count == 0, new ExplorerLocation(false, _bucket, root));
                foreach (var crumb in prefixCrumbs)
                {
                    if (root.Length > 0 && crumb.Prefix.Length < root.Length)
                        continue;
                    AddCrumb(crumb.Name, crumb.Prefix == _prefix, new ExplorerLocation(false, _bucket, crumb.Prefix));
                }
            }
        }

        var path = string.Join(" / ", Crumbs.Select(crumb => crumb.Label));
        if (PathText == path)
            return;
        PathText = path;
        OnPropertyChanged(nameof(PathText));
    }

    private void AddCrumb(string label, bool isCurrent, ExplorerLocation location) =>
        Crumbs.Add(new CrumbViewModel(label, isCurrent, Crumbs.Count > 0, location));

    private void RefreshContextDetail()
    {
        _detail = _selected.Count switch
        {
            1 => BuildDetail(_selected[0]),
            > 1 => ExplorerContextDetailFactory.ForSelection(_selected, _texts),
            _ when _connection is not null => ExplorerContextDetailFactory.ForCurrentLocation(
                _connection, _bucket, _prefix, _texts),
            _ => null
        };
        OnPropertyChanged(nameof(Detail));
    }

    private ExplorerDetail BuildDetail(ExplorerRow row)
    {
        var entry = row.Entry;
        var contentType = HeaderText(entry);
        return new ExplorerDetail
        {
            Name = entry.Name,
            Kind = row.Kind,
            Size = row.Size,
            Modified = row.ModifiedText,
            Key = entry.Key,
            ETag = ExplorerText.ETag(entry.ETag),
            ETagRaw = entry.ETag ?? "",
            Uri = _bucket is null ? "" : ObjectLayout.ObjectUri(_bucket, entry.Key),
            ContentType = contentType,
            ContentTypeDisplay = contentType.Replace("/", "/\u200b").Replace(";", ";\u200b"),
            SizeTooltip = row.IsFolder ? "" : entry.SizeBytes.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " " + _texts["Explorer_Bytes"],
            ModifiedTooltip = entry.LastModified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.CurrentCulture) ?? "",
            ModifiedSummary = _texts["Explorer_Modified"] + " " + row.ModifiedText,
            KeyLabel = _texts[row.IsFolder ? "Explorer_Prefix" : "Explorer_ObjectKey"],
            IsFolder = row.IsFolder,
            ShowHeaders = ObjectHeaderDisplay.AppliesTo(entry.Kind)
        };
    }

    private string HeaderText(ObjectEntry entry)
    {
        if (!ObjectHeaderDisplay.AppliesTo(entry.Kind))
            return "";
        if (_headerPending || _headerKey != entry.Key)
            return _texts["Explorer_Loading"];
        if (_headerFailed || _headerHead is null)
            return _texts["Explorer_HeaderUnavailable"];
        return ObjectHeaderDisplay.Value(_headerHead.ContentType);
    }

    private void BeginHeaderLoad(ExplorerRow row)
    {
        CancelHeaderLoad();
        if (!ObjectHeaderDisplay.AppliesTo(row.Entry.Kind) || _connection is null || _bucket is null)
            return;

        var connection = _connection;
        var bucket = _bucket;
        var key = row.Entry.Key;
        var generation = _headerGeneration;
        var load = new CancellationTokenSource();
        _headerLoad = load;
        _headerKey = key;
        _headerPending = true;
        _ = LoadHeadersAsync(generation, connection, bucket, key, load.Token);
    }

    private void CancelHeaderLoad()
    {
        _headerGeneration++;
        _headerLoad?.Cancel();
        _headerLoad?.Dispose();
        _headerLoad = null;
        _headerKey = null;
        _headerHead = null;
        _headerPending = false;
        _headerFailed = false;
    }

    private async Task LoadHeadersAsync(
        int generation, StorageConnection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            var head = await _storage.Create(connection.Provider).HeadObjectAsync(
                connection, bucket, key, cancellationToken);
            PublishHeaders(generation, key, head, failed: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            PublishHeaders(generation, key, null, failed: true);
        }
    }

    private void PublishHeaders(int generation, string key, ObjectHead? head, bool failed)
    {
        if (_dispatcher.CheckAccess())
            ApplyHeaders(generation, key, head, failed);
        else
            _dispatcher.Invoke(() => ApplyHeaders(generation, key, head, failed));
    }

    private void ApplyHeaders(int generation, string key, ObjectHead? head, bool failed)
    {
        if (generation != _headerGeneration || _headerKey != key)
            return;
        _headerPending = false;
        _headerFailed = failed;
        _headerHead = head;
        if (_selected.Count == 1 && _selected[0].Entry.Key == key)
        {
            _detail = BuildDetail(_selected[0]);
            OnPropertyChanged(nameof(Detail));
        }
    }

    private string? ValidateName(string raw, bool folder, ObjectEntry? ignore, string? parentPrefix = null)
    {
        if (!ObjectLayout.TryJoin(parentPrefix ?? _prefix, raw, folder, out var key, out var issue))
            return ExplorerText.NameMessage(_texts, issue);
        if (_loaded.Concat(_searchResults).Any(entry => entry.Key == key && entry.Key != ignore?.Key))
            return _texts["Explorer_NameExists"];
        // Final conflict detection remains server-side, including items not loaded in the UI.
        return null;
    }

    private void Surface(Exception exception, bool keepList)
    {
        var failure = exception is StorageException storage
            ? storage.Failure
            : new StorageFailure(StorageFailureKind.Unknown, null, null, null, SecretRedactor.Redact(exception.Message));
        var title = ExplorerText.FailureTitle(_texts, failure);
        var detail = ExplorerText.FailureDetail(_texts, failure);
        if (keepList && (Rows.Count > 0 || BucketRows.Count > 0))
        {
            _toasts.Show(new ToastRequest
            {
                Title = title,
                Message = detail,
                Tone = ToastTone.Error
            });
            return;
        }

        SetError(detail.Length == 0 ? title : title + "\n" + detail);
    }

    private IReadOnlyList<ObjectEntry> SelectedEntries() => _selected.Select(row => row.Entry).ToList();

    private string Root => _connection is null ? "" : ObjectLayout.RootPrefix(_connection);

    private bool CanListBuckets => _connection is not null && string.IsNullOrEmpty(_connection.RestrictedBucket);

    private bool ServerCopy => _connection is not null
        && ConnectionPlanner.Capabilities(_connection.Provider).CanUseServerSideCopy;

    private CancellationToken ResetBrowse()
    {
        _browse.Cancel();
        _browse.Dispose();
        _browse = new CancellationTokenSource();
        return _browse.Token;
    }

    private void SetBusy(bool busy, bool more)
    {
        _busy = busy;
        _loadingMore = busy && more;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsLoadingMore));
        OnPropertyChanged(nameof(LoadMoreText));
        NotifyChrome();
        RaiseCommands();
    }

    private void SetError(string message)
    {
        if (SetProperty(ref _error, message, nameof(Error)))
            NotifyChrome();
    }

    private void ClearFilter()
    {
        if (_filter.Length == 0)
            return;
        _filter = "";
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(HasFilter));
    }

}
