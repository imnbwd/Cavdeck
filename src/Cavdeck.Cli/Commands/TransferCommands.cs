using System.CommandLine;
using System.Runtime.CompilerServices;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    static partial void RegisterTransfers(RootCommand root, Option<bool> quiet, CancellationToken token)
    {
        var transfer = new Command("transfer", CliText.Get("TransferDescription"));
        foreach (var parent in new Command[] { root, transfer })
        {
            parent.Subcommands.Add(TransferCommand(upload: true, quiet, token));
            parent.Subcommands.Add(TransferCommand(upload: false, quiet, token));
        }
        RegisterTransferHistory(transfer, quiet, token);
        root.Subcommands.Add(transfer);
    }

    private static Command TransferCommand(bool upload, Option<bool> quiet, CancellationToken token)
    {
        var name = upload ? "upload" : "download";
        var command = new Command(name, upload ? CliText.Get("UploadDescription") : CliText.Get("DownloadDescription"));
        var uriArgument = new Argument<string>("uri") { Description = CliText.Get("UriArgument") };
        var sourceArgument = new Argument<string>("source") { Description = CliText.Get("SourceArgument") };
        var toFile = new Option<string?>("--to-file") { Description = CliText.Get("ToFile") };
        var toDir = new Option<string?>("--to-dir") { Description = CliText.Get("ToDirectory") };
        var recursive = new Option<bool>("--recursive") { Description = CliText.Get("RecursiveTransfer") };
        var policy = new Option<string>("--on-conflict")
        {
            DefaultValueFactory = _ => "fail", Description = CliText.Get("ConflictPolicy")
        };

        if (upload)
            command.Arguments.Add(sourceArgument);
        command.Arguments.Add(uriArgument);
        command.Options.Add(Connection);
        command.Options.Add(recursive);
        command.Options.Add(policy);
        if (!upload)
        {
            command.Options.Add(toFile);
            command.Options.Add(toDir);
        }

        command.SetAction(async (p, _) =>
        {
            var uri = StorageUri.Parse(p.GetValue(uriArgument)!);
            var recurse = p.GetValue(recursive);
            var conflict = ParseConflict(p.GetValue(policy));
            var file = upload ? null : p.GetValue(toFile);
            var directory = upload ? null : p.GetValue(toDir);
            if (!upload && ((file is null) == (directory is null) || recurse && (file is not null || !uri.HasTrailingSlash)))
                throw new CliException(CliError.Arguments);
            if (!upload && !recurse)
                uri.RequireObject();

            var runId = Guid.NewGuid();
            var journal = RunFiles.JournalPath(runId);
            TransferRunResult result;
            await using (var services = CreateRunServices(journal))
            {
                var connection = await ResolveAsync(services, p, token);
                var prefix = uri.Key.Length == 0 ? connection.KeyPrefix ?? "" : uri.Key;
                ObjectAccess.Ensure(connection, uri.Bucket, prefix, write: upload);
                StorageUri.ValidateKey(prefix);
                Func<TransferEngine, CancellationToken, Task> submit;
                if (upload)
                {
                    var source = ExactLocalPaths.FullPath(p.GetValue(sourceArgument)!);
                    var isDirectory = (File.GetAttributes(source) & FileAttributes.Directory) != 0;
                    if (isDirectory && (!recurse || !uri.HasTrailingSlash))
                        throw new CliException(CliError.Arguments);
                    if (uri.Key.Length == 0 && !uri.HasTrailingSlash)
                        throw new CliException(CliError.Arguments);
                    if (uri.HasTrailingSlash && prefix.Length > 0 && !prefix.EndsWith('/'))
                        throw new CliException(CliError.Arguments);

                    var key = isDirectory ? prefix : uri.HasTrailingSlash ? prefix + Path.GetFileName(source) : uri.Key;
                    StorageUri.ValidateKey(key);
                    submit = (engine, ct) => engine.EnqueueExactUploadsAsync(connection, uri.Bucket,
                        key, source, isDirectory, conflict, ct);
                }
                else
                {
                    var output = ExactLocalPaths.FullPath(file ?? directory!);
                    if (file is not null && Path.EndsInDirectorySeparator(output))
                        throw new CliException(CliError.Arguments);
                    var rootPath = file is null ? output : Path.GetDirectoryName(output)!;
                    var explorer = services.GetRequiredService<ExplorerService>();
                    submit = (engine, ct) => engine.EnqueueExactDownloadsAsync(connection, uri.Bucket,
                        DownloadItems(explorer, connection, uri, prefix, recurse, file is null ? null : Path.GetFileName(output), ct),
                        rootPath, conflict, ct);
                }

                using var run = RunFiles.Create(runId, name);
                result = await TransferRunCoordinator.RunAsync(services.GetRequiredService<TransferEngine>(), submit,
                    ShowProgress(p, quiet), token);
                return WriteTransferResult(p, $"transfer.{name}", runId, result, token, run: run);
            }
        });
        return command;
    }

    private static async IAsyncEnumerable<DownloadItem> DownloadItems(ExplorerService explorer,
        StorageConnection connection, StorageUri uri, string prefix, bool recursive, string? fileName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!recursive)
        {
            var relative = fileName ?? uri.Key[(uri.Key.LastIndexOf('/') + 1)..];
            yield return new DownloadItem(uri.Key, false, null, relative);
            yield break;
        }

        string? next = null;
        do
        {
            var page = await explorer.ListExactPageAsync(connection, uri.Bucket, prefix, true, next, 1000, cancellationToken);
            foreach (var item in page.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.SizeBytes == 0 && item.Key.EndsWith('/'))
                    continue;
                if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
                    throw new CliException(new(1, "INVALID_STORAGE_PAGE", CliText.Get("PageOutsidePrefix")));
                StorageUri.ValidateKey(item.Key);
                yield return new DownloadItem(item.Key, false, item.SizeBytes, item.Key[prefix.Length..]);
            }
            if (page.ContinuationToken is not null && page.ContinuationToken == next)
                throw new CliException(new(1, "INVALID_STORAGE_PAGE", CliText.Get("PageTokenRepeated")));
            next = page.ContinuationToken;
        } while (next is not null);
    }

}
