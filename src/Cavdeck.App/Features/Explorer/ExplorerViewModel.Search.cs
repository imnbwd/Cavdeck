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
    private async Task RunSearchAsync(bool continueExisting = false)
    {
        if ((continueExisting ? !CanContinueSearch : !CanRunSearch)
            || _connection is null || _bucket is null)
            return;
        if (_preview.IsDirty)
        {
            _afterLeave = () => RunSearchAsync(continueExisting);
            _preview.MarkLeavePending();
            return;
        }
        _preview.Close();
        if (!continueExisting)
            CancelSearch();
        SetError("");
        var connection = _connection;
        var bucket = _bucket;
        var prefix = _searchScopeIndex == 2 ? Root : _prefix;
        var scopeIndex = _searchScopeIndex;
        var query = _filter.Trim();
        var generation = _generation;
        var baseScanned = continueExisting ? _searchScanned : 0;
        var baseRequests = continueExisting ? _searchRequests : 0;
        var baseMatches = continueExisting ? _searchResults.Count : 0;
        var cursor = continueExisting ? _searchCursor : null;
        var previousKeys = continueExisting ? _searchResults.Select(entry => entry.Key).ToArray() : null;
        var search = new CancellationTokenSource();
        _search = search;
        _searchMode = true;
        _searchRunning = true;
        _searchComplete = false;
        _searchStopped = false;
        _searchFailed = false;
        _searchCursor = null;
        if (!continueExisting)
        {
            _searchScanned = 0;
            _searchRequests = 0;
            _searchMatches = 0;
            _searchDisplayPrefix = prefix;
            _searchScope = ObjectLayout.ObjectUri(bucket,
                scopeIndex == 1 ? prefix + query : prefix);
            _searchResults.Clear();
            ApplySelection([]);
            PublishRows(false);
        }
        NotifySearch();
        try
        {
            bool IsCurrent() => _search == search && !search.IsCancellationRequested
                && generation == _generation;

            void OnProgress(PrefixSearchProgress progress)
            {
                void Apply()
                {
                    if (!IsCurrent())
                        return;
                    _searchScanned = baseScanned + progress.ScannedObjects;
                    _searchRequests = baseRequests + progress.Requests;
                    _searchMatches = baseMatches + progress.Matches;
                    OnPropertyChanged(nameof(SearchStatusText));
                }
                if (_dispatcher.CheckAccess()) Apply(); else _dispatcher.Invoke(Apply);
            }

            void OnMatches(IReadOnlyList<ObjectEntry> matches)
            {
                void Apply()
                {
                    if (!IsCurrent())
                        return;
                    _searchResults.AddRange(matches);
                    // Insert rather than replace the collection: selection, focus and scrolling
                    // remain stable while another S3 listing page is in flight.
                    foreach (var entry in matches)
                    {
                        var row = new ExplorerRow(entry, _texts, entry.Key[_searchDisplayPrefix.Length..]);
                        if (!_sorted)
                        {
                            Rows.Add(row);
                            continue;
                        }
                        var low = 0;
                        var high = Rows.Count;
                        while (low < high)
                        {
                            var middle = (low + high) / 2;
                            var first = ObjectLayout.Sort([entry, Rows[middle].Entry], _sort, _descending)[0];
                            if (first.Key == entry.Key)
                                high = middle;
                            else
                                low = middle + 1;
                        }
                        Rows.Insert(low, row);
                    }
                    NotifySearch();
                }
                if (_dispatcher.CheckAccess()) Apply(); else _dispatcher.Invoke(Apply);
            }

            var result = await (scopeIndex == 1
                ? _explorer.SearchKeyPrefixAsync(connection, bucket, prefix, query, OnProgress,
                    search.Token, OnMatches, cursor, previousKeys)
                : _explorer.SearchPrefixAsync(connection, bucket, prefix, query, OnProgress,
                    search.Token, OnMatches, cursor, previousKeys));
            if (!IsCurrent())
                return;
            // The progress callback has already appended the same objects. Rebuild only if
            // a provider failed to deliver a batch so existing row selection survives.
            if (!_searchResults.Skip(baseMatches).Select(entry => entry.Key)
                    .SequenceEqual(result.Matches.Select(entry => entry.Key)))
            {
                if (continueExisting)
                    _searchResults.RemoveRange(baseMatches, _searchResults.Count - baseMatches);
                else
                    _searchResults.Clear();
                _searchResults.AddRange(result.Matches);
                PublishRows(false);
            }
            _searchScanned = baseScanned + result.ScannedObjects;
            _searchRequests = baseRequests + result.Requests;
            _searchMatches = _searchResults.Count;
            _searchComplete = result.Complete;
            _searchCursor = result.Cursor;
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
            // A user stop keeps the batches already received; navigation discards them.
        }
        catch (Exception exception)
        {
            if (_search == search)
            {
                _searchFailed = true;
                Surface(exception, keepList: true);
            }
        }
        finally
        {
            if (_search == search)
            {
                _searchRunning = false;
                _search = null;
                NotifySearch();
            }
            search.Dispose();
        }
    }

    private void StopSearch()
    {
        if (_search is null)
            return;
        _search.Cancel();
        _search = null;
        _searchRunning = false;
        _searchStopped = true;
        _searchComplete = false;
        _searchCursor = null; // A cancelled in-flight page cannot be resumed safely.
        NotifySearch();
    }

    private void CancelSearch()
    {
        _search?.Cancel();
        _search = null;
        _searchRunning = false;
        _searchMode = false;
        _searchComplete = false;
        _searchStopped = false;
        _searchFailed = false;
        _searchCursor = null;
        _searchResults.Clear();
        NotifySearch();
    }

    private void SetPreparing(bool value)
    {
        _preparing = value;
        if (value)
        {
            _operationScanned = _operationRequests = _operationChecks = 0;
            _lastOperationUpdate = 0;
        }
        OnPropertyChanged(nameof(ShowOperationStatus));
        OnPropertyChanged(nameof(OperationStatusText));
    }

    private void OnOperationProgress(OperationScanProgress progress)
    {
        _operationScanned = progress.ScannedObjects;
        _operationRequests = progress.ListRequests;
        _operationChecks = progress.ExistenceChecks;
        var now = Environment.TickCount64;
        if (now - _lastOperationUpdate >= 100)
        {
            _lastOperationUpdate = now;
            OnPropertyChanged(nameof(OperationStatusText));
        }
    }

    private void NotifySearch()
    {
        OnPropertyChanged(nameof(CanWriteToCurrentDirectory));
        OnPropertyChanged(nameof(CanPasteToCurrentDirectory));
        OnPropertyChanged(nameof(CanRunSearch));
        OnPropertyChanged(nameof(CanCancelSearch));
        OnPropertyChanged(nameof(CanContinueSearch));
        OnPropertyChanged(nameof(ShowSearchPrompt));
        OnPropertyChanged(nameof(ShowSearchPartialEmpty));
        OnPropertyChanged(nameof(ShowEmptyObjects));
        OnPropertyChanged(nameof(ShowSearchStatus));
        OnPropertyChanged(nameof(ShowSearchLoading));
        OnPropertyChanged(nameof(SearchLoadingText));
        OnPropertyChanged(nameof(SearchStatusText));
        OnPropertyChanged(nameof(NoMatchesTitle));
        OnPropertyChanged(nameof(NoMatchesHint));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowLoadMore));
        OnPropertyChanged(nameof(ShowObjectList));
        OnPropertyChanged(nameof(ShowObjectTable));
        OnPropertyChanged(nameof(ShowObjectIcons));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(ToolbarShowDefaultCommands));
    }

}
