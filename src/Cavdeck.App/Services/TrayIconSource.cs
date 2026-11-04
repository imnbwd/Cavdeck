using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Cavdeck.App.Services;

internal static class TrayIconSource
{
    public static BitmapImage Load()
    {
        // H.NotifyIcon.Wpf reopens BitmapImage.UriSource asynchronously. A BitmapFrame
        // created from a stream has no URI and causes an unhandled UriFormatException.
        var resource = System.Windows.Application.GetResourceStream(
            AppResourceUri.Relative("Assets/Cavdeck.ico"))
            ?? throw new FileNotFoundException("Brand icon unavailable.");
        resource.Stream.Dispose();
        var iconUri = AppResourceUri.Absolute("Assets/Cavdeck.ico");
        var image = new BitmapImage(iconUri);
        image.Freeze();
        return image;
    }
}
