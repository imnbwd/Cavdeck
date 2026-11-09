using System.CommandLine;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static ServiceProvider CreateRunServices(string journal, bool existing = false)
    {
        var registration = new ServiceCollection().AddCavdeckCore(journal, readOnlyCatalog: true);
        registration.AddSingleton(new TransferEngineOptions { IsolatedRun = true });
        if (existing)
            registration.AddSingleton<ITransferJournal>(_ => new Cavdeck.Infrastructure.Transfers.SqliteTransferJournal(journal, existing: true));
        return registration.BuildServiceProvider();
    }

    private static TransferConflictPolicy ParseConflict(string? value) => value switch
    {
        "fail" => TransferConflictPolicy.Fail,
        "skip" => TransferConflictPolicy.Skip,
        "overwrite" => TransferConflictPolicy.Overwrite,
        "rename" => TransferConflictPolicy.Rename,
        _ => throw new CliException(CliError.Arguments)
    };

    private static int WriteTransferResult(ParseResult p, string operation, Guid runId,
        TransferRunResult result, CancellationToken token, object? batch = null, RunFiles? run = null)
    {
        var completed = result.Jobs.Count(job => job.State == TransferState.Completed && !job.Skipped);
        var skipped = result.Jobs.Count(job => job.Skipped);
        var failed = result.Jobs.Where(job => job.State == TransferState.Failed).ToArray();
        var cancelled = result.Jobs.Count(job => job.State == TransferState.Cancelled);
        var failures = failed.Select(job => new { job.Id, job.Key, error = CliError.FromTransfer(job.Failure, batch is not null).Code })
            .Take(100).ToArray();
        var counts = new
        {
            runId, completed, skipped, failed = failed.Length, cancelled,
            bytesTransferred = result.Jobs.Where(job => job.State == TransferState.Completed && !job.Skipped)
                .Sum(job => job.BytesTransferred),
            failures, failuresTruncated = failed.Length > failures.Length,
            cleanupPending = result.Jobs.Count(job => job.State != TransferState.Completed && (job.UploadId is not null || job.Failure?.CleanupPending == true))
        };
        object data = batch is null ? counts : new
        {
            counts.runId, batch, counts.completed, counts.skipped, counts.failed, counts.cancelled,
            counts.bytesTransferred, counts.failures, counts.failuresTruncated, counts.cleanupPending,
            jobs = result.Jobs.Take(100).Select(job => new { jobId = job.Id, job.BatchId, job.Key, state = job.State.ToString() }).ToArray(),
            jobsTruncated = result.Jobs.Count > 100
        };

        var submission = result.SubmissionError is { } submitError ? CliError.From(submitError, token) : null;
        var failure = failed.Length > 0 ? CliError.FromTransfer(failed[0].Failure, batch is not null) : null;
        var partial = new CliError(7, "PARTIAL_FAILURE", CliText.Get("PartialFailure"));
        var error = token.IsCancellationRequested ? CliError.Cancelled
            : result.BackgroundError is not null ? new CliError(1, "TRANSFER_WORKER_ERROR", CliText.Get("WorkerFailure"))
            : (submission is not null || failure is not null) && completed + skipped > 0 ? partial
            : submission ?? failure;
        run?.Finish(error?.ExitCode ?? 0);
        if (error is not null)
            return WriteError(error with { Details = data }, IsJson(p),
                p.GetValue<bool>("--verbose") ? (result.BackgroundError ?? result.SubmissionError)?.GetType().Name : null);

        WriteSuccess(p, operation, data);
        return 0;
    }
}
