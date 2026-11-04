namespace Cavdeck.Domain.Storage;

public static class CredentialReferences
{
    public static string ForConnection(Guid id) => $"Cavdeck:connection:{id:D}";
}
