using System.Threading;
using System.Windows.Media.Imaging;
using Cavdeck.App.Services;
using H.NotifyIcon;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TrayIconSourceTests
{
    [Fact]
    public void Icon_source_can_be_reopened_by_notify_icon()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var image = TrayIconSource.Load();
                Assert.IsType<BitmapImage>(image);
                Assert.True(image.UriSource.IsAbsoluteUri);
                using var stream = image.ToStreamAsync().GetAwaiter().GetResult();
                Span<byte> header = stackalloc byte[4];
                stream.ReadExactly(header);
                Assert.Equal(new byte[] { 0, 0, 1, 0 }, header.ToArray());
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }
}
