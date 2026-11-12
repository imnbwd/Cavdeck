using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cavdeck.App.Features.Preview;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class PreviewImageDecoderTests
{
    [Theory]
    [InlineData(8192, 32)]
    [InlineData(32, 8192)]
    public void Very_wide_and_tall_images_are_decoded_with_a_bounded_longest_side(
        int width, int height)
    {
        OnSta(() =>
        {
            var image = PreviewImageDecoder.Decode(Png(width, height));

            Assert.NotNull(image);
            Assert.InRange(image.PixelWidth, 1, PreviewImageDecoder.MaximumSide);
            Assert.InRange(image.PixelHeight, 1, PreviewImageDecoder.MaximumSide);
            Assert.True(image.PixelWidth < width || image.PixelHeight < height);
            Assert.True(image.IsFrozen);
        });
    }

    [Fact]
    public void Small_images_keep_their_original_dimensions()
    {
        OnSta(() =>
        {
            var image = PreviewImageDecoder.Decode(Png(200, 100));

            Assert.NotNull(image);
            Assert.Equal(200, image.PixelWidth);
            Assert.Equal(100, image.PixelHeight);
            Assert.True(image.IsFrozen);
        });
    }

    [Fact]
    public void Empty_or_invalid_input_does_not_crash_the_preview()
    {
        OnSta(() =>
        {
            Assert.Null(PreviewImageDecoder.Decode(null));
            Assert.Null(PreviewImageDecoder.Decode([]));
            Assert.Null(PreviewImageDecoder.Decode([0x01, 0x02, 0x03, 0x04]));
        });
    }

    private static byte[] Png(int width, int height)
    {
        var stride = checked(width * 4);
        var source = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, new byte[checked(stride * height)], stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Image decode test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
