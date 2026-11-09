using System.CommandLine;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Sharing;
using Cavdeck.Domain.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static void RegisterObjectManagement(RootCommand root, Command objects, Option<bool> quiet,
        CancellationToken token)
    {
        foreach (var parent in new Command[] { root, objects })
        {
            parent.Subcommands.Add(ObjectSearch(quiet, token));
            parent.Subcommands.Add(ObjectMkdir(token));
            parent.Subcommands.Add(ObjectPresign(token));
        }
        objects.Subcommands.Add(ObjectCopy("copy", false, quiet, token));
        objects.Subcommands.Add(ObjectCopy("move", true, quiet, token));
        objects.Subcommands.Add(ObjectRemove("remove", token));
        root.Subcommands.Add(ObjectCopy("cp", false, quiet, token));
        root.Subcommands.Add(ObjectCopy("mv", true, quiet, token));
        root.Subcommands.Add(ObjectRemove("rm", token));
    }

    private static Command ObjectSearch(Option<bool> quiet, CancellationToken token)
    {
        var command = new Command("search", CliText.Get("ObjectSearch"));
        var query = new Argument<string>("query");
        var scope = new Option<string>("--in") { Required = true, Description = CliText.Get("PrefixArgument") };
        var match = new Option<string>("--match")
        {
            DefaultValueFactory = _ => "name", Description = CliText.Get("SearchMatch")
        };
        var page = new Option<string?>("--page-token") { Description = CliText.Get("Cursor") };
        command.Arguments.Add(query);
        foreach (var option in new Option[] { scope, match, page, Connection })
            command.Options.Add(option);

        Bind(command, "object.search", async (p, services) =>
        {
            var term = p.GetValue(query)!;
            var mode = p.GetValue(match)!;
            if (string.IsNullOrWhiteSpace(term) || mode is not ("name" or "key-prefix"))
                throw new CliException(CliError.Arguments);
            if (mode == "key-prefix")
                StorageUri.ValidateKey(term);
            var uri = StorageUri.Parse(p.GetValue(scope)!);
            var connection = await ResolveAsync(services, p, token);
            var prefix = EffectivePrefix(connection, uri);
            ObjectAccess.Ensure(connection, uri.Bucket, prefix, write: false);
            var identity = new SearchIdentity(connection.Id, uri.Bucket, prefix, term, mode);
            var checkpoint = SearchToken.Decode(p.GetValue(page), identity);
            var explorer = services.GetRequiredService<ExplorerService>();
            Action<PrefixSearchProgress>? progress = ShowProgress(p, quiet)
                ? value => Console.Error.WriteLine(CliText.Format("SearchProgress", value.ScannedObjects, value.Matches))
                : null;
            var result = mode == "name"
                ? await explorer.SearchPrefixAsync(connection, uri.Bucket, prefix, term, progress, token,
                    cursor: checkpoint?.Cursor, previousKeys: checkpoint?.PreviousKeys, exactScope: true)
                : await explorer.SearchKeyPrefixAsync(connection, uri.Bucket, prefix, term, progress, token,
                    cursor: checkpoint?.Cursor, previousKeys: checkpoint?.PreviousKeys, exactScope: true);
            var items = result.Matches.Select(item => new
            {
                uri.Bucket, item.Key, item.Name, type = item.Kind == ObjectEntryKind.Folder ? "prefix" : "file",
                item.SizeBytes, etag = item.ETag, lastModified = item.LastModified?.ToUniversalTime()
            }).ToArray();
            var nextPageToken = result.Cursor is null ? null : SearchToken.Encode(identity, result.Cursor,
                (checkpoint?.PreviousKeys ?? []).Concat(items.Select(item => item.Key)).Distinct(StringComparer.Ordinal));
            return new { items, result.ScannedObjects, result.Requests, result.Complete, nextPageToken };
        });
        return command;
    }

    private static Command ObjectMkdir(CancellationToken token)
    {
        var command = new Command("mkdir", CliText.Get("ObjectMkdir"));
        var path = AddObjectUri(command);
        Bind(command, "object.mkdir", async (p, services) =>
        {
            var uri = StorageUri.Parse(p.GetValue(path)!);
            uri.RequireObject();
            if (!uri.HasTrailingSlash)
                throw new CliException(CliError.Arguments);
            var connection = await ResolveAsync(services, p, token);
            await services.GetRequiredService<ExplorerService>().CreateFolderMarkerAsync(connection, uri.Bucket, uri.Key, token);
            return new { uri.Bucket, uri.Key, created = true };
        });
        return command;
    }

    private static Command ObjectPresign(CancellationToken token)
    {
        var command = new Command("presign", CliText.Get("ObjectPresign"));
        var path = AddObjectUri(command);
        var expires = new Option<int>("--expires-in")
        {
            DefaultValueFactory = _ => 3600, Description = CliText.Get("PresignLifetime")
        };
        command.Options.Add(expires);
        Bind(command, "object.presign", async (p, services) =>
        {
            var uri = StorageUri.Parse(p.GetValue(path)!);
            uri.RequireObject();
            var lifetime = TimeSpan.FromSeconds(p.GetValue(expires));
            if (!ShareLinks.ValidLifetime(lifetime))
                throw new CliException(CliError.Arguments);
            var connection = await ResolveAsync(services, p, token);
            ObjectAccess.Ensure(connection, uri.Bucket, uri.Key, write: false);
            if (!ConnectionPlanner.Capabilities(connection.Provider).CanPresignDownload)
                throw new CliException(new(9, "UNSUPPORTED_PRESIGN", CliText.Get("PresignUnsupported")));
            return await services.GetRequiredService<IDownloadLinkSigner>()
                .SignDownloadAsync(connection, uri.Bucket, uri.Key, lifetime, token);
        });
        return command;
    }

    private static Argument<string> AddObjectUri(Command command)
    {
        var path = new Argument<string>("uri") { Description = CliText.Get("UriArgument") };
        command.Arguments.Add(path);
        command.Options.Add(Connection);
        return path;
    }

}
