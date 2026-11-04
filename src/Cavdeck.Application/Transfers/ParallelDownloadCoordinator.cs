using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

// An unsupported or malformed fixed-range response may fall back to the existing
// sequential downloader. Source changes and authorization failures must not.
internal sealed class ParallelDownloadUnavailableException(string message) : Exception(message);

internal sealed class DownloadRangeIncompleteException : IOException
{
    public DownloadRangeIncompleteException() : base("The server ended a range response before all requested bytes arrived.") { }
}

internal sealed class ParallelDownloadCoordinator(
    ITransferSink sink, ITransferClock clock, TransferEngineOptions options, ConcurrencyGate slots)
{
    private const int BufferSize = 128 * 1024;
    private const int MaxPartRecords = 10_000;

    public async Task<TransferJob> RunAsync(
        TransferJob job, IRemoteTransferLease lease, string partial,
        RemoteIdentity remote, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remote.ETag) || remote.Size <= 0)
            throw new ParallelDownloadUnavailableException("Parallel download requires an object ETag and a positive length.");

        var plan = Plan(remote.Size, options.DownloadPartSizeBytes);
        var parent = Path.GetDirectoryName(partial);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var exists = File.Exists(partial);
        if (exists && new FileInfo(partial).Length != remote.Size)
        {
            TransferFiles.DeleteIfExists(partial);
            exists = false;
        }

        if (!exists)
            await sink.ClearPartsAsync(job.Id, cancellationToken);

        // Never infer completed bytes from a preallocated file's length. Verify
        // every durable checkpoint against bytes on disk before reusing it.
        var recorded = exists
            ? await sink.GetPartsAsync(job.Id, cancellationToken)
            : [];
        var byNumber = plan.ToDictionary(part => part.Number);
        var trusted = new Dictionary<int, TransferPart>();
        foreach (var part in recorded)
        {
            if (!byNumber.TryGetValue(part.Number, out var expected)
                || part.Offset != expected.Offset || part.Length != expected.Length
                || string.IsNullOrWhiteSpace(part.Sha256)
                || trusted.ContainsKey(part.Number))
                continue;

            await using var window = new FileWindowStream(partial, part.Offset, part.Length);
            var hash = await SHA256.HashDataAsync(window, cancellationToken);
            if (string.Equals(Convert.ToHexString(hash), part.Sha256, StringComparison.OrdinalIgnoreCase))
                trusted[part.Number] = part;
        }

        if (trusted.Count != recorded.Count)
        {
            await sink.ClearPartsAsync(job.Id, cancellationToken);
            foreach (var part in trusted.Values.OrderBy(part => part.Number))
                await sink.SavePartAsync(job.Id, part, cancellationToken);
        }

        var completedBytes = trusted.Values.Sum(part => part.Length);
        job = job with { BytesTransferred = completedBytes, Resume = ResumeMode.ParallelRange };
        sink.ResetProgress(job);
        await sink.PersistCheckpointAsync(job with { UpdatedAt = clock.UtcNow }, cancellationToken);

        await using var target = new FileStream(
            partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (target.Length != remote.Size)
            target.SetLength(remote.Size);

        var pending = plan.Where(part => !trusted.ContainsKey(part.Number)).ToArray();
        if (pending.Length == 0)
            return job with { BytesTransferred = remote.Size };

        using var checkpointGate = new SemaphoreSlim(1, 1);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new DownloadProgressReporter(sink, job, remote.Size, completedBytes);
        Exception? failure = null;

        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxConcurrentParts,
                CancellationToken = linked.Token
            }, async (part, token) =>
            {
                var acquired = false;
                try
                {
                    await slots.AcquireAsync(token);
                    acquired = true;
                    if (Volatile.Read(ref failure) is not null)
                        return;

                    await using var body = await lease.OpenRangeAsync(
                        job.Bucket, job.Key, part.Offset, part.Length,
                        remote.ETag!, remote.VersionId, token);
                    if (!body.RangeHonored || string.IsNullOrWhiteSpace(body.Identity.ETag)
                        || !string.Equals(RemoteIdentity.Normalize(body.Identity.ETag),
                            RemoteIdentity.Normalize(remote.ETag!), StringComparison.OrdinalIgnoreCase))
                        throw new ParallelDownloadUnavailableException("The endpoint did not honor a fixed byte range.");

                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var written = 0L;
                        while (written < part.Length)
                        {
                            var requested = (int)Math.Min(buffer.Length, part.Length - written);
                            var read = await body.Content.ReadAsync(buffer.AsMemory(0, requested), token);
                            if (read == 0)
                                throw new DownloadRangeIncompleteException();

                            await RandomAccess.WriteAsync(
                                target.SafeFileHandle, buffer.AsMemory(0, read),
                                part.Offset + written, token);
                            digest.AppendData(buffer, 0, read);
                            written += read;
                            progress.ReportPart(part.Number, part.Length, written);
                        }

                        // Content-Length was checked in the S3 adapter, but detect
                        // a misbehaving test/custom provider that streams extra bytes.
                        if (await body.Content.ReadAsync(buffer.AsMemory(0, 1), token) != 0)
                            throw new ParallelDownloadUnavailableException("The endpoint returned too many range bytes.");

                        var saved = part with { Sha256 = Convert.ToHexString(digest.GetHashAndReset()) };
                        await checkpointGate.WaitAsync(token);
                        try
                        {
                            // Data must reach durable storage before the SQLite
                            // checkpoint may claim that the part is complete.
                            RandomAccess.FlushToDisk(target.SafeFileHandle);
                            await sink.SavePartAsync(job.Id, saved, token);
                            completedBytes += part.Length;
                            await sink.PersistCheckpointAsync(job with
                            {
                                BytesTransferred = completedBytes,
                                UpdatedAt = clock.UtcNow
                            }, token);
                            progress.CompletePart(part.Number, part.Length);
                        }
                        finally
                        {
                            checkpointGate.Release();
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch (StorageException error) when (IsUnsupportedRange(error))
                {
                    Interlocked.CompareExchange(ref failure,
                        new ParallelDownloadUnavailableException("The endpoint does not support reliable fixed byte ranges."), null);
                    linked.Cancel();
                }
                catch (NotSupportedException)
                {
                    // The lease interface intentionally supplies a default
                    // implementation that throws for providers without ranges.
                    Interlocked.CompareExchange(ref failure,
                        new ParallelDownloadUnavailableException("Fixed ranges are not supported by this provider."), null);
                    linked.Cancel();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref failure, error, null);
                    linked.Cancel();
                }
                finally
                {
                    if (acquired)
                        slots.Release();
                }
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && failure is not null)
        {
            // A failed part cancels its siblings. Surface the original failure.
        }
        catch
        {
            sink.ResetProgress(job with { BytesTransferred = completedBytes });
            throw;
        }

        if (failure is not null)
        {
            sink.ResetProgress(job with { BytesTransferred = completedBytes });
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return job with { BytesTransferred = remote.Size, UpdatedAt = clock.UtcNow };
    }

    private static bool IsUnsupportedRange(StorageException error) =>
        error.Failure.ErrorCode is "InvalidRangeResponse" or "InvalidRange" or "NotImplemented"
            or "NotSupported"
        || error.Failure.StatusCode is 416 or 501
        || error.Failure.Kind == StorageFailureKind.Unsupported;

    private static IReadOnlyList<TransferPart> Plan(long total, long preferred)
    {
        var minimum = total / MaxPartRecords + (total % MaxPartRecords == 0 ? 0 : 1);
        var partSize = Math.Max(Math.Max(1, preferred), minimum);
        var parts = new List<TransferPart>((int)Math.Min(MaxPartRecords, total / partSize + 1));
        var offset = 0L;
        var number = 1;
        while (offset < total)
        {
            var length = Math.Min(partSize, total - offset);
            parts.Add(new TransferPart(number++, offset, length, null));
            offset += length;
        }

        return parts;
    }

    private sealed class DownloadProgressReporter(
        ITransferSink sink, TransferJob job, long totalBytes, long completedBytes)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
        private readonly object _gate = new();
        private readonly Dictionary<int, long> _inFlight = [];
        private long _completed = completedBytes;
        private long _lastReported = completedBytes;
        private long _lastTimestamp;

        public void ReportPart(int number, long length, long transferred)
        {
            lock (_gate)
            {
                _inFlight[number] = Math.Max(_inFlight.GetValueOrDefault(number),
                    Math.Clamp(transferred, 0, length));
                PublishLocked(force: false);
            }
        }

        public void CompletePart(int number, long length)
        {
            lock (_gate)
            {
                _inFlight.Remove(number);
                _completed += length;
                PublishLocked(force: true);
            }
        }

        private void PublishLocked(bool force)
        {
            var aggregate = _completed;
            foreach (var bytes in _inFlight.Values)
                aggregate += Math.Min(bytes, Math.Max(0, totalBytes - aggregate));
            aggregate = Math.Min(aggregate, totalBytes);
            if (aggregate <= _lastReported)
                return;

            var now = Stopwatch.GetTimestamp();
            if (!force && _lastTimestamp != 0
                && Stopwatch.GetElapsedTime(_lastTimestamp, now) < Interval)
                return;

            _lastReported = aggregate;
            _lastTimestamp = now;
            sink.ReportProgress(job with { BytesTransferred = aggregate });
        }
    }
}
