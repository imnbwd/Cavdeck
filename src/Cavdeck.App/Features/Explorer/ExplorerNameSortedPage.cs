using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

// Merge a new S3 page into the Name-ascending list without replacing existing rows.
// Match ObjectLayout.Sort: folders first, then natural object-name order.
public static class ExplorerNameSortedPage
{
    public static void Append(IList<ExplorerRow> rows, IEnumerable<ObjectEntry> page, LocalizationService texts)
    {
        var seen = rows.Select(row => row.Entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in page)
        {
            if (!seen.Add(entry.Key))
                continue;

            var start = 0;
            var end = rows.Count;
            while (start < end)
            {
                var middle = start + (end - start) / 2;
                var current = rows[middle].Entry;
                var comparison = current.Kind.CompareTo(entry.Kind);
                if (comparison == 0)
                    comparison = ObjectNameComparer.Instance.Compare(current.Name, entry.Name);

                // Insert after equal names, preserving the existing order.
                if (comparison <= 0)
                    start = middle + 1;
                else
                    end = middle;
            }

            rows.Insert(start, new ExplorerRow(entry, texts));
        }
    }
}
