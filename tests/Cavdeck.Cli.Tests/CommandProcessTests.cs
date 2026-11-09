using System.Diagnostics;
using System.Text.Json;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.Connections;
using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class CommandProcessTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    public async Task BuiltInOptionsSucceed(string option)
    {
        var result = await InvokeAsync(option);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("ls", "s3://bucket/", "--json")]
    [InlineData("download", "s3://bucket/key", "-c", "missing", "--to-file", "x", "--to-dir", "y", "--json")]
    [InlineData("transfer", "download", "s3://bucket/key", "-c", "missing", "--recursive", "--to-dir", "y", "--json")]
    [InlineData("upload", "a 文件.txt", "s3://bucket/key", "-c", "missing", "--on-conflict", "bogus", "--json")]
    [InlineData("bucket", "list", "--unknown", "--json")]
    [InlineData("connection", "list", "--json", "--format", "table")]
    [InlineData("object", "list", "s3://bucket/", "-c", "missing", "--limit", "1001", "--json")]
    public async Task InvalidArgumentsAreStructured(params string[] arguments)
    {
        var result = await InvokeAsync(arguments);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        using var json = JsonDocument.Parse(result.Error);
        Assert.Equal("INVALID_ARGUMENTS", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AliasesUseSameValidation()
    {
        var full = await InvokeAsync("object", "list", "s3:///", "-c", "missing", "--recursive", "--format", "json");
        var alias = await InvokeAsync("ls", "s3:///", "-c", "missing", "--recursive", "--json");
        Assert.Equal(full, alias);
        Assert.Equal(2, alias.ExitCode);
    }

    [Fact]
    public async Task ConnectionListWritesOneJsonDocumentWithoutCredentialFields()
    {
        var profile = Directory.CreateTempSubdirectory("cavdeck-cli-profile-").FullName;
        try
        {
            var catalog = new JsonConnectionCatalog(Path.Combine(profile, "AppData", "Roaming",
                "Cavdeck", "connections.json"));
            var saved = new StorageConnection(Guid.NewGuid(), "文件 连接", StorageProviderKind.Minio,
                "https://fictional-user:fictional-secret@storage.example.test/?X-Amz-Signature=fictional-signature",
                "us-east-1", "fictional-reference");
            await catalog.UpsertAsync(saved);
            var result = await InvokeInProfileAsync(profile, ["connection", "list", "--json", "--quiet", "--no-input"]);
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Error);
            Assert.DoesNotContain("fictional-", result.Output);
            using var document = JsonDocument.Parse(result.Output);
            var item = Assert.Single(document.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
            Assert.Equal("文件 连接", item.GetProperty("name").GetString());
            Assert.Equal("https://storage.example.test", item.GetProperty("endpoint").GetString());
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    internal static Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] arguments) =>
        InvokeInProfileAsync(null, arguments);

    private static async Task<(int ExitCode, string Output, string Error)> InvokeInProfileAsync(
        string? profile, string[] arguments)
    {
        var cliAssembly = typeof(CliRuntime).Assembly.Location;
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(cliAssembly)!, "cavdeck.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (profile is not null)
        {
            start.Environment["USERPROFILE"] = profile;
            start.Environment["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
        }
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await stdout, await stderr);
    }
}
