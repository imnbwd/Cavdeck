using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.Transfers;
using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class TransferRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cavdeck-cli-").FullName;
    private static readonly StorageConnection Connection = new(Guid.NewGuid(), "Test", StorageProviderKind.Minio,
        "http://localhost", "us-east-1", "fictional-credential");

    [Theory]
    [InlineData(TransferConflictPolicy.Fail, TransferState.Failed, false, "old")]
    [InlineData(TransferConflictPolicy.Skip, TransferState.Completed, true, "old")]
    [InlineData(TransferConflictPolicy.Overwrite, TransferState.Completed, false, "new")]
    [InlineData(TransferConflictPolicy.Rename, TransferState.Completed, false, "old")]
    public async Task UploadConflictPolicy(TransferConflictPolicy policy, TransferState state, bool skipped, string final)
    {
        var file = await LocalFileAsync("本 地.txt", "new");
        var store = new Remote();
        store.Objects["dir/exact.txt"] = "old";
        using var journal = Journal("upload");
        var result = await RunAsync(store, journal, (engine, token) => engine.EnqueueExactUploadsAsync(
            Connection, "bucket", "dir/exact.txt", file, false, policy, token));
        var job = Assert.Single(result.Jobs);
        Assert.Equal(state, job.State);
        Assert.Equal(skipped, job.Skipped);
        Assert.Equal(final, store.Objects["dir/exact.txt"]);
        if (policy == TransferConflictPolicy.Rename)
            Assert.Equal("new", store.Objects["dir/exact (1).txt"]);
        if (policy == TransferConflictPolicy.Fail)
            Assert.Equal("TARGET_CONFLICT", CliError.FromTransfer(job.Failure).Code);
        Assert.Equal(state, Assert.Single(await journal.LoadRecentAsync(10, default)).State);
    }

    [Fact]
    public async Task UploadRaceUsesServerCondition()
    {
        var file = await LocalFileAsync("input", "new");
        var store = new Remote { BeforePut = (remote, key) => remote.Objects[key] = "racer" };
        using var journal = Journal("race");
        var result = await RunAsync(store, journal, (engine, token) => engine.EnqueueExactUploadsAsync(
            Connection, "bucket", "dir/a//b", file, false, TransferConflictPolicy.Fail, token));
        Assert.Equal(TransferFailureKind.Conflict, Assert.Single(result.Jobs).Failure?.Kind);
        Assert.Equal("racer", store.Objects["dir/a//b"]);
    }

    [Fact]
    public async Task UnverifiedProviderIsNeverWritten()
    {
        var file = await LocalFileAsync("input", "new");
        var store = new Remote { SupportsWrites = false };
        using var journal = Journal("unsupported");
        var result = await RunAsync(store, journal, (engine, token) => engine.EnqueueExactUploadsAsync(
            Connection, "bucket", "key", file, false, TransferConflictPolicy.Fail, token));
        Assert.Equal("UNSUPPORTED_CONDITIONAL_WRITE", CliError.FromTransfer(Assert.Single(result.Jobs).Failure).Code);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task RecursiveUploadWaitsForAllJobsAndPreservesRelativeNames()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "b");
        var store = new Remote();
        using var journal = Journal("tree");
        var result = await RunAsync(store, journal, (engine, token) => engine.EnqueueExactUploadsAsync(
            Connection, "bucket", "dir/", source, true, TransferConflictPolicy.Fail, token));
        Assert.Equal(3, result.Jobs.Count);
        Assert.All(result.Jobs, job => Assert.Equal(TransferState.Completed, job.State));
        Assert.Equal(new[] { "dir/b.txt", "dir/empty/", "dir/nested/a.txt" }, store.Objects.Keys.Order());
        Assert.Equal(3, (await journal.LoadRecentAsync(10, default)).Count);
    }

    [Fact]
    public async Task DownloadRacePreservesRacingFileAndRemovesTemporaryFile()
    {
        var file = Path.Combine(_root, "local file.txt");
        var store = new Remote { BeforeRead = async (_, token) => await File.WriteAllTextAsync(file, "racer", token) };
        store.Objects["remote/deep/name"] = "download";
        using var journal = Journal("download-race");
        var result = await RunAsync(store, journal, (engine, token) => engine.EnqueueExactDownloadsAsync(
            Connection, "bucket", Items([new("remote/deep/name", false, null, "local file.txt")], token),
            _root, TransferConflictPolicy.Fail, token));
        Assert.Equal(TransferFailureKind.Conflict, Assert.Single(result.Jobs).Failure?.Kind);
        Assert.Equal("racer", await File.ReadAllTextAsync(file));
        Assert.Empty(Directory.EnumerateFiles(_root, ".cavdeck-*"));
    }

    [Fact]
    public async Task DownloadsUseExactRelativePathsAndRejectWindowsCollisions()
    {
        var store = new Remote();
        store.Objects["base/one"] = "one";
        store.Objects["base/two"] = "two";
        using var journal = Journal("download");
        DownloadItem[] items =
        [
            new("base/one", false, null, "内/名 字.txt"),
            new("base/two", false, null, "内/名 字.TXT"),
            new("base/two", false, null, "other//alias"),
            new("base/two", false, null, "../outside"),
            new("base/two", false, null, "CON.txt")
        ];
        var result = await RunAsync(store, journal, (engine, token) => engine.EnqueueExactDownloadsAsync(
            Connection, "bucket", Items(items, token), _root, TransferConflictPolicy.Overwrite, token));
        Assert.Single(result.Jobs, job => job.State == TransferState.Completed);
        Assert.Equal(4, result.Jobs.Count(job => job.State == TransferState.Failed));
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(_root, "内", "名 字.txt")));
    }

    [Fact]
    public async Task CancellationWaitsForRunningWorkerAndPersistsCancelledState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var store = new Remote
        {
            BeforeRead = async (_, token) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cleaned = true; }
            }
        };
        store.Objects["key"] = "data";
        using var journal = Journal("cancel");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = RunAsync(store, journal, (engine, token) => engine.EnqueueExactDownloadsAsync(
            Connection, "bucket", Items([new("key", false, null, "local")], token), _root,
            TransferConflictPolicy.Fail, token), cancel.Token);
        await started.Task.WaitAsync(cancel.Token);
        await cancel.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cleaned);
        Assert.Equal(TransferState.Cancelled, Assert.Single(result.Jobs).State);
        Assert.Equal(TransferState.Cancelled, Assert.Single(await journal.LoadRecentAsync(10, default)).State);
        Assert.Empty(Directory.EnumerateFiles(_root, ".cavdeck-*"));
    }

    [Fact]
    public async Task ConcurrentRunsNeverRecoverOtherJobs()
    {
        var file = await LocalFileAsync("upload", "content");
        var store = new Remote();
        using var desktop = Journal("desktop");
        using var first = Journal("run-1");
        using var second = Journal("run-2");
        var pending = TransferJob.Create(TransferKind.Upload, Connection.Id, "bucket", "desktop",
            file, null, TransferConflictPolicy.Overwrite, Guid.NewGuid(), null, false, DateTimeOffset.UtcNow);
        await desktop.UpsertAsync(pending, default);
        var runs = await Task.WhenAll(new[] { first, second }.Select((journal, index) =>
            RunAsync(store, journal, (engine, token) => engine.EnqueueExactUploadsAsync(
                Connection, "bucket", $"run-{index}", file, false, TransferConflictPolicy.Fail, token))));
        Assert.All(runs, run => Assert.Equal(TransferState.Completed, Assert.Single(run.Jobs).State));
        Assert.Equal(TransferState.Queued, Assert.Single(await desktop.LoadUnfinishedAsync(default)).State);
        Assert.Equal(new[] { "run-0", "run-1" }, store.Objects.Keys.Order());
    }

    private static async IAsyncEnumerable<DownloadItem> Items(IEnumerable<DownloadItem> items,
        [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            yield return item;
        }
        await Task.CompletedTask;
    }

    private async Task<string> LocalFileAsync(string name, string contents)
    {
        var file = Path.Combine(_root, name);
        await File.WriteAllTextAsync(file, contents);
        return file;
    }

    private SqliteTransferJournal Journal(string name) => new(Path.Combine(_root, name + ".db"));

    private static async Task<TransferRunResult> RunAsync(Remote remote, ITransferJournal journal,
        Func<TransferEngine, CancellationToken, Task> submit, CancellationToken token = default)
    {
        await using var engine = new TransferEngine(remote, journal, new Connections(),
            new TransferEngineOptions { IsolatedRun = true, MaxAttempts = 1 });
        return await TransferRunCoordinator.RunAsync(engine, submit, progress: false, token);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Connections : ITransferConnections
    {
        public Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageConnection>>([Connection]);
        public Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(id == Connection.Id ? Connection : null);
    }

    private sealed class Remote : IRemoteObjectTransfer, IRemoteTransferLease
    {
        public ConcurrentDictionary<string, string> Objects { get; } = new();
        public Action<Remote, string>? BeforePut { get; init; }
        public Func<string, CancellationToken, Task>? BeforeRead { get; init; }
        public bool SupportsWrites { get; init; } = true;

        public Task<IRemoteTransferLease> OpenAsync(StorageConnection connection, bool write, CancellationToken token) =>
            Task.FromResult<IRemoteTransferLease>(this);
        public void EnsureVerifiedConditionalPut()
        {
            if (!SupportsWrites)
                throw new StorageException(new(StorageFailureKind.Unsupported, "ConditionalWriteUnsupported", null, null, null));
        }

        public Task<RemoteIdentity?> HeadAsync(string bucket, string key, CancellationToken token) =>
            Task.FromResult(Objects.TryGetValue(key, out var contents) ? Identity(contents) : null);

        public async Task<RemoteIdentity> PutConditionalAsync(string bucket, string key, Stream content,
            long length, string? contentType, string contentMd5, string? expectedETag, bool requireMissing,
            CancellationToken token)
        {
            BeforePut?.Invoke(this, key);
            using var reader = new StreamReader(content, leaveOpen: true);
            var data = await reader.ReadToEndAsync(token);
            var stored = requireMissing
                ? Objects.TryAdd(key, data)
                : Objects.TryGetValue(key, out var existing) && Identity(existing).ETag == expectedETag
                    && Objects.TryUpdate(key, data, existing);
            if (!stored)
                throw new StorageException(new(StorageFailureKind.PreconditionFailed, "PreconditionFailed", 412, null, null));
            return Identity(data);
        }

        public async Task<DownloadBody> OpenDownloadAsync(string bucket, string key, long? rangeStart, CancellationToken token)
        {
            if (BeforeRead is not null)
                await BeforeRead(key, token);
            var contents = Objects[key];
            return new DownloadBody { Content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contents)),
                Identity = Identity(contents), RangeHonored = false };
        }

        private static RemoteIdentity Identity(string contents) =>
            new(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(contents))),
                System.Text.Encoding.UTF8.GetByteCount(contents), DateTimeOffset.UnixEpoch);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void EnsureConditionalMultipartSupported() => throw new NotSupportedException();
        public Task<RemoteIdentity> PutAsync(string bucket, string key, Stream content, long length, string? type,
            string md5, IProgress<long>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<string> InitiateMultipartAsync(string bucket, string key, string? type, CancellationToken token) => throw new NotSupportedException();
        public Task<string> UploadPartAsync(string bucket, string key, string uploadId, int partNumber,
            Stream content, long length, string md5, IProgress<long>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task CompleteMultipartAsync(string bucket, string key, string uploadId, IReadOnlyList<TransferPart> parts,
            CancellationToken token) => throw new NotSupportedException();
        public Task AbortMultipartAsync(string bucket, string key, string uploadId, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<TransferPart>> ListPartsAsync(string bucket, string key, string uploadId,
            CancellationToken token) => throw new NotSupportedException();
        public IAsyncEnumerable<ListedObject> ListAsync(string bucket, string prefix, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(string bucket, string key, CancellationToken token) => throw new NotSupportedException();
    }
}
