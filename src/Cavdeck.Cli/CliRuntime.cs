using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo EnglishUiCulture = CultureInfo.GetCultureInfo("en-US");

    internal static void UseEnglishUiCulture() => CultureInfo.CurrentUICulture = EnglishUiCulture;

    public static async Task<int> RunAsync(string[] args)
    {
        // An empty root or a resource group is a discovery request, not an error.
        if (args.Length == 0 || args.Length == 1 &&
            args[0] is "connection" or "bucket" or "object" or "transfer")
            args = [..args, "--help"];

        // System.CommandLine localizes built-in help independently of Cavdeck's .resx strings.
        UseEnglishUiCulture();
        var root = new RootCommand(CliText.Get("RootDescription"));
        var format = new Option<string?>("--format") { Recursive = true, Description = CliText.Get("FormatDescription") };
        var json = new Option<bool>("--json") { Recursive = true, Description = CliText.Get("JsonDescription") };
        var quiet = new Option<bool>("--quiet", "-q") { Recursive = true, Description = CliText.Get("QuietDescription") };
        var verbose = new Option<bool>("--verbose") { Recursive = true, Description = CliText.Get("VerboseDescription") };
        root.Options.Add(format);
        root.Options.Add(json);
        root.Options.Add(quiet);
        root.Options.Add(verbose);
        root.Options.Add(new Option<bool>("--no-color") { Recursive = true, Description = CliText.Get("NoColorDescription") });
        root.Options.Add(new Option<bool>("--wide") { Recursive = true, Description = CliText.Get("WideDescription") });
        root.Options.Add(new Option<bool>("--no-input") { Recursive = true, Description = CliText.Get("NoInputDescription") });

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; _ = cancellation.CancelAsync(); };
        Console.CancelKeyPress += cancel;
        try
        {
            RegisterCommands(root, quiet, cancellation.Token);
            var parsed = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
            var requestedFormat = parsed.GetResult(format)?.Tokens.FirstOrDefault()?.Value;
            var asJson = parsed.GetValue(json) || requestedFormat == "json";

            if (requestedFormat is not (null or "table" or "json"))
                return WriteError(CliError.Arguments with { Message = CliText.Get("InvalidOutputFormat") }, asJson);
            if (parsed.GetValue(json) && requestedFormat == "table")
                return WriteError(CliError.Arguments with { Message = CliText.Get("ConflictingOutputFormat") }, asJson);
            if (parsed.Errors.Count > 0)
                return WriteError(CliError.Arguments with { Message = SafeParseError(parsed.Errors[0].Message) }, asJson);

            try
            {
                if (parsed.Action is not null)
                    return await parsed.InvokeAsync(new InvocationConfiguration
                    {
                        EnableDefaultExceptionHandler = false,
                        // The console handler waits for durable shutdown and cleanup.
                        ProcessTerminationTimeout = null
                    }, cancellation.Token);
                return WriteError(CliError.Arguments, asJson);
            }
            catch (Exception exception)
            {
                return WriteError(CliError.From(exception, cancellation.Token), asJson,
                    parsed.GetValue(verbose) ? exception.GetType().Name : null);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    // Parse messages may include arbitrary user-supplied arguments (including secrets).
    // Report only option tokens and static text, never raw parser error messages.
    private static string SafeParseError(string message)
    {
        var match = Regex.Match(message, @"--[a-zA-Z][a-zA-Z0-9-]*", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            if (message.Contains("required", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("missing", StringComparison.OrdinalIgnoreCase))
                return CliText.Format("MissingOption", match.Value);
            if (message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unknown", StringComparison.OrdinalIgnoreCase))
                return CliText.Format("UnknownOption", match.Value);
            return CliText.Format("InvalidOption", match.Value);
        }
        if (message.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("missing", StringComparison.OrdinalIgnoreCase))
            return CliText.Get("MissingArgument");
        return CliText.Get("InvalidSyntax");
    }

    static partial void RegisterCommands(RootCommand root, Option<bool> quiet, CancellationToken token);

    private static int WriteError(CliError error, bool json, string? diagnostic = null)
    {
        if (json)
            Console.Error.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1,
                error = new { error.Code, error.Message, error.Retryable, error.Details }, diagnostic }, Json));
        else
        {
            Console.Error.WriteLine(CliText.Format("ErrorLine", error.Code, error.Message));
            if (error.Details is not null)
                Console.Error.WriteLine(JsonSerializer.Serialize(error.Details, Json));
            if (diagnostic is not null)
                Console.Error.WriteLine(diagnostic);
        }
        return error.ExitCode;
    }
}
