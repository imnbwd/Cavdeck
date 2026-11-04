using System.Globalization;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

internal static class ExplorerText
{
    public static string Size(ObjectEntry entry) =>
        entry.Kind == ObjectEntryKind.Folder ? "—" : FormatBytes(entry.SizeBytes);

    public static string Modified(ObjectEntry entry) =>
        entry.LastModified is { } value
            ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "—";

    public static string ETag(string? etag) => etag?.Trim().Trim('"') ?? "";

    public static string KindKey(ObjectFileKind kind) => kind switch
    {
        ObjectFileKind.Folder => "Explorer_KindFolder",
        ObjectFileKind.Image => "Explorer_KindImage",
        ObjectFileKind.Video => "Explorer_KindVideo",
        ObjectFileKind.Audio => "Explorer_KindAudio",
        ObjectFileKind.Code => "Explorer_KindCode",
        ObjectFileKind.Data => "Explorer_KindData",
        ObjectFileKind.Text => "Explorer_KindText",
        ObjectFileKind.Document => "Explorer_KindDocument",
        ObjectFileKind.Pdf => "Explorer_KindPdf",
        ObjectFileKind.Archive => "Explorer_KindArchive",
        ObjectFileKind.Spreadsheet => "Explorer_KindSheet",
        ObjectFileKind.Presentation => "Explorer_KindSlides",
        ObjectFileKind.Font => "Explorer_KindFont",
        ObjectFileKind.Program => "Explorer_KindProgram",
        _ => "Explorer_KindFile"
    };

    public static string NameMessage(LocalizationService texts, NameIssue issue) => issue switch
    {
        NameIssue.Separator => texts["Explorer_NameSeparator"],
        NameIssue.Reserved => texts["Explorer_NameReserved"],
        NameIssue.Control => texts["Explorer_NameControl"],
        NameIssue.TooLong => texts["Explorer_NameTooLong"],
        _ => texts["Explorer_NameEmpty"]
    };

    public static string FailureTitle(LocalizationService texts, StorageFailure failure)
    {
        if (failure.ErrorCode is "IntoItself")
            return texts["Explorer_IntoItself"];
        if (failure.ErrorCode is "ServerSideCopy")
            return texts["Explorer_CopyUnsupported"];
        if (Enum.TryParse<NameIssue>(failure.ErrorCode, out var issue) && issue != NameIssue.None)
            return NameMessage(texts, issue);
        return texts["Connection_Failure_" + failure.Kind];
    }

    public static string FailureDetail(LocalizationService texts, StorageFailure failure)
    {
        var hint = failure.ErrorCode is "IntoItself" or "ServerSideCopy"
            ? ""
            : texts["Connection_Failure_" + failure.Kind + "Hint"];
        var technical = ConnectionText.Technical(texts, failure);
        if (hint.Length == 0)
            return technical;
        return technical.Length == 0 ? hint : hint + "\n" + technical;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
            return string.Format(CultureInfo.CurrentCulture, "{0} B", bytes);
        var number = value >= 10
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);
        return number + " " + units[unit];
    }
}
