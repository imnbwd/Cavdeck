using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Cavdeck.App.Features.Diagnostics;

namespace Cavdeck.App.Services;

public enum GlobalHotkeyResult { Success, Invalid, Conflict, Unavailable }

/// <summary>
/// A single optional OS hotkey, registered against the existing WPF window handle.
/// No background hook, keyboard observation, or additional process is required.
/// All methods are called on the UI thread.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyMessage = 0x0312; // WM_HOTKEY
    private const uint NoRepeat = 0x4000;
    private const int FirstId = 0x6A70;
    private const int SecondId = 0x6A71;
    private HwndSource? _source;
    private Action? _activate;
    private int _activeId;
    private string? _registeredGesture;
    private bool _disposed;

    public event EventHandler? Changed;
    public bool IsActive => _activeId != 0;
    public string? RegisteredGesture => _registeredGesture;
    public GlobalHotkeyResult LastResult { get; private set; } = GlobalHotkeyResult.Success;

    public GlobalHotkeyResult Initialize(Window window, Action activate, bool enabled, string? gesture)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(activate);
        if (_disposed || _source is not null)
            return GlobalHotkeyResult.Unavailable;
        var handle = new WindowInteropHelper(window).Handle;
        _source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            LastResult = GlobalHotkeyResult.Unavailable;
            Changed?.Invoke(this, EventArgs.Empty);
            return LastResult;
        }

        _activate = activate;
        _source.AddHook(WndProc);
        return TryApply(enabled, gesture);
    }

    /// <summary>
    /// Registers a replacement BEFORE releasing the previous binding. A conflict
    /// leaves the old shortcut intact; callers persist preferences only on success.
    /// </summary>
    public GlobalHotkeyResult TryApply(bool enabled, string? gesture)
    {
        if (_disposed || _source is null)
            return Report(GlobalHotkeyResult.Unavailable);
        if (!enabled)
        {
            UnregisterCurrent();
            return Report(GlobalHotkeyResult.Success);
        }
        if (!GlobalHotkeyGesture.TryParse(gesture, out var parsed))
            return Report(GlobalHotkeyResult.Invalid);
        if (IsActive && _registeredGesture == parsed.Text)
            return Report(GlobalHotkeyResult.Success);

        var next = _activeId == FirstId ? SecondId : FirstId;
        if (!RegisterHotKey(_source.Handle, next, parsed.Modifiers | NoRepeat, parsed.VirtualKey))
        {
            DiagnosticLog.Current.Queue("global-hotkey",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            return Report(GlobalHotkeyResult.Conflict);
        }

        UnregisterCurrent();
        _activeId = next;
        _registeredGesture = parsed.Text;
        return Report(GlobalHotkeyResult.Success);
    }

    private GlobalHotkeyResult Report(GlobalHotkeyResult result)
    {
        LastResult = result;
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void UnregisterCurrent()
    {
        if (_activeId != 0 && _source is not null)
            UnregisterHotKey(_source.Handle, _activeId);
        _activeId = 0;
        _registeredGesture = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != HotkeyMessage || _activeId == 0 || wParam.ToInt64() != _activeId)
            return IntPtr.Zero;
        handled = true;
        try { _activate?.Invoke(); }
        catch (Exception exception) { DiagnosticLog.Current.Queue("global-hotkey", exception); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterCurrent();
        _source?.RemoveHook(WndProc);
        _source = null;
        _activate = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
