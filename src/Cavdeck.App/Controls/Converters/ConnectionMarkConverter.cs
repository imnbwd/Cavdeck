using System.Globalization;
using System.Windows.Data;

namespace Cavdeck.App.Controls.Converters;

public sealed class ConnectionMarkConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not Guid id)
            return false;
        return values[1] is Guid active && id == active;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
