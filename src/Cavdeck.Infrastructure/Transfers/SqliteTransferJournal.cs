using System.Text.Json;
using System.Text.Json.Serialization;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Configuration;
using Cavdeck.Domain.Transfers;
using Microsoft.Data.Sqlite;

namespace Cavdeck.Infrastructure.Transfers;

public sealed class SqliteTransferJournal : ITransferJournal, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqliteTransferJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public static string DefaultPath()
    {
        var root = CavdeckDataPaths.LocalDirectory;
        Directory.CreateDirectory(root);
        return Path.Combine(root, "transfers.db");
    }

    public async Task<TransferHistoryRemoval> RemoveTerminalAsync(
        TransferHistoryScope scope, Guid? id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var candidates = new List<TransferJob>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = """
                    SELECT payload FROM jobs
                    WHERE state IN ('Failed', 'Completed', 'Cancelled')
                    """ + (scope == TransferHistoryScope.Single ? " AND id = $id;" : ";");
                if (scope == TransferHistoryScope.Single)
                    select.Parameters.AddWithValue("$id", id?.ToString("N") ?? "");
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var job = JsonSerializer.Deserialize<TransferJob>(reader.GetString(0), Json);
                    if (job is not null)
                        candidates.Add(job);
                }
            }

            var removed = new List<Guid>();
            var batchIds = new HashSet<Guid>();
            foreach (var job in candidates)
            {
                var scanning = false;
                if (job.CrossTransfer is not null)
                {
                    await using var check = connection.CreateCommand();
                    check.Transaction = (SqliteTransaction)transaction;
                    check.CommandText = "SELECT state FROM cross_batches WHERE id = $id;";
                    check.Parameters.AddWithValue("$id", job.BatchId.ToString("N"));
                    scanning = string.Equals((string?)await check.ExecuteScalarAsync(cancellationToken),
                        nameof(CrossBatchState.Scanning), StringComparison.Ordinal);
                }
                if (!TransferHistoryRules.CanRemove(job, scope, id, scanning))
                    continue;

                var jobKey = job.Id.ToString("N");
                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = (SqliteTransaction)transaction;
                    delete.CommandText = """
                        DELETE FROM jobs WHERE id = $id
                        AND state IN ('Failed', 'Completed', 'Cancelled');
                        """;
                    delete.Parameters.AddWithValue("$id", jobKey);
                    if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
                        continue;
                }
                await using (var parts = connection.CreateCommand())
                {
                    parts.Transaction = (SqliteTransaction)transaction;
                    parts.CommandText = "DELETE FROM parts WHERE job_id = $id;";
                    parts.Parameters.AddWithValue("$id", jobKey);
                    await parts.ExecuteNonQueryAsync(cancellationToken);
                }
                if (job.CrossTransfer is not null)
                {
                    await using var mapping = connection.CreateCommand();
                    mapping.Transaction = (SqliteTransaction)transaction;
                    mapping.CommandText = "DELETE FROM cross_batch_items WHERE job_id = $id;";
                    mapping.Parameters.AddWithValue("$id", jobKey);
                    await mapping.ExecuteNonQueryAsync(cancellationToken);
                    batchIds.Add(job.BatchId);
                }
                removed.Add(job.Id);
            }

            var removedBatches = new List<Guid>();
            foreach (var batchId in batchIds)
            {
                await using var batch = connection.CreateCommand();
                batch.Transaction = (SqliteTransaction)transaction;
                batch.CommandText = """
                    DELETE FROM cross_batches
                    WHERE id = $id AND state <> 'Scanning'
                    AND NOT EXISTS (
                        SELECT 1 FROM cross_batch_items WHERE batch_id = $id
                    )
                    AND NOT EXISTS (
                        SELECT 1 FROM jobs WHERE json_extract(payload, '$.BatchId') = $guid
                    );
                    """;
                batch.Parameters.AddWithValue("$id", batchId.ToString("N"));
                batch.Parameters.AddWithValue("$guid", batchId.ToString("D"));
                if (await batch.ExecuteNonQueryAsync(cancellationToken) == 1)
                    removedBatches.Add(batchId);
            }
            await transaction.CommitAsync(cancellationToken);
            return new TransferHistoryRemoval(removed, removedBatches);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(TransferJob job, CancellationToken cancellationToken)
    {
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                INSERT INTO jobs (id, state, updated, payload)
                VALUES ($id, $state, $updated, $payload)
                ON CONFLICT(id) DO UPDATE SET
                    state = excluded.state,
                    updated = excluded.updated,
                    payload = excluded.payload;
                """;
            command.Parameters.AddWithValue("$id", job.Id.ToString("N"));
            command.Parameters.AddWithValue("$state", job.State.ToString());
            command.Parameters.AddWithValue("$updated", job.UpdatedAt.UtcDateTime.ToString("o"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job, Json));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task UpsertBatchAsync(CrossTransferBatch batch, CancellationToken cancellationToken) =>
        ExecuteAsync(async command =>
        {
            command.CommandText = """
                INSERT INTO cross_batches (id, state, updated, payload)
                VALUES ($id, $state, $updated, $payload)
                ON CONFLICT(id) DO UPDATE SET
                    state = excluded.state,
                    updated = excluded.updated,
                    payload = excluded.payload;
                """;
            command.Parameters.AddWithValue("$id", batch.Id.ToString("N"));
            command.Parameters.AddWithValue("$state", batch.State.ToString());
            command.Parameters.AddWithValue("$updated", batch.UpdatedAt.UtcDateTime.ToString("o"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(batch, Json));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyList<CrossTransferBatch>> LoadUnfinishedBatchesAsync(CancellationToken cancellationToken)
    {
        var batches = new List<CrossTransferBatch>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = "SELECT payload FROM cross_batches WHERE state = 'Scanning' ORDER BY updated;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var batch = JsonSerializer.Deserialize<CrossTransferBatch>(reader.GetString(0), Json);
                if (batch is not null)
                    batches.Add(batch);
            }
        }, cancellationToken);
        return batches;
    }

    public async Task<IReadOnlyList<CrossTransferBatch>> LoadRecentBatchesAsync(int limit, CancellationToken cancellationToken)
    {
        var batches = new List<CrossTransferBatch>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = "SELECT payload FROM cross_batches ORDER BY updated DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var batch = JsonSerializer.Deserialize<CrossTransferBatch>(reader.GetString(0), Json);
                if (batch is not null)
                    batches.Add(batch);
            }
        }, cancellationToken);
        return batches;
    }

    public async Task<bool> TryCreateCrossJobAsync(TransferJob job, CancellationToken cancellationToken)
    {
        var spec = job.CrossTransfer ?? throw new ArgumentException("A cross-copy job is required.", nameof(job));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var mapping = connection.CreateCommand();
            mapping.Transaction = (SqliteTransaction)transaction;
            mapping.CommandText = """
                INSERT OR IGNORE INTO cross_batch_items
                    (batch_id, destination_key, source_key, job_id)
                VALUES ($batch, $destination, $source, $job);
                """;
            mapping.Parameters.AddWithValue("$batch", job.BatchId.ToString("N"));
            mapping.Parameters.AddWithValue("$destination", spec.Destination.Key);
            mapping.Parameters.AddWithValue("$source", spec.Source.Key);
            mapping.Parameters.AddWithValue("$job", job.Id.ToString("N"));
            var inserted = await mapping.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (!inserted)
            {
                await using var check = connection.CreateCommand();
                check.Transaction = (SqliteTransaction)transaction;
                check.CommandText = """
                    SELECT source_key FROM cross_batch_items
                    WHERE batch_id = $batch AND destination_key = $destination;
                    """;
                check.Parameters.AddWithValue("$batch", job.BatchId.ToString("N"));
                check.Parameters.AddWithValue("$destination", spec.Destination.Key);
                var existing = (string?)await check.ExecuteScalarAsync(cancellationToken);
                if (!string.Equals(existing, spec.Source.Key, StringComparison.Ordinal))
                    throw new InvalidOperationException("Two source objects map to the same destination key.");
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
            await using var save = connection.CreateCommand();
            save.Transaction = (SqliteTransaction)transaction;
            save.CommandText = """
                INSERT INTO jobs (id, state, updated, payload)
                VALUES ($id, $state, $updated, $payload);
                """;
            save.Parameters.AddWithValue("$id", job.Id.ToString("N"));
            save.Parameters.AddWithValue("$state", job.State.ToString());
            save.Parameters.AddWithValue("$updated", job.UpdatedAt.UtcDateTime.ToString("o"));
            save.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job, Json));
            await save.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CrossBatchProgress> GetBatchProgressAsync(Guid batchId, CancellationToken cancellationToken)
    {
        CrossBatchProgress progress = new(0, 0, 0, 0, 0, 0);
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                SELECT COUNT(*),
                    SUM(CASE WHEN j.state = 'Completed' AND json_extract(j.payload, '$.Skipped') = 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN j.state = 'Completed' AND json_extract(j.payload, '$.Skipped') = 1 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN j.state = 'Failed' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN j.state = 'Cancelled' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN j.state IN ('Queued', 'Running', 'Paused', 'WaitingRetry') THEN 1 ELSE 0 END)
                FROM cross_batch_items AS i JOIN jobs AS j ON j.id = i.job_id
                WHERE i.batch_id = $id;
                """;
            command.Parameters.AddWithValue("$id", batchId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                progress = new CrossBatchProgress(reader.GetInt64(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt64(5));
        }, cancellationToken);
        return progress;
    }

    public async Task<IReadOnlyList<TransferJob>> LoadFailedBatchJobsAsync(Guid batchId,
        string? afterId, int limit, CancellationToken cancellationToken)
    {
        var jobs = new List<TransferJob>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                SELECT j.payload FROM cross_batch_items AS i JOIN jobs AS j ON j.id = i.job_id
                WHERE i.batch_id = $batch AND j.state = 'Failed' AND j.id > $after
                ORDER BY j.id LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$batch", batchId.ToString("N"));
            command.Parameters.AddWithValue("$after", afterId ?? "");
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 128));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var job = JsonSerializer.Deserialize<TransferJob>(reader.GetString(0), Json);
                if (job is not null)
                    jobs.Add(job);
            }
        }, cancellationToken);
        return jobs;
    }

    public async Task<IReadOnlyList<TransferJob>> LoadRecentAsync(int limit, CancellationToken cancellationToken)
    {
        var jobs = new List<TransferJob>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                SELECT payload FROM jobs
                ORDER BY updated DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var job = JsonSerializer.Deserialize<TransferJob>(reader.GetString(0), Json);
                if (job is not null)
                    jobs.Add(job);
            }
        }, cancellationToken);
        return jobs;
    }

    public async Task<IReadOnlyList<TransferJob>> LoadUnfinishedAsync(CancellationToken cancellationToken)
    {
        var jobs = new List<TransferJob>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                SELECT payload FROM jobs
                WHERE state IN ('Queued', 'Running', 'Paused', 'WaitingRetry')
                   OR json_extract(payload, '$.CrossTransfer.CleanupPending') = 1
                ORDER BY updated;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var job = JsonSerializer.Deserialize<TransferJob>(reader.GetString(0), Json);
                if (job is not null)
                    jobs.Add(job);
            }
        }, cancellationToken);
        return jobs;
    }

    public Task UpsertPartAsync(Guid jobId, TransferPart part, CancellationToken cancellationToken) =>
        ExecuteAsync(async command =>
        {
            command.CommandText = """
                INSERT INTO parts (job_id, number, offset, length, etag, sha256)
                VALUES ($id, $number, $offset, $length, $etag, $sha256)
                ON CONFLICT(job_id, number) DO UPDATE SET
                    offset = excluded.offset,
                    length = excluded.length,
                    etag = excluded.etag,
                    sha256 = excluded.sha256;
                """;
            command.Parameters.AddWithValue("$id", jobId.ToString("N"));
            command.Parameters.AddWithValue("$number", part.Number);
            command.Parameters.AddWithValue("$offset", part.Offset);
            command.Parameters.AddWithValue("$length", part.Length);
            command.Parameters.AddWithValue("$etag", (object?)part.ETag ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha256", (object?)part.Sha256 ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task ClearPartsAsync(Guid jobId, CancellationToken cancellationToken) =>
        ExecuteAsync(async command =>
        {
            command.CommandText = "DELETE FROM parts WHERE job_id = $id;";
            command.Parameters.AddWithValue("$id", jobId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyList<TransferPart>> GetPartsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var parts = new List<TransferPart>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                SELECT number, offset, length, etag, sha256
                FROM parts
                WHERE job_id = $id
                ORDER BY number;
                """;
            command.Parameters.AddWithValue("$id", jobId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                parts.Add(new TransferPart(
                    reader.GetInt32(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3))
                {
                    Sha256 = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
        }, cancellationToken);
        return parts;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        SqliteConnection.ClearAllPools();
        _gate.Dispose();
    }

    private async Task ExecuteAsync(Func<SqliteCommand, Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            await action(command);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
            return _connection;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var settings = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        var connection = new SqliteConnection(settings.ToString());
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                updated TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS jobs_state_updated ON jobs (state, updated);
            CREATE INDEX IF NOT EXISTS jobs_updated ON jobs (updated DESC);
            CREATE TABLE IF NOT EXISTS parts (
                job_id TEXT NOT NULL,
                number INTEGER NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                etag TEXT NULL,
                sha256 TEXT NULL,
                PRIMARY KEY (job_id, number)
            );
            CREATE TABLE IF NOT EXISTS cross_batches (
                id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                updated TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS cross_batches_state ON cross_batches (state, updated);
            CREATE TABLE IF NOT EXISTS cross_batch_items (
                batch_id TEXT NOT NULL,
                destination_key TEXT NOT NULL,
                source_key TEXT NOT NULL,
                job_id TEXT NOT NULL,
                PRIMARY KEY (batch_id, destination_key)
            );
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(parts);";
        var hasSha256 = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                hasSha256 |= string.Equals(reader.GetString(1), "sha256", StringComparison.Ordinal);
        }
        if (!hasSha256)
        {
            await using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE parts ADD COLUMN sha256 TEXT NULL;";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        _connection = connection;
        return connection;
    }
}
