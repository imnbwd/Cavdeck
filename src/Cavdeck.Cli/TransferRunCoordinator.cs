using System.Threading.Channels;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Cli;

internal sealed record TransferRunResult(
    IReadOnlyList<TransferJob> Jobs, Exception? SubmissionError, Exception? BackgroundError);

internal static class TransferRunCoordinator
{
    public static async Task<TransferRunResult> RunAsync(TransferEngine engine,
        Func<TransferEngine, CancellationToken, Task> submit, bool progress, CancellationToken cancellationToken,
        bool resume = false)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var changes = Channel.CreateBounded<bool>(1);
        Exception? backgroundError = null;
        Exception? submissionError = null;
        long lastProgress = 0;

        void Changed(TransferJob job)
        {
            if (!job.IsOpen)
                changes.Writer.TryWrite(true);
            if (!progress || job.State != TransferState.Running || job.BytesTransferred <= 0)
                return;
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref lastProgress);
            if (now - previous >= 1000 && Interlocked.CompareExchange(ref lastProgress, now, previous) == previous)
                Console.Error.WriteLine(CliText.Format("Transferred", job.BytesTransferred, job.TotalBytes));
        }

        void Faulted(Exception exception)
        {
            Interlocked.CompareExchange(ref backgroundError, exception, null);
            stop.Cancel();
        }

        engine.Changed += Changed;
        engine.BackgroundFaulted += Faulted;
        try
        {
            try
            {
                if (!resume)
                    await engine.StartAsync(stop.Token);
                await submit(engine, stop.Token);
            }
            catch (Exception exception)
            {
                submissionError = exception;
            }

            try
            {
                while (engine.HasActiveTransfers)
                    await changes.Reader.ReadAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

            var jobs = await engine.FinishIsolatedRunAsync(preserveRetryableState: true);
            return new(jobs, submissionError, backgroundError);
        }
        finally
        {
            engine.Changed -= Changed;
            engine.BackgroundFaulted -= Faulted;
        }
    }
}
