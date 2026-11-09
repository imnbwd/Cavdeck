using System.Text.Json;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Configuration;

namespace Cavdeck.Cli;

internal sealed record RunMetadata(int SchemaVersion, Guid RunId, string Operation, string Status,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, DateTimeOffset? EndedAt);

internal sealed class RunFiles : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly FileStream _lock;
    private readonly IDisposable _pins;
    private RunMetadata _metadata;

    private RunFiles(FileStream runLock, IDisposable pins, RunMetadata metadata)
    {
        _lock = runLock;
        _pins = pins;
        _metadata = metadata;
    }

    internal static string Root => Path.Combine(CavdeckDataPaths.LocalDirectory, "cli", "runs");
    internal static string DirectoryPath(Guid runId) => Path.Combine(Root, runId.ToString("N"));
    internal static string JournalPath(Guid runId) => Path.Combine(DirectoryPath(runId), "transfers.db");
    private static string LockPath(Guid runId) => Path.Combine(DirectoryPath(runId), "run.lock");
    private static string MetadataPath(Guid runId) => Path.Combine(DirectoryPath(runId), "run.json");

    public static RunFiles Create(Guid runId, string operation)
    {
        var now = DateTimeOffset.UtcNow;
        return Open(new RunMetadata(1, runId, operation, "running", now, now, null), create: true);
    }

    public static RunFiles Resume(Guid runId) =>
        Open(new RunMetadata(1, runId, "upload", "running", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
            create: false);

    public bool HasMetadata { get; private init; }

    public void StartResume()
    {
        _metadata = _metadata with { Status = "running", UpdatedAt = DateTimeOffset.UtcNow, EndedAt = null };
        Write();
    }

    private static RunFiles Open(RunMetadata metadata, bool create)
    {
        var id = metadata.RunId;
        var journal = JournalPath(id);
        var pins = ExactLocalPaths.Pin(journal, includeLeaf: !create && File.Exists(journal),
            createDirectories: create, leafShare: FileShare.ReadWrite);
        FileStream? runLock = null;
        try
        {
            ValidateFiles(id);
            if (create && File.Exists(JournalPath(id)))
                throw new IOException("The run directory already contains a journal.");
            if (!create && !File.Exists(JournalPath(id)))
                throw new CliException(new(3, "RUN_NOT_FOUND", CliText.Get("RunMissing")));
            try
            {
                runLock = new FileStream(LockPath(id), create ? FileMode.CreateNew : FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                throw new CliException(new(5, "RUN_IN_PROGRESS", CliText.Get("RunInProgress"), true));
            }
            var previous = create ? null : Read(id);
            var run = new RunFiles(runLock, pins, previous ?? metadata) { HasMetadata = previous is not null || create };
            if (create)
                run.Write();
            return run;
        }
        catch
        {
            runLock?.Dispose();
            pins.Dispose();
            throw;
        }
    }

    public void Finish(int exitCode)
    {
        var now = DateTimeOffset.UtcNow;
        _metadata = _metadata with { Status = exitCode switch { 0 => "completed", 130 => "cancelled", _ => "failed" },
            UpdatedAt = now, EndedAt = now };
        Write();
    }

    private void Write()
    {
        var path = MetadataPath(_metadata.RunId);
        var temp = path + ".tmp";
        ExactLocalPaths.RejectExistingReparsePoint(temp);
        if (File.Exists(temp))
            File.Delete(temp);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _metadata, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    internal static RunMetadata? Read(Guid id)
    {
        try
        {
            using var stream = new FileStream(MetadataPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 16_384)
                throw new InvalidDataException("Run metadata is too large.");
            var metadata = JsonSerializer.Deserialize<RunMetadata>(stream, Json);
            if (metadata is not { SchemaVersion: 1 } || metadata.RunId != id ||
                metadata.Operation is not ("upload" or "download" or "copy" or "move") ||
                metadata.Status is not ("running" or "failed" or "cancelled" or "completed"))
                throw new InvalidDataException("Invalid run metadata.");
            return metadata;
        }
        catch (FileNotFoundException) { return null; }
    }

    internal static bool IsActive(Guid id)
    {
        try
        {
            using var stream = new FileStream(LockPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException exception) when (IsSharingViolation(exception)) { return true; }
    }

    internal static void ValidateFiles(Guid id)
    {
        var journal = JournalPath(id);
        foreach (var path in new[] { journal, journal + "-wal", journal + "-shm", journal + "-journal",
            LockPath(id), MetadataPath(id), MetadataPath(id) + ".tmp" })
            ExactLocalPaths.RejectExistingReparsePoint(path);
    }

    internal static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xffff) is 32 or 33;

    public void Dispose()
    {
        _lock.Dispose();
        _pins.Dispose();
    }
}
