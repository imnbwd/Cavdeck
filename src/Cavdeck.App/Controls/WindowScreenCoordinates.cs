using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Cavdeck.Application.Shell;

namespace Cavdeck.App.Controls;

/// <summary>
/// Isolates Win32 virtual-desktop pixel coordinates from WPF device-independent units.
/// All native positions are captured and restored using the same physical pixel system.
/// </summary>
internal static class WindowScreenCoordinates
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    public static bool TryRead(Window window, out ScreenArea rect)
    {
        rect = default;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds))
            return false;
        var width = (double)bounds.Right - bounds.Left;
        var height = (double)bounds.Bottom - bounds.Top;
        if (width < 100 || height < 100)
            return false;
        rect = new ScreenArea(bounds.Left, bounds.Top, width, height);
        return true;
    }

    public static bool TryPlace(Window window, NativeRestoredWindow placement)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;
        return SetWindowPos(handle, IntPtr.Zero,
            (int)Math.Round(placement.Left), (int)Math.Round(placement.Top),
            Math.Max(1, (int)Math.Round(placement.DpiWidth)),
            Math.Max(1, (int)Math.Round(placement.DpiHeight)),
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out PixelRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
