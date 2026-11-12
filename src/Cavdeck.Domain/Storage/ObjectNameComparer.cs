using System.Globalization;

namespace Cavdeck.Domain.Storage;

/// <summary>
/// Compares object names using case-insensitive, culture-aware natural ordering.
/// Numeric sequences are compared by value, so "file2" sorts before "file10".
/// </summary>
public sealed class ObjectNameComparer : IComparer<string>
{
    private const CompareOptions Options =
        CompareOptions.IgnoreCase | CompareOptions.NumericOrdering;

    public static ObjectNameComparer Instance { get; } = new();

    private ObjectNameComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        var comparison = CultureInfo.CurrentCulture.CompareInfo.Compare(x, y, Options);

        // Culture-aware comparisons can consider distinct strings equivalent
        // (for example, names differing only by case). Use an ordinal fallback
        // to keep the ordering deterministic across full and incremental sorts.
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(x, y);
    }
}
