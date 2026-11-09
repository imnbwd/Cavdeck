using System.CommandLine;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Explorer;
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

    static partial void RegisterCommands(RootCommand root, Option<bool> quiet, CancellationToken token)
    {
        var connection = new Command("connection", CliText.Get("ConnectionDescription"));
        var list = new Command("list", CliText.Get("ConnectionList"));
        Bind(list, "connection.list", async (p, services) =>
        {
            var items = await services.GetRequiredService<ConnectionService>().ListAsync(token);
            return new { items = items.Select(c => new
            {
                c.Id, c.Name, provider = c.Provider.ToString(), endpoint = SafeEndpoint(c.Endpoint),
                c.Region, c.IsReadOnly, c.RestrictedBucket
            }).ToArray() };
        });
        connection.Subcommands.Add(list);

        foreach (var test in new[] { false, true })
        {
            var command = new Command(test ? "test" : "show", test ? CliText.Get("ConnectionTest") : CliText.Get("ConnectionShow"));
            var selector = new Argument<string>("connection") { Description = CliText.Get("ConnectionSelector") };
            command.Arguments.Add(selector);
            Bind(command, test ? "connection.test" : "connection.show", async (p, services) =>
            {
                var service = services.GetRequiredService<ConnectionService>();
                var c = await ResolveAsync(service, p.GetValue(selector)!, token);
                if (test)
                {
                    var result = await service.TestStoredAsync(c.Id, token);
                    if (!result.Succeeded)
                        throw new CliException(result.Probe?.Failure is { } failure
                            ? CliError.FromStorage(failure) : CliError.Arguments);
                    return new { c.Id, c.Name, reachable = true };
                }

                return new { c.Id, c.Name, provider = c.Provider.ToString(), endpoint = SafeEndpoint(c.Endpoint),
                    c.Region, c.IsReadOnly, c.RestrictedBucket, c.KeyPrefix, c.ForcePathStyle };
            });
            connection.Subcommands.Add(command);
        }
        root.Subcommands.Add(connection);

        var bucket = new Command("bucket", CliText.Get("BucketDescription"));
        var buckets = new Command("list", CliText.Get("BucketList"));
        buckets.Options.Add(Connection);
        Bind(buckets, "bucket.list", async (p, services) =>
        {
            var c = await ResolveAsync(services, p, token);
            var items = await services.GetRequiredService<ExplorerService>().ListBucketsAsync(c, token);
            return new { items = items.Select(b => new { b.Name, b.Region }).ToArray() };
        });
        bucket.Subcommands.Add(buckets);
        root.Subcommands.Add(bucket);

        var objects = new Command("object", CliText.Get("ObjectDescription"));
        objects.Subcommands.Add(ObjectList("list", token));
        objects.Subcommands.Add(ObjectStat("stat", token));
        root.Subcommands.Add(objects);
        root.Subcommands.Add(ObjectList("ls", token));
        root.Subcommands.Add(ObjectStat("stat", token));
        RegisterTransfers(root, quiet, token);
    }

    static partial void RegisterTransfers(RootCommand root, Option<bool> quiet, CancellationToken token);

    private static Command ObjectList(string name, CancellationToken token)
    {
        var command = new Command(name, CliText.Get("ObjectList"));
        var path = new Argument<string>("uri") { Description = CliText.Get("PrefixArgument") };
        var recursive = new Option<bool>("--recursive") { Description = CliText.Get("RecursiveList") };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200, Description = CliText.Get("Limit") };
        var cursor = new Option<string?>("--page-token") { Description = CliText.Get("Cursor") };
        command.Arguments.Add(path);
        command.Options.Add(Connection);
        command.Options.Add(recursive);
        command.Options.Add(limit);
        command.Options.Add(cursor);
        Bind(command, "object.list", async (p, services) =>
        {
            var uri = StorageUri.Parse(p.GetValue(path)!);
            var size = p.GetValue(limit);
            if (size is < 1 or > 1000)
                throw new CliException(CliError.Arguments);
            var c = await ResolveAsync(services, p, token);
            var page = await services.GetRequiredService<ExplorerService>().ListExactPageAsync(c, uri.Bucket,
                uri.Key, p.GetValue(recursive), p.GetValue(cursor), size, token);
            if (page.CommonPrefixes.Count + page.Objects.Count > size)
                throw new CliException(new(1, "INVALID_STORAGE_PAGE", CliText.Get("PageSizeExceeded")));

            var items = page.CommonPrefixes.Select(key => new
                { uri.Bucket, key, type = "prefix", sizeBytes = (long?)null, etag = (string?)null, lastModified = (DateTimeOffset?)null })
                .Concat(page.Objects.Select(o => new
                { uri.Bucket, key = o.Key, type = o.Key.EndsWith('/') && o.SizeBytes == 0 ? "directoryMarker" : "file",
                    sizeBytes = (long?)o.SizeBytes, etag = o.ETag, lastModified = (DateTimeOffset?)o.LastModified.ToUniversalTime() }))
                .ToArray();
            return new { items, nextPageToken = page.ContinuationToken };
        });
        return command;
    }

    private static Command ObjectStat(string name, CancellationToken token)
    {
        var command = new Command(name, CliText.Get("ObjectStat"));
        var path = new Argument<string>("uri") { Description = CliText.Get("UriArgument") };
        command.Arguments.Add(path);
        command.Options.Add(Connection);
        Bind(command, "object.stat", async (p, services) =>
        {
            var uri = StorageUri.Parse(p.GetValue(path)!);
            uri.RequireObject();
            var c = await ResolveAsync(services, p, token);
            var head = await services.GetRequiredService<ExplorerService>().HeadAsync(c, uri.Bucket, uri.Key, token);
            return new { head.Bucket, head.Key, head.SizeBytes, etag = head.ETag, head.ContentType };
        });
        return command;
    }

    private static void Bind(Command command, string operation, Func<ParseResult, ServiceProvider, Task<object>> run)
    {
        command.SetAction(async (p, _) =>
        {
            object data;
            await using (var services = new ServiceCollection().AddCavdeckCore(readOnlyCatalog: true).BuildServiceProvider())
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

    private static string SafeEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : "";
}
