using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class AppWindowFullscreenTests
{
    private const string NonFullscreenProperty = "NonRudeHWND";
    private static readonly IntPtr NonFullscreenValue = new(1);

    [Fact]
    public void Maximized_window_is_marked_as_non_fullscreen_before_first_show()
    {
        RunSta(() =>
        {
            var window = new AppWindow
            {
                Width = 800,
                Height = 500,
                ShowInTaskbar = false,
                WindowState = WindowState.Maximized
            };

            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                Assert.False(window.IsVisible);
                Assert.Equal(NonFullscreenValue, GetProp(hwnd, NonFullscreenProperty));

                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(NonFullscreenValue, GetProp(hwnd, NonFullscreenProperty));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Immersive_video_fullscreen_does_not_claim_the_taskbar()
    {
        RunSta(() =>
        {
            var window = new AppWindow
            {
                Width = 800,
                Height = 500,
                ShowInTaskbar = false,
                IsImmersiveFullScreen = true
            };

            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                Assert.Equal(IntPtr.Zero, GetProp(hwnd, NonFullscreenProperty));

                window.IsImmersiveFullScreen = false;
                Assert.Equal(NonFullscreenValue, GetProp(hwnd, NonFullscreenProperty));

                window.IsImmersiveFullScreen = true;
                Assert.Equal(IntPtr.Zero, GetProp(hwnd, NonFullscreenProperty));

                window.IsImmersiveFullScreen = false;
                Assert.Equal(NonFullscreenValue, GetProp(hwnd, NonFullscreenProperty));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "AppWindow WPF test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
