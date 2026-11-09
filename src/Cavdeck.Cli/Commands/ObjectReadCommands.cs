using System.CommandLine;
using Cavdeck.Application.Explorer;
using Cavdeck.Domain.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
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
}
