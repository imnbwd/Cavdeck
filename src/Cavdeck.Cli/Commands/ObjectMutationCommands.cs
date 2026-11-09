using System.CommandLine;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static Command ObjectCopy(string name, bool move, Option<bool> quiet, CancellationToken token)
    {
        var command = new Command(name, CliText.Get(move ? "ObjectMove" : "ObjectCopy"));
        var source = new Argument<string>("source") { Description = CliText.Get("UriArgument") };
        var destination = new Argument<string>("destination") { Description = CliText.Get("UriArgument") };
        var connection = new Option<string?>("--connection", "-c") { Description = CliText.Get("ConnectionSelector") };
        var sourceConnection = new Option<string?>("--source-connection") { Description = CliText.Get("ConnectionSelector") };
        var destinationConnection = new Option<string?>("--destination-connection") { Description = CliText.Get("ConnectionSelector") };
        var recursive = new Option<bool>("--recursive") { Description = CliText.Get("RecursiveTransfer") };
        var yes = new Option<bool>("--yes") { Description = CliText.Get("ConfirmOperation") };
        var conflict = new Option<string>("--on-conflict")
        {
            DefaultValueFactory = _ => "fail", Description = CliText.Get("ConflictPolicy")
        };
        command.Arguments.Add(source);
        command.Arguments.Add(destination);
        foreach (var option in new Option[] { connection, sourceConnection, destinationConnection, recursive, yes, conflict })
            command.Options.Add(option);
        command.SetAction(async (p, _) =>
        {
            var from = StorageUri.Parse(p.GetValue(source)!);
            var to = StorageUri.Parse(p.GetValue(destination)!);
            var policy = ParseConflict(p.GetValue(conflict));
            var fromPrefix = from.HasTrailingSlash || from.Key.Length == 0;
            if ((move || policy == TransferConflictPolicy.Overwrite) && !p.GetValue(yes))
                throw new CliException(CliError.Arguments with { Message = CliText.Get("ConfirmationRequired") });
            if (fromPrefix && (!p.GetValue(recursive) || !to.HasTrailingSlash) ||
                to.Key.Length == 0 && !to.HasTrailingSlash)
                throw new CliException(CliError.Arguments);
            var common = p.GetValue(connection);
            var srcSelector = p.GetValue(sourceConnection);
            var dstSelector = p.GetValue(destinationConnection);
            if (common is null ? srcSelector is null || dstSelector is null : srcSelector is not null || dstSelector is not null)
                throw new CliException(CliError.Arguments);

            var kind = move ? "move" : "copy";
            var runId = Guid.NewGuid();
            await using var services = CreateRunServices(RunFiles.JournalPath(runId));
            var connections = services.GetRequiredService<ConnectionService>();
            var src = await ResolveAsync(connections, common ?? srcSelector!, token);
            var dst = common is not null ? src : await ResolveAsync(connections, dstSelector!, token);
            var fromKey = EffectivePrefix(src, from);
            var toKey = EffectivePrefix(dst, to);
            if (to.HasTrailingSlash && toKey.Length != 0 && !toKey.EndsWith('/'))
                throw new CliException(CliError.Arguments);
            if (!fromPrefix)
            {
                from.RequireObject();
                if (to.HasTrailingSlash)
                    toKey += from.Key[(from.Key.LastIndexOf('/') + 1)..];
            }
            StorageUri.ValidateKey(toKey);
            ObjectAccess.Ensure(src, from.Bucket, fromKey, write: move);
            ObjectAccess.Ensure(dst, to.Bucket, toKey, write: true);
            var sameBucket = CrossTransferPlanner.SameBackend(src, dst) && from.Bucket == to.Bucket;
            if (sameBucket && (fromPrefix ? toKey.StartsWith(fromKey, StringComparison.Ordinal) : fromKey == toKey))
                throw new CliException(new(5, "SOURCE_DESTINATION_OVERLAP", CliText.Get("CopyOverlap")));

            var explorer = services.GetRequiredService<ExplorerService>();
            var options = services.GetRequiredService<TransferEngineOptions>();
            Guid? batchId = null;
            using var run = RunFiles.Create(runId, kind);
            var result = await TransferRunCoordinator.RunAsync(services.GetRequiredService<TransferEngine>(), async (engine, ct) =>
            {
                var items = await BuildCopyManifestAsync(explorer, src, from, fromKey, fromPrefix, toKey,
                    options.MaxCrossMoveManifestObjects, sameBucket, ct);
                await engine.EnqueueCrossCopiesAsync(src, dst, from.Bucket, items, to.Bucket, "",
                    policy, ct, move, id => batchId = id, exactTargets: true);
            }, ShowProgress(p, quiet), token);
            return WriteTransferResult(p, "object." + kind, runId, result, token,
                new { sourceConnectionId = src.Id, destinationConnectionId = dst.Id, batchId }, run);
        });
        return command;
    }

    private static async Task<IReadOnlyList<DownloadItem>> BuildCopyManifestAsync(ExplorerService explorer,
        StorageConnection source, StorageUri from, string prefix, bool recursive, string target, int limit,
        bool sameBucket, CancellationToken token)
    {
        var items = new List<DownloadItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string key, long size, string? etag)
        {
            if (items.Count >= limit)
                throw new ObjectManifestLimitException();
            StorageUri.ValidateKey(key);
            var mapped = recursive ? CrossTransferPlanner.MapKey(key, prefix, target) : target;
            StorageUri.ValidateKey(mapped);
            if (sameBucket && recursive && mapped.StartsWith(prefix, StringComparison.Ordinal))
                throw new CliException(new(5, "SOURCE_DESTINATION_OVERLAP", CliText.Get("CopyOverlap")));
            if (string.IsNullOrWhiteSpace(etag))
                throw new CliException(new(9, "SOURCE_ETAG_REQUIRED", CliText.Get("SourceETagRequired")));
            items.Add(new DownloadItem(key, false, size, mapped, etag));
        }

        if (!recursive)
        {
            var head = await explorer.HeadAsync(source, from.Bucket, from.Key, token);
            Add(from.Key, head.SizeBytes, head.ETag);
            return items;
        }

        string? cursor = null;
        do
        {
            var page = await explorer.ListExactPageAsync(source, from.Bucket, prefix, true, cursor, 1000, token);
            if (page.Objects.Count > 1000 || page.Objects.Any(item => !item.Key.StartsWith(prefix, StringComparison.Ordinal)))
                throw new CliException(new(1, "INVALID_STORAGE_PAGE", CliText.Get("PageOutsidePrefix")));
            foreach (var item in page.Objects)
            {
                token.ThrowIfCancellationRequested();
                var etag = item.ETag;
                if (string.IsNullOrWhiteSpace(etag))
                    etag = (await explorer.HeadAsync(source, from.Bucket, item.Key, token)).ETag;
                Add(item.Key, item.SizeBytes, etag);
            }
            cursor = page.ContinuationToken;
            if (cursor is not null && !seen.Add(cursor))
                throw new CliException(new(1, "INVALID_STORAGE_PAGE", CliText.Get("PageTokenRepeated")));
        } while (cursor is not null);
        if (items.Count == 0)
            throw new CliException(new(3, "OBJECT_NOT_FOUND", CliText.Get("ObjectMissing")));
        return items;
    }

    private static Command ObjectRemove(string name, CancellationToken token)
    {
        var command = new Command(name, CliText.Get("ObjectRemove"));
        var path = AddObjectUri(command);
        var recursive = new Option<bool>("--recursive") { Description = CliText.Get("RecursiveTransfer") };
        var dryRun = new Option<bool>("--dry-run") { Description = CliText.Get("PreviewOperation") };
        var yes = new Option<bool>("--yes") { Description = CliText.Get("ConfirmOperation") };
        var max = new Option<int>("--max-objects") { DefaultValueFactory = _ => 10_000, Description = CliText.Get("MaxDeleteObjects") };
        foreach (var option in new Option[] { recursive, dryRun, yes, max })
            command.Options.Add(option);

        Bind(command, "object.remove", async (p, services) =>
        {
            var uri = StorageUri.Parse(p.GetValue(path)!);
            uri.RequireObject();
            var previewOnly = p.GetValue(dryRun);
            var limit = p.GetValue(max);
            if (previewOnly == p.GetValue(yes))
                throw new CliException(CliError.Arguments with { Message = CliText.Get("PreviewOrConfirm") });
            if (uri.HasTrailingSlash && !p.GetValue(recursive) || limit is < 1 or > 100_000)
                throw new CliException(CliError.Arguments);
            var connection = await ResolveAsync(services, p, token);
            ObjectAccess.Ensure(connection, uri.Bucket, uri.Key, write: true);
            var explorer = services.GetRequiredService<ExplorerService>();
            var entry = new ObjectEntry(uri.HasTrailingSlash ? ObjectEntryKind.Folder : ObjectEntryKind.File,
                ObjectLayout.DisplayName(uri.Key), uri.Key, 0, null, null);
            DeletePreview preview;
            try
            {
                preview = await explorer.PreviewDeleteAsync(connection, uri.Bucket, [entry], token, maxObjects: limit);
            }
            catch (ObjectManifestLimitException)
            {
                throw new CliException(new(2, "TOO_MANY_OBJECTS", CliText.Get("DeleteLimit")));
            }
            foreach (var key in preview.Keys)
                StorageUri.ValidateKey(key);
            if (previewOnly)
                return new { dryRun = true, totalObjects = preview.Keys.Count, sampleKeys = preview.Keys.Take(20).ToArray(), executed = false };
            var report = await explorer.DeletePreparedAsync(connection, preview, token, requireVerifiedDelete: true);
            var issues = report.Issues.Take(100).Select(issue => new { issue.Key, error = DeleteError(issue.Failure).Code }).ToArray();
            var data = new { report.Succeeded, report.Failed, total = preview.Keys.Count, issues, issuesTruncated = report.Issues.Count > issues.Length };
            if (report.Failed > 0)
            {
                var error = report.Succeeded > 0
                    ? new CliError(7, "PARTIAL_FAILURE", CliText.Get("PartialFailure"))
                    : DeleteError(report.Issues[0].Failure);
                throw new CliException(error with { Details = data });
            }
            return data;
        });
        return command;
    }

    private static CliError DeleteError(StorageFailure failure) => failure.Kind == StorageFailureKind.PreconditionFailed
        ? new CliError(5, "SOURCE_CHANGED", CliText.Get("SourceChanged")) : CliError.FromStorage(failure);
}
