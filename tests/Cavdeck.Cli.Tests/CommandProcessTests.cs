using System.Diagnostics;
using System.Globalization;
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
    [InlineData("--help")]
    [InlineData("connection", "--help")]
    [InlineData("transfer", "upload", "--help")]
    public async Task HelpUsesEnglishForBuiltInAndCommandDescriptions(params string[] arguments)
    {
        var result = await InvokeAsync(arguments);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("Usage: cavdeck-cli", result.Output);
        Assert.Contains("Options:", result.Output);
        Assert.Contains("Show help and usage information", result.Output);
        Assert.DoesNotContain("用法:", result.Output);
        Assert.DoesNotContain("选项:", result.Output);
        if (arguments.Length == 1)
        {
            Assert.Contains("Access S3-compatible storage using saved Cavdeck connections.", result.Output);
            Assert.Contains("Show version information", result.Output);
        }
    }

    [Fact]
    public void EnglishUiCultureOverridesChineseWithoutChangingFormattingCulture()
    {
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            CliRuntime.UseEnglishUiCulture();

            Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("ls", "s3://bucket/", "--json")]
    [InlineData("download", "s3://bucket/key", "-c", "missing", "--to-file", "x", "--to-dir", "y", "--json")]
    [InlineData("transfer", "download", "s3://bucket/key", "-c", "missing", "--recursive", "--to-dir", "y", "--json")]
    [InlineData("upload", "a 文件.txt", "s3://bucket/key", "-c", "missing", "--on-conflict", "bogus", "--json")]
    [InlineData("bucket", "list", "--unknown", "--json")]
    [InlineData("connection", "list", "--json", "--format", "table")]
    [InlineData("object", "list", "s3://bucket/", "-c", "missing", "--limit", "1001", "--json")]
    [InlineData("object", "search", "logo", "-c", "missing", "--json")]
    [InlineData("search", "logo", "--in", "s3://bucket/", "-c", "missing", "--match", "unknown", "--json")]
    [InlineData("mkdir", "s3://bucket/key", "-c", "missing", "--json")]
    [InlineData("mkdir", "s3://bucket/", "-c", "missing", "--json")]
    [InlineData("presign", "s3://bucket/key", "-c", "missing", "--expires-in", "59", "--json")]
    [InlineData("presign", "s3://bucket/key", "-c", "missing", "--expires-in", "604801", "--json")]
    [InlineData("cp", "s3://bucket/a", "s3://bucket/b", "-c", "missing", "--source-connection", "other", "--json")]
    [InlineData("cp", "s3://bucket/a", "s3://bucket/b", "-c", "missing", "--on-conflict", "overwrite", "--json")]
    [InlineData("cp", "s3://bucket/prefix/", "s3://bucket/b", "-c", "missing", "--recursive", "--json")]
    [InlineData("mv", "s3://bucket/a", "s3://bucket/b", "-c", "missing", "--json")]
    [InlineData("rm", "s3://bucket/a", "-c", "missing", "--json")]
    [InlineData("rm", "s3://bucket/a/", "-c", "missing", "--yes", "--json")]
    [InlineData("rm", "s3://bucket/a", "-c", "missing", "--dry-run", "--yes", "--json")]
    [InlineData("rm", "s3://bucket/", "-c", "missing", "--yes", "--recursive", "--json")]
    [InlineData("rm", "s3://bucket/a", "-c", "missing", "--yes", "--max-objects", "100001", "--json")]
    [InlineData("connection", "add", "--name", "test", "--provider", "minio", "--secret", "fictional-value", "--json")]
    [InlineData("connection", "update", "missing", "--read-only", "--writable", "--json")]
    [InlineData("connection", "remove", "missing", "--json")]
    [InlineData("transfer", "list", "--limit", "501", "--json")]
    [InlineData("transfer", "show", "../invalid", "--json")]
    public async Task InvalidArgumentsAreStructured(params string[] arguments)
    {
        var result = await InvokeAsync(arguments);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        using var json = JsonDocument.Parse(result.Error);
        Assert.Equal("INVALID_ARGUMENTS", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingConnectionReportsTheOptionWithoutChangingErrorContract()
    {
        var result = await InvokeAsync("bucket", "list", "--json");
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        using var json = JsonDocument.Parse(result.Error);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("INVALID_ARGUMENTS", error.GetProperty("code").GetString());
        Assert.Contains("--connection", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvalidOptionsDoNotReflectUntrustedValues()
    {
        const string untrustedValue = "fictional-private-key-12345";
        var result = await InvokeAsync("bucket", "list", "--unknown-flag", untrustedValue, "--json");
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.DoesNotContain(untrustedValue, result.Error);
        using var json = JsonDocument.Parse(result.Error);
        Assert.Equal("INVALID_ARGUMENTS", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidOutputFormatExplainsAllowedValues()
    {
        var result = await InvokeAsync("connection", "list", "--format", "xml", "--json");
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("table or json", result.Error);
    }

    [Fact]
    public async Task WideOptionDoesNotChangeJsonOutput()
    {
        var regular = await InvokeAsync("connection", "list", "--json", "--no-input");
        var wide = await InvokeAsync("connection", "list", "--json", "--wide", "--no-input");
        Assert.Equal(regular, wide);
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

    internal static async Task<(int ExitCode, string Output, string Error)> InvokeInProfileAsync(
        string? profile, string[] arguments)
    {
        var cliAssembly = typeof(CliRuntime).Assembly.Location;
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(cliAssembly)!, "cavdeck-cli.exe"))
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
            start.Environment["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
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
