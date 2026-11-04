using System.Globalization;
using System.Windows.Data;

namespace Cavdeck.App.Controls.Converters;

public sealed class ConnectionPresentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Guid;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
