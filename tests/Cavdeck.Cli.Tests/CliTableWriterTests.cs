using System.Text.Json;
using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class CliTableWriterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ConnectionTableUsesReadableColumnsAndPreservesUnicode()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            items = new[]
            {
                new
                {
                    id = Guid.Empty,
                    name = "中文备份",
                    provider = "CloudflareR2",
                    region = "auto",
                    isReadOnly = true,
                    restrictedBucket = "assets",
                    endpoint = "https://storage.example.test"
                }
            }
        }, Json);
        using var output = new StringWriter();

        Assert.True(CliTableWriter.TryWrite("connection.list", data, false, output));
        var text = output.ToString();
        Assert.Contains("NAME", text);
        Assert.Contains("PROVIDER", text);
        Assert.Contains("READ-ONLY", text);
        Assert.Contains("中文备份", text);
        Assert.Contains("Cloudflare R2", text);
        Assert.DoesNotContain("ENDPOINT", text);
        Assert.DoesNotContain("https://storage.example.test", text);
        Assert.Equal(4, CliTableWriter.DisplayWidth("中文"));
        Assert.Equal(3, CliTableWriter.DisplayWidth("abc"));
    }

    [Fact]
    public void WideTableShowsAdditionalColumnsAndFullValues()
    {
        var key = new string('a', 90) + "/报告.pdf";
        var data = JsonSerializer.SerializeToElement(new
        {
            items = new[]
            {
                new
                {
                    key,
                    type = "file",
                    sizeBytes = 2048L,
                    lastModified = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero),
                    etag = "fictional-etag"
                }
            }
        }, Json);
        using var compact = new StringWriter();
        using var wide = new StringWriter();

        Assert.True(CliTableWriter.TryWrite("object.list", data, false, compact));
        Assert.True(CliTableWriter.TryWrite("object.list", data, true, wide));

        Assert.Contains("…", compact.ToString());
        Assert.DoesNotContain(key, compact.ToString());
        Assert.DoesNotContain("fictional-etag", compact.ToString());
        Assert.Contains(key, wide.ToString());
        Assert.Contains("fictional-etag", wide.ToString());
        Assert.Contains("2 KiB", wide.ToString());
    }

    [Fact]
    public void TerminalControlCodesCannotEscapeTheTable()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            items = new[] { new { name = "bad\u001b[31m", region = "auto" } }
        }, Json);
        using var output = new StringWriter();

        Assert.True(CliTableWriter.TryWrite("bucket.list", data, false, output));
        Assert.DoesNotContain("\u001b", output.ToString());
        Assert.Contains("\\u001b", output.ToString());
    }
}
