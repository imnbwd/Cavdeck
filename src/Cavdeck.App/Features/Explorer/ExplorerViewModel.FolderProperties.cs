using Cavdeck.App.Controls;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public sealed partial class ExplorerViewModel
{
    private bool CanShowFolderProperties(object? parameter) =>
        !_disposed && !_busy && _connection is not null &&
        (parameter is BucketRow row ? ShowBuckets && BucketRows.Contains(row)
            : ShowObjects && (parameter is string scope && scope == "Current" || _selected.Count > 0));

    private void ShowFolderProperties(object? parameter)
    {
        if (!CanShowFolderProperties(parameter) || _connection is not { } connection)
            return;

        var bucket = parameter is BucketRow row ? row.Name : _bucket!;
        var current = parameter is BucketRow || parameter is string scope && scope == "Current";
        var prefix = parameter is BucketRow ? ObjectLayout.RootPrefix(connection) : _prefix;
        ObjectEntry[] entries = current
            ? [new ObjectEntry(ObjectEntryKind.Folder, prefix.Length == 0 ? bucket : ObjectLayout.DisplayName(prefix),
                prefix, 0, null, null)]
            : _selected.Select(item => item.Entry).ToArray();
        var title = entries.Length == 1 ? entries[0].Name
            : string.Format(_texts["Explorer_StatusSelected"], entries.Length);
        var location = ObjectLayout.ObjectUri(bucket, entries.Length == 1 ? entries[0].Key : _prefix);

        using var content = new FolderPropertiesView(_texts, title, connection.Name, location,
            (progress, token) => _explorer.GetFolderPropertiesAsync(connection, bucket, entries, progress, token),
            CopyDetailValue);
        _dialogs.Show(null, new DialogRequest
        {
            Title = _texts["FolderProperties_Title"],
            Content = content,
            PrimaryText = _texts["Dialog_Close"]
        });
    }
}
