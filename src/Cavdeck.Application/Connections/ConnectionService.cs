using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Connections;

public sealed class ConnectionService
{
    private readonly IConnectionCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly IStorageProbe _probe;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly IConnectionMutationLock? _mutationLock;

    public ConnectionService(IConnectionCatalog catalog, ICredentialVault vault, IStorageProbe probe,
        IConnectionMutationLock? mutationLock = null)
    {
        _catalog = catalog;
        _vault = vault;
        _probe = probe;
        _mutationLock = mutationLock;
    }

    public Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken = default) =>
        _catalog.ListAsync(cancellationToken);

    public async Task<string?> ReadAccessKeyIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await FindAsync(id, cancellationToken);
        if (connection is null)
            return null;
        var stored = await _vault.ReadAsync(connection.CredentialReference, cancellationToken);
        return stored?.AccessKeyId;
    }

    public async Task<ConnectionAttempt> TestAsync(
        ConnectionDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (target, accessKey, secret, issues) = await ResolveSecretsAsync(draft, cancellationToken);
        if (issues.Count > 0 || target is null)
            return ConnectionAttempt.Invalid(issues);
        if (secret is null || accessKey is null)
            return ConnectionAttempt.FromProbe(StorageProbeResult.Failed(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null)));

        var probe = await _probe.ProbeAsync(new StorageProbeRequest
        {
            Region = target.Region,
            ServiceUrl = string.IsNullOrEmpty(target.Endpoint) ? null : target.Endpoint,
            ForcePathStyle = target.ForcePathStyle,
            AccessKeyId = accessKey,
            SecretAccessKey = secret,
            Bucket = target.RestrictedBucket,
            KeyPrefix = target.KeyPrefix
        }, cancellationToken);
        return ConnectionAttempt.FromProbe(probe);
    }

    public async Task<ConnectionAttempt> TestStoredAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await FindAsync(id, cancellationToken);
        if (connection is null)
            return ConnectionAttempt.FromProbe(StorageProbeResult.Failed(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null)));

        var stored = await _vault.ReadAsync(connection.CredentialReference, cancellationToken);
        if (stored is null)
            return ConnectionAttempt.FromProbe(StorageProbeResult.Failed(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null)));

        var probe = await _probe.ProbeAsync(new StorageProbeRequest
        {
            Region = connection.Region,
            ServiceUrl = string.IsNullOrEmpty(connection.Endpoint) ? null : connection.Endpoint,
            ForcePathStyle = connection.ForcePathStyle,
            AccessKeyId = stored.Value.AccessKeyId,
            SecretAccessKey = stored.Value.SecretAccessKey,
            Bucket = connection.RestrictedBucket,
            KeyPrefix = connection.KeyPrefix
        }, cancellationToken);
        return ConnectionAttempt.FromProbe(probe);
    }

    public async Task<StorageConnection> SaveAsync(
        ConnectionDraft draft, CancellationToken cancellationToken = default, StorageConnection? expected = null)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = _mutationLock is null ? null : await _mutationLock.AcquireAsync(cancellationToken);
            var all = await _catalog.ListForMutationAsync(cancellationToken);
            if (expected is not null && (draft.Id != expected.Id || !all.Contains(expected)))
                throw new StorageException(new StorageFailure(StorageFailureKind.PreconditionFailed,
                    "ConnectionConfigurationChanged", null, null, null));
            return await SaveCoreAsync(draft, all, cancellationToken);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<StorageConnection> UpdateAsync(Guid id,
        Func<StorageConnection, string?, ConnectionDraft> buildDraft, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = _mutationLock is null ? null : await _mutationLock.AcquireAsync(cancellationToken);
            var all = await _catalog.ListForMutationAsync(cancellationToken);
            var current = all.FirstOrDefault(item => item.Id == id) ?? throw ConnectionMissing();
            var stored = await _vault.ReadAsync(current.CredentialReference, cancellationToken);
            var draft = buildDraft(current, stored?.AccessKeyId);
            if (draft.Id != id)
                throw new ArgumentException("The update must keep the original connection ID.", nameof(buildDraft));
            return await SaveCoreAsync(draft, all, cancellationToken);
        }
        finally { _mutationGate.Release(); }
    }

    private async Task<StorageConnection> SaveCoreAsync(ConnectionDraft draft,
        IReadOnlyList<StorageConnection> all, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var previousConnection = draft.Id is null ? null : all.FirstOrDefault(item => item.Id == draft.Id);
        var (target, accessKey, secret, issues) = await ResolveSecretsAsync(draft, cancellationToken,
            previousConnection?.CredentialReference);
        if (issues.Count > 0 || target is null)
            throw new ConnectionValidationException(issues);
        if (secret is null || accessKey is null)
            throw new StorageException(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null));
        if (draft.Id is not null && previousConnection is null)
            throw ConnectionMissing();

        var id = draft.Id ?? Guid.NewGuid();
        var reference = previousConnection?.CredentialReference ?? CredentialReferences.ForConnection(id);
        var name = draft.Name.Trim();
        if (!string.Equals(previousConnection?.Name, name, StringComparison.OrdinalIgnoreCase) &&
            all.Any(item => item.Id != id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConnectionValidationException([new(ConnectionField.Name, ConnectionIssueCode.Duplicate)]);
        var creating = draft.Id is null;
        var typedNewSecret = !draft.KeepExistingSecret || !string.IsNullOrWhiteSpace(draft.SecretAccessKey);
        var previousCredential = typedNewSecret && !creating
            ? await _vault.ReadAsync(reference, cancellationToken)
            : null;

        var connection = new StorageConnection(
            id,
            name,
            draft.Provider,
            target!.Endpoint,
            target.Region,
            reference,
            draft.IsReadOnly,
            target.RestrictedBucket,
            target.KeyPrefix,
            target.ForcePathStyle,
            target.AccountId)
        {
            ClientRevision = previousConnection?.ClientRevision ?? 0
        };
        var clientConfigurationChanged = previousConnection is null
            || new ConnectionChange(previousConnection, connection, typedNewSecret).RequiresReconnect;
        connection = connection with
        {
            ClientRevision = (previousConnection?.ClientRevision ?? 0)
                + (clientConfigurationChanged ? 1 : 0)
        };

        var credentialWriteAttempted = false;
        try
        {
            if (typedNewSecret)
            {
                // A vault implementation can fail after writing. Roll back even then.
                credentialWriteAttempted = true;
                await _vault.SaveAsync(reference, accessKey, secret, cancellationToken);
            }

            await _catalog.UpsertAsync(connection, cancellationToken);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            try
            {
                if (previousConnection is not null)
                    await _catalog.UpsertAsync(previousConnection, CancellationToken.None);
                else
                    await _catalog.DeleteAsync(id, CancellationToken.None);
            }
            catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            if (credentialWriteAttempted)
            {
                try
                {
                    if (previousCredential is { } previous)
                        await _vault.SaveAsync(reference, previous.AccessKeyId, previous.SecretAccessKey, CancellationToken.None);
                    else
                        await _vault.DeleteAsync(reference, CancellationToken.None);
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Connection save failed and rollback was incomplete.",
                    new[] { error }.Concat(rollbackErrors));
            throw;
        }

        return connection;
    }

    private async Task<(StorageTarget? Target, string? AccessKey, string? Secret, IReadOnlyList<ConnectionIssue> Issues)>
        ResolveSecretsAsync(ConnectionDraft draft, CancellationToken cancellationToken, string? credentialReference = null)
    {
        var resolved = ConnectionPlanner.TryResolve(draft, out var target, out var issues);
        var accessKey = draft.AccessKeyId?.Trim() ?? "";
        var typedSecret = string.IsNullOrWhiteSpace(draft.SecretAccessKey) ? null : draft.SecretAccessKey.Trim();
        var keepStored = draft.KeepExistingSecret && typedSecret is null;
        var local = issues.ToList();
        local.AddRange(ConnectionPlanner.ValidateCredentials(accessKey, typedSecret, requireSecret: !keepStored));
        if (!resolved || target is null || local.Count > 0)
            return (null, null, null, local);

        if (!keepStored)
            return (target, accessKey, typedSecret, local);

        if (draft.Id is null)
        {
            local.Add(new ConnectionIssue(ConnectionField.Secret, ConnectionIssueCode.SecretRequired));
            return (null, null, null, local);
        }

        var stored = await _vault.ReadAsync(credentialReference ?? CredentialReferences.ForConnection(draft.Id.Value), cancellationToken);
        if (stored is null)
            return (target, accessKey, null, local);

        if (!string.Equals(stored.Value.AccessKeyId, accessKey, StringComparison.Ordinal))
        {
            local.Add(new ConnectionIssue(ConnectionField.Secret, ConnectionIssueCode.SecretRequired));
            return (null, null, null, local);
        }

        return (target, stored.Value.AccessKeyId, stored.Value.SecretAccessKey, local);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = _mutationLock is null ? null : await _mutationLock.AcquireAsync(cancellationToken);
            await DeleteCoreAsync(id, cancellationToken);
        }
        finally { _mutationGate.Release(); }
    }

    private async Task DeleteCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = (await _catalog.ListForMutationAsync(cancellationToken)).FirstOrDefault(item => item.Id == id);
        if (connection is null)
            return;

        // Removing credentials first can leave a visible, unusable connection if catalog deletion fails.
        var credentials = await _vault.ReadAsync(connection.CredentialReference, cancellationToken);
        try
        {
            await _catalog.DeleteAsync(id, cancellationToken);
            await _vault.DeleteAsync(connection.CredentialReference, cancellationToken);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            try { await _catalog.UpsertAsync(connection, CancellationToken.None); }
            catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            if (credentials is { } old)
            {
                try
                {
                    await _vault.SaveAsync(connection.CredentialReference, old.AccessKeyId,
                        old.SecretAccessKey, CancellationToken.None);
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Connection deletion failed and rollback was incomplete.",
                    new[] { error }.Concat(rollbackErrors));
            throw;
        }
    }

    private async Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var all = await _catalog.ListAsync(cancellationToken);
        return all.FirstOrDefault(connection => connection.Id == id);
    }

    private static StorageException ConnectionMissing() => new(new StorageFailure(
        StorageFailureKind.CredentialMissing, "ConnectionMissing", null, null, null));
}
