using System.CommandLine;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{

    static partial void RegisterCommands(RootCommand root, Option<bool> quiet, CancellationToken token)
    {
        RegisterConnectionCommands(root, token);
        RegisterBucketCommands(root, token);

        var objects = new Command("object", CliText.Get("ObjectDescription"));
        objects.Subcommands.Add(ObjectList("list", token));
        objects.Subcommands.Add(ObjectStat("stat", token));
        root.Subcommands.Add(objects);
        root.Subcommands.Add(ObjectList("ls", token));
        root.Subcommands.Add(ObjectStat("stat", token));
        RegisterObjectManagement(root, objects, quiet, token);
        RegisterTransfers(root, quiet, token);
    }

    static partial void RegisterTransfers(RootCommand root, Option<bool> quiet, CancellationToken token);
}
