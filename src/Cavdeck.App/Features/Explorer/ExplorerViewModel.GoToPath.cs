using Cavdeck.App.Features.Diagnostics;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Places;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

internal sealed record GoToPathSuggestion(string Label, string Target, ExplorerPathInput Input);

internal sealed record GoToPathPromptData(string Initial, IReadOnlyList<GoToPathSuggestion> Recent,
    IReadOnlyList<GoToPathSuggestion> History, Func<Task<bool>>? ClearHistory);

public sealed partial class ExplorerViewModel
{
    private CancellationTokenSource? _pathPromptStop;

    public bool CanGoToPath => !_disposed && ShowObjects && !_busy && _pathPromptStop is null;

    internal void CancelGoToPath() => _pathPromptStop?.Cancel();

    private Task<bool> ShowGoToPathPrompt(GoToPathPromptData data,
        Func<ExplorerPathInput, CancellationToken, Task<string?>> validate, CancellationToken cancellationToken) =>
        Task.FromResult(ExplorerPrompt.AskForPath(_dialogs, _texts, data, validate, cancellationToken));

    internal async Task GoToPathAsync(
        Func<GoToPathPromptData, Func<ExplorerPathInput, CancellationToken, Task<string?>>, CancellationToken, Task<bool>> showPrompt)
    {
        if (!CanGoToPath || _connection is not { } connection || _bucket is not { } bucket)
            return;

        var prefix = _prefix;
        var generation = _generation;
        var root = Root;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_browse.Token);
        _pathPromptStop = stop;
        ResolvedExplorerPath? destination = null;
        ExplorerPathInput acceptedInput = default;

        bool IsCurrent() => !stop.IsCancellationRequested && !_disposed && !_reconnectPending
            && generation == _generation && ReferenceEquals(connection, _connection)
            && bucket == _bucket && prefix == _prefix;

        async Task<string?> ValidateAsync(ExplorerPathInput input, CancellationToken dialogToken)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, dialogToken);
            destination = null;
            try
            {
                var resolved = await _explorer.ResolvePathAsync(connection, bucket, prefix, input, request.Token);
                request.Token.ThrowIfCancellationRequested();
                destination = resolved;
                acceptedInput = input.Text.TrimStart().StartsWith("s3://", StringComparison.OrdinalIgnoreCase)
                    ? new ExplorerPathInput(input.Text.TrimStart())
                    : new ExplorerPathInput(input.Text.Trim(), input.BucketPath);
                return destination is null ? _texts["ExplorerPath_NotFound"] : null;
            }
            catch (StorageException exception) when (!request.IsCancellationRequested)
            {
                var failure = exception.Failure;
                if (failure.Kind == StorageFailureKind.InvalidConfiguration
                    && failure.ErrorCode is "ExplorerPath_Required" or "ExplorerPath_Unsupported"
                        or "ExplorerPath_Invalid" or "ExplorerPath_TooLong" or "ExplorerPath_Unbrowsable")
                    return _texts[failure.ErrorCode];

                var title = ExplorerText.FailureTitle(_texts, failure);
                var hint = _texts["Connection_Failure_" + failure.Kind + "Hint"];
                return string.IsNullOrEmpty(hint) ? title : title + "\n" + hint;
            }
            catch (Exception exception) when (!request.IsCancellationRequested)
            {
                DiagnosticLog.Current.Queue("ui", exception);
                return _texts["ExplorerPath_Failed"];
            }
        }

        try
        {
            GoToPathCommand.RaiseCanExecuteChanged();
            var data = BuildPathPrompt(connection, bucket, prefix, root);
            if (!await showPrompt(data, ValidateAsync, stop.Token) || !IsCurrent() || destination is null)
                return;

            var confirmed = await ConfirmPathLeaveAsync(destination, stop.Token);
            if (confirmed is not null && IsCurrent() && !_preview.IsDirty)
            {
                var history = new PathHistoryEntry(connection.Id, bucket, prefix, acceptedInput, confirmed.Bucket,
                    confirmed.File?.Key ?? confirmed.Listing.Prefix, confirmed.File is not null, DateTimeOffset.UtcNow);
                await GoToAsync(confirmed.Bucket, confirmed.Listing.Prefix, buckets: false,
                    resolvedPath: confirmed, pathHistory: history);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            _pathPromptStop = null;
            GoToPathCommand.RaiseCanExecuteChanged();
        }
    }

    private GoToPathPromptData BuildPathPrompt(StorageConnection connection, string bucket, string prefix, string root)
    {
        bool InScope(string targetBucket, string key) =>
            (string.IsNullOrEmpty(connection.RestrictedBucket) || targetBucket == connection.RestrictedBucket)
            && key.StartsWith(root, StringComparison.Ordinal);

        bool Matches(ExplorerPathInput input, string targetBucket, string key, bool isFile) =>
            ExplorerPath.ParseTarget(input, bucket, root, prefix, out var parsedBucket, out var path) == ExplorerPathIssue.None
            && parsedBucket == targetBucket && path.Key == key && path.DirectoryOnly != isFile;

        GoToPathSuggestion? Suggest(string targetBucket, string key, bool isFile, PathHistoryEntry? item = null)
        {
            if (!InScope(targetBucket, key))
                return null;

            var target = ObjectLayout.ObjectUri(targetBucket, key);
            var input = new ExplorerPathInput(target);
            if (item is not null && item.SourceBucket == bucket && item.SourcePrefix == prefix
                && Matches(item.Input, targetBucket, key, isFile))
                input = item.Input;
            else if (targetBucket == bucket)
            {
                var absolute = new ExplorerPathInput("/" + key[root.Length..]);
                if (Matches(absolute, targetBucket, key, isFile))
                    input = absolute;
            }

            if (!Matches(input, targetBucket, key, isFile))
                return null;
            var label = item is not null && item.Input.Text != target
                ? item.Input.Text + "  ·  " + target : input.Text;
            return new GoToPathSuggestion(label, target, input with { FileOnly = isFile });
        }

        var recent = _places.Recent
            .Where(item => item.ConnectionId == connection.Id && item.Bucket is not null
                && (item.Bucket != bucket || item.Prefix != prefix))
            .Select(item => Suggest(item.Bucket!, item.Prefix, isFile: false))
            .OfType<GoToPathSuggestion>().ToList();
        var history = _places.PathHistory.Where(item => item.ConnectionId == connection.Id).ToList();
        var suggestions = history.Select(item => Suggest(item.Bucket, item.Key, item.IsFile, item))
            .OfType<GoToPathSuggestion>().ToList();

        async Task<bool> ClearHistoryAsync()
        {
            try
            {
                await _places.ClearPathHistoryAsync(connection.Id);
                return true;
            }
            catch (Exception exception)
            {
                DiagnosticLog.Current.Queue("ui", exception);
                return false;
            }
        }

        return new GoToPathPromptData("/" + prefix[root.Length..], recent, suggestions,
            history.Count == 0 ? null : ClearHistoryAsync);
    }

    private async Task<ResolvedExplorerPath?> ConfirmPathLeaveAsync(
        ResolvedExplorerPath destination, CancellationToken token)
    {
        if (!_preview.IsDirty)
            return destination;

        // The existing save/discard/stay UI lives in the preview pane of the owner window.
        ObjectHead? saved = null;
        void CaptureSave(object? sender, ObjectHead head) => saved = head;
        _preview.Saved += CaptureSave;
        try
        {
            if (!await ConfirmLeaveAsync(token))
                return null;
        }
        finally
        {
            _preview.Saved -= CaptureSave;
        }

        if (saved is null || saved.Bucket != destination.Bucket)
            return destination;

        var listing = destination.Listing;
        var entries = listing.Entries.Select(entry => entry.Key == saved.Key
            ? entry with { SizeBytes = saved.SizeBytes, ETag = saved.ETag, LastModified = null }
            : entry).ToList();
        return destination with
        {
            Listing = listing with { Entries = entries },
            File = destination.File?.Key == saved.Key ? saved : destination.File
        };
    }
}
