namespace Cavdeck.Domain.Storage;

// HTTP header text for the details pane. Folders are prefixes and have no object headers.
public static class ObjectHeaderDisplay
{
    public const string Empty = "—";

    public static bool AppliesTo(ObjectEntryKind kind) => kind == ObjectEntryKind.File;

    public static string Value(string? header) =>
        string.IsNullOrWhiteSpace(header) ? Empty : header.Trim();
}
