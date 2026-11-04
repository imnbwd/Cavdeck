namespace Cavdeck.Application.Abstractions;

// Store secrets only in Windows Credential Manager (never in settings.json).
public interface ICredentialVault
{
    Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
        CancellationToken cancellationToken);

    Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
        string reference, CancellationToken cancellationToken);

    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}
