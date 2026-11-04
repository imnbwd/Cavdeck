using System.Runtime.InteropServices;
using Cavdeck.Application.Shell;

namespace Cavdeck.App.Controls;

internal static class MonitorWorkAreas
{
    public static IReadOnlyList<ScreenArea> Capture()
    {
        var areas = new List<ScreenArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, IntPtr _, IntPtr _) =>
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return true;

            // GetMonitorInfo returns virtual-desktop PHYSICAL coordinates for a
            // PerMonitorV2 process. Never divide each monitor's origin by its own DPI.
            areas.Add(new ScreenArea(
                info.Work.Left, info.Work.Top,
                info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top,
                ScaleOf(monitor), (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return areas;
    }

    private static double ScaleOf(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX >= 96)
                return dpiX / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        return 1;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectBounds Monitor;
        public RectBounds Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
