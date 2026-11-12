using System.IO;
using System.Windows.Media.Imaging;

namespace Cavdeck.App.Features.Preview;

/// <summary>
/// Shared bounded WIC decode for the preview pane and immersive viewer.
/// Both surfaces use the same resolution so an already loaded image can be
/// reused without a second cloud download or a lower-quality viewer image.
/// </summary>
internal static class PreviewImageDecoder
{
    internal const int MaximumSide = 6144;

    internal static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
            return null;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            // Read dimensions before decoding pixels. A small compressed image
            // can otherwise expand to an enormous bitmap in memory.
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                return null;

            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                return null;

            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (Math.Max(frame.PixelWidth, frame.PixelHeight) > MaximumSide)
            {
                // Bound the longer side for both panoramic and portrait images.
                if (frame.PixelWidth >= frame.PixelHeight)
                    bitmap.DecodePixelWidth = MaximumSide;
                else
                    bitmap.DecodePixelHeight = MaximumSide;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or InvalidOperationException or FileFormatException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
