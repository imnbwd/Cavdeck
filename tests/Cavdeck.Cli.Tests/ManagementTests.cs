using System.Text.Json;
using Cavdeck.Application.Explorer;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.Transfers;
using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class ManagementTests : IDisposable
{
    private readonly string _profile = Directory.CreateTempSubdirectory("cavdeck-management-").FullName;

    [Fact]
    public void SearchCheckpointBindsAllQueryFieldsAndPreservesCursor()
    {
        var identity = new SearchIdentity(Guid.NewGuid(), "assets", "scope/", "Logo", "name");
        var cursor = new PrefixSearchCursor("provider+cursor/", 57);
        var encoded = SearchToken.Encode(identity, cursor, ["scope/Logo/", "scope/Logo.png"]);
        var decoded = Assert.IsType<SearchToken>(SearchToken.Decode(encoded, identity));
        Assert.Equal(cursor, decoded.Cursor);
        Assert.Equal(["scope/Logo/", "scope/Logo.png"], decoded.PreviousKeys);
        foreach (var other in new[] { identity with { ConnectionId = Guid.NewGuid() }, identity with { Bucket = "other" },
            identity with { Prefix = "other/" }, identity with { Query = "logo" }, identity with { MatchMode = "key-prefix" } })
            Assert.Equal("INVALID_SEARCH_TOKEN", Assert.Throws<CliException>(() => SearchToken.Decode(encoded, other)).Error.Code);
        Assert.Throws<CliException>(() => SearchToken.Decode(new string('x', 25_000), identity));
        Assert.Throws<CliException>(() => SearchToken.Decode("local.../../../x", identity));
        Assert.Throws<CliException>(() => SearchToken.Decode("invalid", identity));
    }

    [Fact]
    public void LargeSearchCheckpointFitsOnWindowsCommandLine()
    {
        var identity = new SearchIdentity(Guid.NewGuid(), "assets", "", "a", "name");
        var keys = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid().ToString("N") + "/").ToArray();
        var token = SearchToken.Encode(identity, new("next", 0), keys, _profile);
        Assert.StartsWith("local.", token, StringComparison.Ordinal);
        Assert.Equal(keys, SearchToken.Decode(token, identity, _profile)!.PreviousKeys);
    }

    [Fact]
    public async Task HistoryReadsLegacyAndActiveRunsWithoutChangingTheJournal()
    {
        var run = Guid.NewGuid();
        var activeRun = Guid.NewGuid();
        var job = TransferJob.Create(TransferKind.Upload, Guid.NewGuid(), "assets", "object",
            @"C:\fictional\source", 1, TransferConflictPolicy.Fail) with { State = TransferState.Completed };
        var path = await WriteJobAsync(run, job);
        await WriteJobAsync(run, job with { Id = Guid.NewGuid(), State = TransferState.Failed });
        var activePath = await WriteJobAsync(activeRun, job with { Id = Guid.NewGuid(), State = TransferState.Running });
        using var active = new FileStream(Path.Combine(Path.GetDirectoryName(activePath)!, "run.lock"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var original = await File.ReadAllBytesAsync(path);

        var list = await RunAsync("transfer", "list", "--json");
        Assert.Equal(0, list.ExitCode);
        using (var output = JsonDocument.Parse(list.Output))
        {
            var items = output.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.True(items.Single(item => item.GetProperty("runId").GetGuid() == activeRun).GetProperty("isActive").GetBoolean());
            Assert.True(items.Single(item => item.GetProperty("runId").GetGuid() == run).GetProperty("legacy").GetBoolean());
        }
        var shown = await RunAsync("transfer", "show", run.ToString(), "--limit", "1", "--json");
        Assert.Equal(0, shown.ExitCode);
        using (var output = JsonDocument.Parse(shown.Output))
        {
            var data = output.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, data.GetProperty("nextOffset").GetInt32());
        }
        Assert.Equal(0, (await RunAsync("transfer", "show", run.ToString(), "--job", job.Id.ToString(), "--json")).ExitCode);
        await AssertErrorAsync("JOB_NOT_FOUND", "transfer", "show", run.ToString(), "--job", Guid.NewGuid().ToString(), "--json");
        await AssertErrorAsync("RUN_NOT_FOUND", "transfer", "show", Guid.NewGuid().ToString(), "--json");
        await AssertErrorAsync("RUN_IN_PROGRESS", "transfer", "resume", activeRun.ToString(), "--json");
        await AssertErrorAsync("JOB_NOT_RESUMABLE", "transfer", "resume", run.ToString(), "--json");
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InvalidJournalReportsAnExplicitErrorAndListContinues()
    {
        var id = Guid.NewGuid();
        var path = JournalPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "invalid");
        await AssertErrorAsync("RUN_JOURNAL_CORRUPTED", "transfer", "show", id.ToString(), "--json");
        var list = await RunAsync("transfer", "list", "--json");
        using var output = JsonDocument.Parse(list.Output);
        Assert.Single(output.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task NoInputReturnsAnActionableErrorWithoutEchoingCredentials()
    {
        var result = await RunAsync("connection", "add", "--name", "fictional", "--provider", "aws-s3",
            "--region", "us-east-1", "--access-key-id", "fictional-access-key", "--no-input", "--json");
        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("fictional-access-key", result.Error);
        using var output = JsonDocument.Parse(result.Error);
        Assert.Equal("CREDENTIALS_REQUIRED", output.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task AssertErrorAsync(string code, params string[] args)
    {
        var result = await RunAsync(args);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        using var output = JsonDocument.Parse(result.Error);
        Assert.Equal(code, output.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<string> WriteJobAsync(Guid id, TransferJob job)
    {
        var path = JournalPath(id);
        using var journal = new SqliteTransferJournal(path);
        await journal.UpsertAsync(job, CancellationToken.None);
        return path;
    }

    private string JournalPath(Guid id) =>
        Path.Combine(_profile, "AppData", "Local", "CavdeckData", "cli", "runs", id.ToString("N"), "transfers.db");

    private Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args) =>
        CommandProcessTests.InvokeInProfileAsync(_profile, args);

    public void Dispose() => Directory.Delete(_profile, recursive: true);
}
