using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cavdeck.Application.Transfers;

public static class ExactLocalPaths
{
    public static bool IsValidRelative(string path)
    {
        return path.Split('/').All(segment =>
            segment.Length is > 0 and <= 255
            && segment[^1] is not (' ' or '.')
            && !segment.Any(c => char.IsControl(c) || "<>:\"\\|?*~".Contains(c))
            && !IsDeviceName(segment.Split('.')[0].TrimEnd(' ')));
    }

    private static bool IsDeviceName(string stem)
    {
        var name = stem.ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal)
                || name.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(name[3]);
    }

    public static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new IOException("The local path is invalid.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var tail = Path.TrimEndingDirectorySeparator(full)[root.Length..].Replace('\\', '/');
        if (tail.Length > 0 && !IsValidRelative(tail))
            throw new IOException("The local path is invalid.");
        return full;
    }

    // Open each component without following reparse points and deny deletion
    // for the lifetime of the operation. Check attributes on the opened handle.
    public static IDisposable Pin(string path, bool includeLeaf, bool createDirectories = false,
        FileShare leafShare = FileShare.Read)
    {
        var full = FullPath(path);
        var root = Path.GetPathRoot(full)!;
        var components = Path.TrimEndingDirectorySeparator(full)[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var pins = new PathPins();
        try
        {
            var count = includeLeaf ? components.Length : components.Length - 1;
            var current = root;
            for (var i = 0; i < count; i++)
            {
                current = Path.Combine(current, components[i]);
                var directory = i < components.Length - 1 || createDirectories;
                if (createDirectories && directory)
                    Directory.CreateDirectory(current);
                pins.Add(current, directory, directory ? FileShare.Read : leafShare);
            }
            return pins;
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    public static void RejectExistingReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A reparse point is present in the transfer path.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class PathPins : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        public void Add(string path, bool directory, FileShare share)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            const uint readAttributes = 0x80;
            const uint openExisting = 3;
            const uint openReparsePoint = 0x00200000;
            const uint backupSemantics = 0x02000000;
            var longPath = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            var handle = CreateFile(longPath, readAttributes, share, IntPtr.Zero,
                openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
            _handles.Add(handle);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8))
                throw new IOException("The transfer path could not be opened.", new Win32Exception(Marshal.GetLastPInvokeError()));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0
                || directory && (info.Attributes & FileAttributes.Directory) == 0)
                throw new IOException("The transfer path contains a reparse point or a non-directory parent.");
        }

        public void Dispose()
        {
            foreach (var handle in _handles)
                handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTagInfo info, uint size);
}
