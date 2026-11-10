using System.CommandLine;
using System.Text.Json;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static bool IsJson(ParseResult parsed) =>
        parsed.GetValue<bool>("--json") || parsed.GetValue<string?>("--format") == "json";

    private static void WriteSuccess(ParseResult parsed, string operation, object data, IReadOnlyList<string>? warnings = null)
    {
        if (IsJson(parsed))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, operation, data, warnings = warnings ?? [] }, Json));
            return;
        }

        var document = JsonSerializer.SerializeToElement(data, Json);
        if (document.TryGetProperty("items", out var items))
        {
            if (operation == "transfer.show")
                foreach (var field in document.GetProperty("run").EnumerateObject())
                    Console.WriteLine($"{field.Name,-16} {Display(field.Value)}");
            if (!CliTableWriter.TryWrite(operation, document, parsed.GetValue<bool>("--wide"), Console.Out))
            {
                var rows = items.EnumerateArray().Select(item => item.EnumerateObject()
                    .Where(field => operation != "connection.list" || field.Name != "endpoint").ToArray()).ToArray();
                if (rows.Length == 0)
                    Console.WriteLine(CliText.Get("NoResults"));
                else
                {
                    var headers = rows[0].Select(field => field.Name.ToUpperInvariant()).ToArray();
                    var values = rows.Select(row => row.Select(field => Display(field.Value)).ToArray()).ToArray();
                    var widths = headers.Select((header, i) => Math.Max(header.Length, values.Max(row => row[i].Length))).ToArray();
                    Console.WriteLine(FormatRow(headers, widths));
                    foreach (var row in values)
                        Console.WriteLine(FormatRow(row, widths));
                }
            }
            if (document.TryGetProperty("nextPageToken", out var next) && next.ValueKind != JsonValueKind.Null)
                Console.WriteLine(CliText.Format("NextPage", Display(next)));
            if (operation == "object.search")
                Console.WriteLine(CliText.Format(document.GetProperty("complete").GetBoolean() ? "SearchComplete" : "SearchPartial",
                    document.GetProperty("scannedObjects").GetInt32(), document.GetProperty("requests").GetInt32()));
            if (document.TryGetProperty("nextOffset", out var offset) && offset.ValueKind != JsonValueKind.Null)
                Console.WriteLine(CliText.Format("NextOffset", offset.GetInt32()));
        }
        else if (operation == "connection.test")
            Console.WriteLine(CliText.Format("Reachable", Display(document.GetProperty("name"))));
        else
            foreach (var field in document.EnumerateObject())
                Console.WriteLine($"{field.Name,-16} {Display(field.Value)}");
        if (warnings is not null)
            foreach (var warning in warnings)
                Console.Error.WriteLine(Display(JsonSerializer.SerializeToElement(warning)));
    }

    private static string FormatRow(string[] values, int[] widths) =>
        string.Join("  ", values.Select((value, i) => value.PadRight(widths[i]))).TrimEnd();

    private static string Display(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return "-";
        var plain = value.ToString();
        // Remote names may contain terminal control characters.
        return string.Concat(plain.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? $"\\u{(int)c:x4}" : c.ToString()));
    }
}
