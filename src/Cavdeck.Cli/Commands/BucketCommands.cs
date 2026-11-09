using System.CommandLine;
using Cavdeck.Application.Explorer;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{

    private static void RegisterBucketCommands(RootCommand root, CancellationToken token)
    {
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

    }
}
