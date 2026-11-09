using System.CommandLine;
using System.Text.Json;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.Transfers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static void RegisterTransferHistory(Command transfer, Option<bool> quiet, CancellationToken token)
    {
        var list = new Command("list", CliText.Get("RunHistory"));
        var listOptions = AddHistoryFilters(list, 100);
        list.SetAction(async (p, _) =>
        {
            var (limit, kind, state) = ParseHistoryFilters(p, listOptions);
            var items = new List<RunSummary>();
            var warnings = new List<string>();
            if (Directory.Exists(RunFiles.Root))
            {
                using var root = ExactLocalPaths.Pin(Path.Combine(RunFiles.Root, "placeholder"), includeLeaf: false);
                var directories = new DirectoryInfo(RunFiles.Root).EnumerateDirectories()
                    .Where(dir => (dir.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        Guid.TryParseExact(dir.Name, "N", out var id) && id != Guid.Empty)
                    .OrderByDescending(dir => dir.LastWriteTimeUtc).ThenBy(dir => dir.Name, StringComparer.Ordinal);
                foreach (var directory in directories)
                {
                    token.ThrowIfCancellationRequested();
                    var id = Guid.ParseExact(directory.Name, "N");
                    try
                    {
                        using var pins = PinRun(id);
                        var active = RunFiles.IsActive(id);
                        var metadata = RunFiles.Read(id);
                        var counts = File.Exists(RunFiles.JournalPath(id))
                            ? await ReadCountsAsync(id, token) : [];
                        if (metadata is null && counts.Count == 0 ||
                            kind is not null && OperationKind(metadata, counts) != kind ||
                            state is not null && !counts.Any(item => item.State == state) &&
                                !(state == TransferState.Running && active))
                            continue;
                        items.Add(Summarize(id, metadata, active, counts));
                        if (items.Count == limit)
                            break;
                    }
                    catch (Exception exception) when (IsHistoryError(exception))
                    {
                        if (warnings.Count < 10)
                            warnings.Add(CliText.Format("RunSkipped", id));
                    }
                }
            }
            WriteSuccess(p, "transfer.list", new { items = items.OrderByDescending(run => run.UpdatedAt).ToArray() }, warnings);
            return 0;
        });

        var show = new Command("show", CliText.Get("RunShow"));
        var showIds = AddHistoryIds(show);
        var showOptions = AddHistoryFilters(show, 100);
        var offset = new Option<int>("--offset") { Description = CliText.Get("RunOffset") };
        show.Options.Add(offset);
        show.SetAction(async (p, _) =>
        {
            var (runId, jobId) = ParseHistoryIds(p, showIds);
            var (limit, kind, state) = ParseHistoryFilters(p, showOptions);
            var skip = p.GetValue(offset);
            if (skip < 0 || skip > int.MaxValue - limit || jobId is not null && (skip != 0 || kind is not null || state is not null))
                throw new CliException(CliError.Arguments);
            try
            {
                using var pins = PinRun(runId);
                var active = RunFiles.IsActive(runId);
                var metadata = RunFiles.Read(runId);
                var path = RunFiles.JournalPath(runId);
                if (!File.Exists(path) && metadata is null)
                    throw RunMissing();
                using var journal = new SqliteTransferJournal(path, readOnly: true);
                var counts = File.Exists(path) ? await journal.ReadHistoryCountsAsync(token) : [];
                var jobs = File.Exists(path)
                    ? await journal.ReadRunJobsAsync(jobId, kind, state, limit + 1, skip, token) : [];
                if (jobId is not null && jobs.Count == 0)
                    throw JobMissing();
                var data = new
                {
                    run = Summarize(runId, metadata, active, counts),
                    items = jobs.Take(limit).Select(DescribeJob).ToArray(),
                    truncated = jobs.Count > limit,
                    nextOffset = jobs.Count > limit ? (int?)(skip + limit) : null
                };
                WriteSuccess(p, "transfer.show", data);
                return 0;
            }
            catch (Exception exception) when (IsHistoryError(exception)) { throw CorruptRun(); }
        });

        var resume = new Command("resume", CliText.Get("RunResume"));
        var resumeIds = AddHistoryIds(resume);
        resume.SetAction(async (p, _) =>
        {
            var (runId, jobId) = ParseHistoryIds(p, resumeIds);
            try
            {
                if (!Directory.Exists(RunFiles.DirectoryPath(runId)))
                    throw RunMissing();
                using var run = RunFiles.Resume(runId);
                var path = RunFiles.JournalPath(runId);
                IReadOnlyList<TransferJob> candidates;
                IReadOnlyList<CrossTransferBatch> batches;
                using (var journal = new SqliteTransferJournal(path, readOnly: true))
                {
                    candidates = await journal.ReadRunJobsAsync(jobId, null, null, jobId is null ? -1 : 1, 0, token);
                    if (jobId is not null && candidates.Count == 0)
                        throw JobMissing();
                    batches = jobId is null && run.HasMetadata ? await journal.ReadRunBatchesAsync(token) : [];
                }
                if (!run.HasMetadata)
                    throw new CliException(new(9, "JOB_NOT_RESUMABLE", CliText.Get("LegacyRunNotResumable"), Details: new { runId }));

                var rejections = new List<ResumeRejection>();
                await using var services = CreateRunServices(path, existing: true);
                var engine = services.GetRequiredService<TransferEngine>();
                var started = false;
                var result = await TransferRunCoordinator.RunAsync(engine, (worker, ct) =>
                    worker.ResumeIsolatedRunAsync(candidates, batches, rejections, () =>
                    {
                        run.StartResume();
                        started = true;
                    }, ct), ShowProgress(p, quiet), token, resume: true);
                var jobs = result.Jobs;
                var completed = jobs.Count(job => job.State == TransferState.Completed && !job.Skipped);
                var skipped = jobs.Count(job => job.Skipped);
                var failed = jobs.Where(job => job.State == TransferState.Failed).ToArray();
                var cancelled = jobs.Count(job => job.State == TransferState.Cancelled);
                var details = new
                {
                    runId, resumed = jobs.Count, completed, failed = failed.Length, skipped, cancelled,
                    notResumable = rejections.Count,
                    rejections = rejections.Take(100).Select(rejection => new { rejection.Id, rejection.Reason, rejection.IsBatch }).ToArray(),
                    failures = failed.Take(100).Select(job => new { jobId = job.Id, job.Key, error = CliError.FromTransfer(job.Failure, cross: true).Code }).ToArray(),
                    failuresTruncated = failed.Length > 100
                };
                var failure = result.SubmissionError is { } submitError ? CliError.From(submitError, token)
                    : failed.Length > 0 ? CliError.FromTransfer(failed[0].Failure, cross: true) : null;
                var error = token.IsCancellationRequested ? CliError.Cancelled
                    : result.BackgroundError is not null ? new CliError(1, "TRANSFER_WORKER_ERROR", CliText.Get("WorkerFailure"))
                    : !started && failure is null ? new CliError(9, "JOB_NOT_RESUMABLE", CliText.Get("JobNotResumable"))
                    : failure is not null && completed + skipped > 0 ? new CliError(7, "PARTIAL_FAILURE", CliText.Get("PartialFailure"))
                    : failure;
                if (started)
                    run.Finish(error?.ExitCode ?? 0);
                if (error is not null)
                    return WriteError(error with { Details = details }, IsJson(p));
                WriteSuccess(p, "transfer.resume", details);
                return 0;
            }
            catch (Exception exception) when (IsHistoryError(exception)) { throw CorruptRun(); }
        });
        foreach (var command in new[] { list, show, resume })
            transfer.Subcommands.Add(command);
    }

    private sealed record RunSummary(Guid RunId, string Operation, string Status, int Total, int Completed,
        int Skipped, int Failed, int Cancelled, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, bool IsActive, bool Legacy);

    private static RunSummary Summarize(Guid id, RunMetadata? metadata, bool active, IReadOnlyList<TransferHistoryCount> counts)
    {
        var total = counts.Sum(item => item.Count);
        var completed = counts.Where(item => item.State == TransferState.Completed && !item.Skipped).Sum(item => item.Count);
        var skipped = counts.Where(item => item.Skipped).Sum(item => item.Count);
        var failed = counts.Where(item => item.State == TransferState.Failed).Sum(item => item.Count);
        var cancelled = counts.Where(item => item.State == TransferState.Cancelled).Sum(item => item.Count);
        var status = active ? "running" : metadata?.Status is "running" ? "interrupted"
            : failed > 0 ? "failed" : completed + skipped + cancelled < total ? "interrupted"
            : metadata?.Status ?? (cancelled > 0 ? "cancelled" : "completed");
        var updated = counts.Count == 0 ? metadata?.UpdatedAt : counts.Max(item => item.UpdatedAt);
        if (metadata is not null && metadata.UpdatedAt > updated)
            updated = metadata.UpdatedAt;
        return new RunSummary(id, OperationKind(metadata, counts)?.ToString().ToLowerInvariant() ?? "unknown",
            status, total, completed, skipped, failed, cancelled,
            metadata?.StartedAt ?? (counts.Count == 0 ? null : counts.Min(item => item.CreatedAt)),
            updated, active, metadata is null);
    }

    private static TransferKind? OperationKind(RunMetadata? metadata, IReadOnlyList<TransferHistoryCount> counts) =>
        metadata is not null ? Enum.Parse<TransferKind>(metadata.Operation, ignoreCase: true)
            : counts.Count == 0 ? null : counts[0].Kind;

    private static object DescribeJob(TransferJob job) => new
    {
        jobId = job.Id, job.BatchId, kind = (job.CrossTransfer?.Move == true ? TransferKind.Move : job.Kind).ToString().ToLowerInvariant(),
        state = job.State.ToString().ToLowerInvariant(), job.ConnectionId, job.Bucket, job.Key,
        job.LocalPath, job.BytesTransferred, job.TotalBytes,
        progress = job.TotalBytes > 0 ? Math.Clamp(100d * job.BytesTransferred / job.TotalBytes.Value, 0, 100)
            : job.State == TransferState.Completed ? 100d : 0d,
        job.Attempt, conflict = job.Conflict.ToString().ToLowerInvariant(),
        failure = job.Failure is null ? null : new { code = CliError.FromTransfer(job.Failure, cross: true).Code, job.Failure.Retryable },
        createdAt = job.CreatedAt.ToUniversalTime(), updatedAt = job.UpdatedAt.ToUniversalTime(),
        cleanupPending = job.Failure?.CleanupPending == true || job.CrossTransfer?.CleanupPending == true
    };

    private static IDisposable PinRun(Guid id)
    {
        if (!Directory.Exists(RunFiles.DirectoryPath(id)))
            throw RunMissing();
        var path = RunFiles.JournalPath(id);
        var pins = ExactLocalPaths.Pin(path, includeLeaf: File.Exists(path), leafShare: FileShare.ReadWrite);
        try { RunFiles.ValidateFiles(id); return pins; }
        catch { pins.Dispose(); throw; }
    }

    private static async Task<IReadOnlyList<TransferHistoryCount>> ReadCountsAsync(Guid id, CancellationToken token)
    {
        using var journal = new SqliteTransferJournal(RunFiles.JournalPath(id), readOnly: true);
        return await journal.ReadHistoryCountsAsync(token);
    }

    private static (Option<int> Limit, Option<string?> Kind, Option<string?> State) AddHistoryFilters(Command command, int defaultLimit)
    {
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => defaultLimit, Description = CliText.Get("RunLimit") };
        var kind = new Option<string?>("--kind") { Description = CliText.Get("RunKind") };
        var state = new Option<string?>("--state") { Description = CliText.Get("RunState") };
        foreach (var option in new Option[] { limit, kind, state })
            command.Options.Add(option);
        return (limit, kind, state);
    }

    private static (int Limit, TransferKind? Kind, TransferState? State) ParseHistoryFilters(ParseResult parsed,
        (Option<int> Limit, Option<string?> Kind, Option<string?> State) options)
    {
        var limit = parsed.GetValue(options.Limit);
        if (limit is < 1 or > 500)
            throw new CliException(CliError.Arguments);
        return (limit, ParseFilter<TransferKind>(parsed.GetValue(options.Kind)), ParseFilter<TransferState>(parsed.GetValue(options.State)));
    }

    private static T? ParseFilter<T>(string? value) where T : struct, Enum =>
        value is null ? null : Enum.GetNames<T>().Contains(value, StringComparer.OrdinalIgnoreCase)
            ? Enum.Parse<T>(value, ignoreCase: true) : throw new CliException(CliError.Arguments);

    private static (Argument<string> Run, Option<string?> Job) AddHistoryIds(Command command)
    {
        var run = new Argument<string>("run-id") { Description = CliText.Get("RunId") };
        var job = new Option<string?>("--job") { Description = CliText.Get("JobId") };
        command.Arguments.Add(run);
        command.Options.Add(job);
        return (run, job);
    }

    private static (Guid Run, Guid? Job) ParseHistoryIds(ParseResult parsed, (Argument<string> Run, Option<string?> Job) ids) =>
        (ParseHistoryId(parsed.GetValue(ids.Run)!), parsed.GetValue(ids.Job) is { } job ? ParseHistoryId(job) : null);

    private static Guid ParseHistoryId(string value) =>
        (Guid.TryParseExact(value, "N", out var id) || Guid.TryParseExact(value, "D", out id)) && id != Guid.Empty
            ? id : throw new CliException(CliError.Arguments);

    private static bool IsHistoryError(Exception exception) => exception is SqliteException or JsonException
        or IOException or UnauthorizedAccessException or FormatException or InvalidCastException or ArgumentException;

    private static CliException CorruptRun() => new(new(8, "RUN_JOURNAL_CORRUPTED", CliText.Get("RunCorrupted")));
    private static CliException RunMissing() => new(new(3, "RUN_NOT_FOUND", CliText.Get("RunMissing")));
    private static CliException JobMissing() => new(new(3, "JOB_NOT_FOUND", CliText.Get("JobMissing")));
}
