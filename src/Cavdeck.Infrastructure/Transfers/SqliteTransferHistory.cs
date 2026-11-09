using System.Globalization;
using System.Text.Json;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Infrastructure.Transfers;

public sealed record TransferHistoryCount(TransferKind Kind, TransferState State, bool Skipped,
    int Count, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed partial class SqliteTransferJournal
{
    public async Task<IReadOnlyList<TransferHistoryCount>> ReadHistoryCountsAsync(CancellationToken token)
    {
        var counts = new List<TransferHistoryCount>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                SELECT CASE WHEN json_extract(payload, '$.CrossTransfer.Move') = 1 THEN 'Move'
                       ELSE json_extract(payload, '$.Kind') END AS kind,
                       state, COALESCE(json_extract(payload, '$.Skipped'), 0) AS skipped,
                       COUNT(*), MIN(json_extract(payload, '$.CreatedAt')), MAX(updated)
                FROM jobs GROUP BY kind, state, skipped;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                counts.Add(new TransferHistoryCount(
                    Enum.Parse<TransferKind>(reader.GetString(0)), Enum.Parse<TransferState>(reader.GetString(1)),
                    reader.GetBoolean(2), reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture).ToUniversalTime(),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture).ToUniversalTime()));
            }
        }, token);
        return counts;
    }

    public async Task<IReadOnlyList<TransferJob>> ReadRunJobsAsync(Guid? id, TransferKind? kind,
        TransferState? state, int limit, int offset, CancellationToken token)
    {
        var jobs = new List<TransferJob>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = """
                SELECT payload, id, state FROM jobs
                WHERE ($id IS NULL OR id = $id)
                  AND ($state IS NULL OR state = $state)
                  AND ($kind IS NULL OR $kind = CASE
                      WHEN json_extract(payload, '$.CrossTransfer.Move') = 1 THEN 'Move'
                      ELSE json_extract(payload, '$.Kind') END)
                ORDER BY updated DESC, id LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$id", (object?)id?.ToString("N") ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", (object?)state?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", (object?)kind?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var job = JsonSerializer.Deserialize<TransferJob>(reader.GetString(0), Json);
                if (job is null || job.Id.ToString("N") != reader.GetString(1) ||
                    !Enum.IsDefined(job.State) || job.State.ToString() != reader.GetString(2) ||
                    !Enum.IsDefined(job.Kind) || !Enum.IsDefined(job.Conflict))
                    throw new InvalidDataException("A transfer job has an invalid payload.");
                jobs.Add(job);
            }
        }, token);
        return jobs;
    }

    public async Task<IReadOnlyList<CrossTransferBatch>> ReadRunBatchesAsync(CancellationToken token)
    {
        var batches = new List<CrossTransferBatch>();
        await ExecuteAsync(async command =>
        {
            command.CommandText = "SELECT payload FROM cross_batches ORDER BY updated, id;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                batches.Add(JsonSerializer.Deserialize<CrossTransferBatch>(reader.GetString(0), Json)
                    ?? throw new InvalidDataException("A transfer batch is missing its payload."));
        }, token);
        return batches;
    }
}
