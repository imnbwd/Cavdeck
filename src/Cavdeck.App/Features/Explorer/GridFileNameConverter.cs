using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace Cavdeck.App.Features.Explorer;

// Keep the useful suffix visible when a two-line tile cannot fit the entire key.
public sealed class GridFileNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not string name)
            return "";
        var compact = values.Length > 1 && values[1] is true;
        var budget = compact ? 28 : 38;
        if (Units(name) <= budget)
            return name;

        var extensionStart = name.LastIndexOf('.');
        var suffix = extensionStart > 0 && name.Length - extensionStart <= 12
            ? name[extensionStart..] : "";
        var tail = suffix.Length > 0 ? suffix : name[^Math.Min(8, name.Length)..];
        var prefixBudget = budget - Units(tail) - 1;
        if (prefixBudget < 4)
            return name;

        var result = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(name);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var width = Units(element);
            if (used + width > prefixBudget)
                break;
            result.Append(element);
            used += width;
        }
        return result + "…" + tail;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        [DependencyProperty.UnsetValue, DependencyProperty.UnsetValue];

    private static int Units(string value)
    {
        var units = 0;
        foreach (var rune in value.EnumerateRunes())
            units += rune.Value >= 0x2E80 ? 2 : 1;
        return units;
    }
}
