using System.CommandLine;
using System.Text;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Cli;

internal static partial class CliRuntime
{
    private static void RegisterConnectionCommands(RootCommand root, CancellationToken token)
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
        connection.Subcommands.Add(ConnectionSave(update: false, token));
        connection.Subcommands.Add(ConnectionSave(update: true, token));
        connection.Subcommands.Add(ConnectionRemove(token));
        root.Subcommands.Add(connection);

    }

    private static Command ConnectionSave(bool update, CancellationToken token)
    {
        var command = new Command(update ? "update" : "add", CliText.Get(update ? "ConnectionUpdate" : "ConnectionAdd"));
        var selector = new Argument<string>("connection") { Description = CliText.Get("ConnectionSelector") };
        var name = new Option<string?>("--name") { Required = !update, Description = CliText.Get("ConnectionName") };
        var provider = new Option<string?>("--provider") { Required = !update, Description = CliText.Get("ConnectionProvider") };
        var region = new Option<string?>("--region");
        var endpoint = new Option<string?>("--endpoint");
        var account = new Option<string?>("--account-id");
        var access = new Option<string?>("--access-key-id");
        var accessEnv = new Option<string?>("--access-key-id-env") { Description = CliText.Get("AccessEnv") };
        var secretEnv = new Option<string?>("--secret-env") { Description = CliText.Get("SecretEnv") };
        var pathStyle = new Option<bool>("--force-path-style");
        var defaultPathStyle = new Option<bool>("--no-force-path-style");
        var readOnly = new Option<bool>("--read-only");
        var writable = new Option<bool>("--writable");
        var bucket = new Option<string?>("--restricted-bucket");
        var prefix = new Option<string?>("--key-prefix");
        var clearEndpoint = new Option<bool>("--clear-endpoint");
        var clearBucket = new Option<bool>("--clear-restricted-bucket");
        var clearPrefix = new Option<bool>("--clear-key-prefix");
        foreach (var option in new Option[] { name, provider, region, endpoint, account, access, accessEnv, secretEnv,
            pathStyle, readOnly, bucket, prefix })
            command.Options.Add(option);
        if (update)
        {
            command.Arguments.Add(selector);
            foreach (var option in new Option[] { defaultPathStyle, writable, clearEndpoint, clearBucket, clearPrefix })
                command.Options.Add(option);
        }

        Bind(command, update ? "connection.update" : "connection.add", async (p, services) =>
        {
            var providerName = p.GetValue(provider);
            StorageProviderKind? requestedProvider = providerName switch
            {
                null => null,
                "aws-s3" => StorageProviderKind.AwsS3,
                "cloudflare-r2" => StorageProviderKind.CloudflareR2,
                "minio" => StorageProviderKind.Minio,
                "s3-compatible" => StorageProviderKind.S3Compatible,
                _ => throw new CliException(CliError.Arguments)
            };
            var endpointValue = p.GetValue(endpoint);
            var regionValue = p.GetValue(region);
            var accountValue = p.GetValue(account);
            var bucketValue = p.GetValue(bucket);
            var prefixValue = p.GetValue(prefix);
            var accessValue = p.GetValue(access);
            var accessVariable = p.GetValue(accessEnv);
            var secretVariable = p.GetValue(secretEnv);
            bool On(Option<bool> option) => p.GetResult(option) is not null && p.GetValue(option);
            if (accessValue is not null && accessVariable is not null || On(readOnly) && On(writable) ||
                On(pathStyle) && On(defaultPathStyle) || On(clearEndpoint) && endpointValue is not null ||
                On(clearBucket) && bucketValue is not null || On(clearPrefix) && prefixValue is not null)
                throw new CliException(CliError.Arguments);
            var rotate = !update || accessValue is not null || accessVariable is not null || secretVariable is not null;
            string? suppliedAccess = null;
            string? secret = null;
            if (rotate)
            {
                suppliedAccess = accessVariable is null ? accessValue : ReadCredentialVariable(accessVariable);
                if (string.IsNullOrWhiteSpace(suppliedAccess))
                    throw MissingCredential();
                secret = secretVariable is not null ? ReadCredentialVariable(secretVariable) : ReadSecret(p, token);
                if (string.IsNullOrWhiteSpace(secret))
                    throw MissingCredential();
            }

            ConnectionDraft Build(StorageConnection? current, string? currentAccess)
            {
                var kind = requestedProvider ?? current!.Provider;
                var sameProvider = current?.Provider == kind;
                if (kind == StorageProviderKind.CloudflareR2
                    ? endpointValue is not null || On(clearEndpoint) || regionValue is not (null or "auto") || On(defaultPathStyle)
                    : accountValue is not null)
                    throw new CliException(CliError.Arguments);
                return new ConnectionDraft
                {
                    Id = current?.Id,
                    Name = p.GetValue(name) ?? current?.Name ?? "",
                    Provider = kind,
                    Region = regionValue ?? (sameProvider ? current!.Region : null),
                    Endpoint = On(clearEndpoint) ? null : endpointValue ?? (sameProvider ? current!.Endpoint : null),
                    AccountId = accountValue ?? (sameProvider ? current!.AccountId : null),
                    RestrictedBucket = On(clearBucket) ? null : bucketValue ?? current?.RestrictedBucket,
                    KeyPrefix = On(clearPrefix) ? null : prefixValue ?? current?.KeyPrefix,
                    ForcePathStyle = On(pathStyle) || !On(defaultPathStyle) && sameProvider && current!.ForcePathStyle,
                    IsReadOnly = On(readOnly) || !On(writable) && current?.IsReadOnly == true,
                    AccessKeyId = rotate ? suppliedAccess : currentAccess,
                    SecretAccessKey = secret,
                    KeepExistingSecret = !rotate
                };
            }

            var service = services.GetRequiredService<ConnectionService>();
            StorageConnection saved;
            if (update)
            {
                var current = await ResolveAsync(service, p.GetValue(selector)!, token);
                saved = await service.UpdateAsync(current.Id, Build, token);
            }
            else
                saved = await service.SaveAsync(Build(null, null), token);
            return new { saved.Id, saved.Name, provider = saved.Provider.ToString(),
                endpoint = SafeEndpoint(saved.Endpoint), saved.Region, saved.IsReadOnly,
                saved.RestrictedBucket, saved.KeyPrefix, saved.ForcePathStyle };
        }, writableCatalog: true);
        return command;
    }

    private static Command ConnectionRemove(CancellationToken token)
    {
        var command = new Command("remove", CliText.Get("ConnectionRemove"));
        var selector = new Argument<string>("connection") { Description = CliText.Get("ConnectionSelector") };
        var yes = new Option<bool>("--yes") { Description = CliText.Get("ConfirmOperation") };
        var dryRun = new Option<bool>("--dry-run") { Description = CliText.Get("PreviewOperation") };
        command.Arguments.Add(selector);
        command.Options.Add(yes);
        command.Options.Add(dryRun);
        Bind(command, "connection.remove", async (p, services) =>
        {
            var preview = p.GetValue(dryRun);
            if (preview == p.GetValue(yes))
                throw new CliException(CliError.Arguments with { Message = CliText.Get("PreviewOrConfirm") });
            var service = services.GetRequiredService<ConnectionService>();
            var current = await ResolveAsync(service, p.GetValue(selector)!, token);
            if (!preview)
                await service.DeleteAsync(current.Id, token);
            return new { current.Id, current.Name, dryRun = preview, removed = !preview };
        }, writableCatalog: true, previewOption: dryRun);
        return command;
    }

    private static string ReadCredentialVariable(string variable)
    {
        if (string.IsNullOrWhiteSpace(variable) || variable.Length > 256 ||
            variable.Contains('=') || variable.Any(char.IsControl))
            throw new CliException(CliError.Arguments);
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? throw MissingCredential() : value;
    }

    private static CliException MissingCredential() => new(new(2, "CREDENTIALS_REQUIRED", CliText.Get("CredentialsRequired")));

    private static string ReadSecret(ParseResult parsed, CancellationToken token)
    {
        if (parsed.GetValue<bool>("--no-input") || IsJson(parsed) || Console.IsInputRedirected || Console.IsErrorRedirected)
            throw MissingCredential();
        Console.Error.Write(CliText.Get("SecretPrompt"));
        var secret = new StringBuilder();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable)
                {
                    token.WaitHandle.WaitOne(50);
                    continue;
                }
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    return secret.ToString();
                if (key.Key == ConsoleKey.Escape)
                    throw new CliException(CliError.Cancelled);
                if (key.Key == ConsoleKey.Backspace && secret.Length > 0)
                    secret.Length--;
                else if (!char.IsControl(key.KeyChar) && secret.Length < 1025)
                    secret.Append(key.KeyChar);
            }
        }
        finally { Console.Error.WriteLine(); }
    }
    private static string SafeEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : "";
}
