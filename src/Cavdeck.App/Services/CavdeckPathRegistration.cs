using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Cavdeck.App.Services;

/// <summary>
/// Registers the installed CLI directory in the current user's PATH.
/// The MSI is deliberately per-user, so machine PATH and elevation are not required.
/// </summary>
internal static class CavdeckPathRegistration
{
    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private const uint WmSettingChange = 0x001a;
    private const uint SmtoAbortIfHung = 0x0002;

    internal static bool TryEnsureForCurrentUser() => TryUpdateForCurrentUser(add: true);

    internal static bool TryRemoveForCurrentUser() => TryUpdateForCurrentUser(add: false);

    internal static string UpdatePathValue(string? currentPath, string appDirectory, bool add)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        var entries = string.IsNullOrEmpty(currentPath)
            ? new List<string>()
            : currentPath.Split(Path.PathSeparator).ToList();
        var target = Normalize(appDirectory);

        if (add)
        {
            if (!entries.Any(entry => PathsEqual(entry, target)))
                entries.Add(target);
        }
        else
        {
            entries.RemoveAll(entry => PathsEqual(entry, target));
        }

        return string.Join(Path.PathSeparator, entries);
    }

    private static bool TryUpdateForCurrentUser(bool add)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return false;

            var appDirectory = GetInstalledCliDirectory();
            if (appDirectory is null)
                return false;

            var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            var updated = UpdatePathValue(current, appDirectory, add);
            if (string.Equals(current ?? string.Empty, updated, StringComparison.Ordinal))
                return true;

            Environment.SetEnvironmentVariable(
                "PATH", string.IsNullOrEmpty(updated) ? null : updated, EnvironmentVariableTarget.User);
            NotifyEnvironmentChanged();
            return true;
        }
        catch (Exception exception)
        {
            // PATH registration must never prevent installation, updates or normal startup.
            Debug.WriteLine($"Cavdeck PATH registration failed: {exception.GetType().Name}");
            return false;
        }
    }

    private static string? GetInstalledCliDirectory()
    {
        var current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (!string.Equals(Path.GetFileName(current), "current", StringComparison.OrdinalIgnoreCase))
            return null;

        var installRoot = Directory.GetParent(current)?.FullName;
        if (installRoot is null || !File.Exists(Path.Combine(installRoot, "Update.exe")))
            return null;

        var cliDirectory = Path.Combine(current, "cli");
        if (!File.Exists(Path.Combine(cliDirectory, "cavdeck-cli.exe")))
            return null;

        return cliDirectory;
    }

    private static bool PathsEqual(string left, string right) =>
        left.Length > 0 && string.Equals(Normalize(left), right, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        var value = path.Trim().Trim('"')
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.TrimEndingDirectorySeparator(value);
    }

    private static void NotifyEnvironmentChanged()
    {
        var parameter = Marshal.StringToHGlobalUni("Environment");
        try
        {
            _ = SendMessageTimeout(
                HwndBroadcast, WmSettingChange, IntPtr.Zero, parameter,
                SmtoAbortIfHung, 5000, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(parameter);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
