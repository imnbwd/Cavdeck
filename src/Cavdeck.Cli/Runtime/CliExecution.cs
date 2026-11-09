using System.CommandLine;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static readonly Option<string> Connection = new("--connection", "-c")
    {
        Required = true, Description = CliText.Get("ConnectionSelector")
    };


    private static void Bind(Command command, string operation, Func<ParseResult, ServiceProvider, Task<object>> run,
        bool writableCatalog = false, Option<bool>? previewOption = null)
    {
        command.SetAction(async (p, _) =>
        {
            object data;
            var readOnlyCatalog = !writableCatalog || previewOption is not null && p.GetValue(previewOption);
            await using (var services = new ServiceCollection().AddCavdeckCore(readOnlyCatalog: readOnlyCatalog).BuildServiceProvider())
                data = await run(p, services);
            WriteSuccess(p, operation, data);
            return 0;
        });
    }


    private static Task<StorageConnection> ResolveAsync(IServiceProvider services, ParseResult p, CancellationToken token) =>
        ResolveAsync(services.GetRequiredService<ConnectionService>(), p.GetValue(Connection)!, token);

    internal static async Task<StorageConnection> ResolveAsync(ConnectionService service, string selector, CancellationToken token)
    {
        var items = await service.ListAsync(token);
        var found = Guid.TryParse(selector, out var id)
            ? items.Where(c => c.Id == id)
            : items.Where(c => string.Equals(c.Name, selector, StringComparison.OrdinalIgnoreCase));
        return found.Take(2).ToArray() switch
        {
            [var c] => c,
            [] => throw new CliException(new(3, "CONNECTION_NOT_FOUND", CliText.Get("ConnectionMissing"))),
            _ => throw new CliException(new(2, "AMBIGUOUS_CONNECTION", CliText.Get("AmbiguousConnection")))
        };
    }


    private static string EffectivePrefix(StorageConnection connection, StorageUri uri) =>
        uri.Key.Length == 0 ? connection.KeyPrefix ?? "" : uri.Key;

    private static bool ShowProgress(ParseResult p, Option<bool> quiet) =>
        !p.GetValue(quiet) && !IsJson(p) && !Console.IsErrorRedirected &&
        !Console.IsInputRedirected && !Console.IsOutputRedirected;
}
