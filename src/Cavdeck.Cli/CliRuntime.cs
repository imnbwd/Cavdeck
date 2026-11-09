using System.CommandLine;
using System.Text.Json;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
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

            if (requestedFormat is not (null or "table" or "json") ||
                (parsed.GetValue(json) && requestedFormat == "table") || parsed.Errors.Count > 0)
                return WriteError(CliError.Arguments, asJson);

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
