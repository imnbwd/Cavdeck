using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cavdeck.Cli;

internal static class CliTableWriter
{
    private sealed record Column(string Header, string Property, int MaxWidth, bool WideOnly = false, bool RightAlign = false);

    private static readonly IReadOnlyDictionary<string, Column[]> Layouts = new Dictionary<string, Column[]>(StringComparer.Ordinal)
    {
        ["connection.list"] =
        [
            new("NAME", "name", 32),
            new("PROVIDER", "provider", 16),
            new("REGION", "region", 18),
            new("READ-ONLY", "isReadOnly", 10),
            new("ID", "id", 36, WideOnly: true),
            new("BUCKET", "restrictedBucket", 36, WideOnly: true),
            new("ENDPOINT", "endpoint", 64, WideOnly: true)
        ],
        ["bucket.list"] =
        [
            new("NAME", "name", 48),
            new("REGION", "region", 20)
        ],
        ["object.list"] =
        [
            new("KEY", "key", 52),
            new("TYPE", "type", 16),
            new("SIZE", "sizeBytes", 12, RightAlign: true),
            new("MODIFIED (UTC)", "lastModified", 20),
            new("ETAG", "etag", 56, WideOnly: true)
        ],
        ["object.search"] =
        [
            new("KEY", "key", 52),
            new("TYPE", "type", 16),
            new("SIZE", "sizeBytes", 12, RightAlign: true),
            new("MODIFIED (UTC)", "lastModified", 20),
            new("ETAG", "etag", 56, WideOnly: true)
        ],
        ["transfer.list"] =
        [
            new("RUN ID", "runId", 36),
            new("OPERATION", "operation", 12),
            new("STATUS", "status", 14),
            new("TOTAL", "total", 8, RightAlign: true),
            new("FAILED", "failed", 8, RightAlign: true),
            new("UPDATED (UTC)", "updatedAt", 20),
            new("COMPLETED", "completed", 9, WideOnly: true, RightAlign: true),
            new("SKIPPED", "skipped", 8, WideOnly: true, RightAlign: true)
        ],
        ["transfer.show"] =
        [
            new("JOB ID", "jobId", 36),
            new("KIND", "kind", 12),
            new("STATE", "state", 16),
            new("KEY", "key", 48),
            new("PROGRESS", "progress", 10, RightAlign: true),
            new("LOCAL PATH", "localPath", 64, WideOnly: true),
            new("BYTES", "bytesTransferred", 12, WideOnly: true, RightAlign: true)
        ]
    };

    internal static bool TryWrite(string operation, JsonElement document, bool wide, TextWriter output)
    {
        if (!Layouts.TryGetValue(operation, out var layout) ||
            !document.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return false;

        var rows = items.EnumerateArray().ToArray();
        var columns = layout.Where(column => wide || !column.WideOnly).ToArray();
        if (rows.Length == 0)
        {
            output.WriteLine(CliText.Get("NoResults"));
            return true;
        }

        var values = rows.Select(row => columns.Select(column => Cell(row, column)).ToArray()).ToArray();
        var widths = columns.Select((column, index) =>
            Math.Max(DisplayWidth(column.Header), values.Max(row => DisplayWidth(
                wide ? row[index] : Clip(row[index], column.MaxWidth))))).ToArray();

        output.WriteLine(Row(columns.Select(column => column.Header).ToArray(), widths,
            columns.Select(column => column.RightAlign).ToArray()));
        foreach (var valuesRow in values)
        {
            var cells = columns.Select((column, index) =>
                wide ? valuesRow[index] : Clip(valuesRow[index], column.MaxWidth)).ToArray();
            output.WriteLine(Row(cells, widths, columns.Select(column => column.RightAlign).ToArray()));
        }
        return true;
    }

    private static string Row(string[] cells, int[] widths, bool[] alignRight) =>
        string.Join("  ", cells.Select((cell, i) =>
        {
            var gap = new string(' ', widths[i] - DisplayWidth(cell));
            return alignRight[i] ? gap + cell : cell + gap;
        })).TrimEnd();

    private static string Cell(JsonElement row, Column column)
    {
        if (!row.TryGetProperty(column.Property, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "-";

        if ((column.Property is "sizeBytes" or "bytesTransferred") &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes))
            return FormatSize(bytes);

        if ((column.Property is "updatedAt" or "lastModified") &&
            value.ValueKind == JsonValueKind.String &&
            value.TryGetDateTimeOffset(out var timestamp))
            return timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        if (value.ValueKind == JsonValueKind.True) return "Yes";
        if (value.ValueKind == JsonValueKind.False) return "No";
        if (column.Property == "provider")
            return value.GetString() switch
            {
                "AwsS3" => "AWS S3",
                "CloudflareR2" => "Cloudflare R2",
                "Minio" => "MinIO",
                "S3Compatible" => "S3-compatible",
                _ => Sanitize(value.ToString())
            };
        if (column.Property == "progress" && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var percentage))
            return percentage.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        return Sanitize(value.ToString());
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        var size = (double)bytes;
        var unit = 0;
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return size.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                builder.Append(@"\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
            else
                builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static string Clip(string value, int maxWidth)
    {
        if (DisplayWidth(value) <= maxWidth) return value;
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var width = RuneWidth(rune);
            if (used + width > maxWidth - 1) break;
            builder.Append(rune.ToString());
            used += width;
        }
        return builder.Append('…').ToString();
    }

    internal static int DisplayWidth(string text) => text.EnumerateRunes().Sum(RuneWidth);

    private static int RuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;
        var code = rune.Value;
        return code is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or
            >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff or
            >= 0xfe10 and <= 0xfe6f or >= 0xff01 and <= 0xff60 or
            >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff or
            >= 0x20000 and <= 0x3fffd ? 2 : 1;
    }
}
