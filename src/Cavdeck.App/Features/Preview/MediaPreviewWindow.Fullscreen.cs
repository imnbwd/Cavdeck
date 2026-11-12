using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cavdeck.App.Features.Preview;

public partial class MediaPreviewWindow
{
    private bool _fullScreen;
    private Rect _previousBounds;
    private WindowState _previousWindowState;
    private ResizeMode _previousResizeMode;
    private bool _previousTopmost;
    private DateTime _lastPointerActivityUtc = DateTime.UtcNow;
    private Point? _lastPointerPosition;

    private void PlaybackRoot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullScreen)
            return;
        var point = e.GetPosition(PlaybackRoot);
        if (_lastPointerPosition is Point previous &&
            Math.Abs(previous.X - point.X) + Math.Abs(previous.Y - point.Y) < 1)
            return;
        _lastPointerPosition = point;
        ShowFullscreenControls();
    }

    private void ShowFullscreenControls()
    {
        if (!_fullScreen)
            return;
        _lastPointerActivityUtc = DateTime.UtcNow;
        ControlsBar.Visibility = Visibility.Visible;
    }

    private void UpdateFullscreenControls()
    {
        if (!_fullScreen)
            return;
        var interacting = _dragging || ControlsBar.IsMouseOver || SpeedSelector.IsDropDownOpen
            || SeekSlider.IsKeyboardFocusWithin || VolumeSlider.IsKeyboardFocusWithin;
        if (MediaPlaybackMath.ShouldHideControls(_playing, interacting,
                DateTime.UtcNow - _lastPointerActivityUtc))
            ControlsBar.Visibility = Visibility.Collapsed;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isVideo)
            return;
        if (_fullScreen)
            ExitFullscreen();
        else
            EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, 2); // nearest monitor
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) ||
            PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;

        var physicalTopLeft = new Point(info.Monitor.Left, info.Monitor.Top);
        var physicalBottomRight = new Point(info.Monitor.Right, info.Monitor.Bottom);
        var topLeft = target.TransformFromDevice.Transform(physicalTopLeft);
        var bottomRight = target.TransformFromDevice.Transform(physicalBottomRight);

        _previousWindowState = WindowState;
        _previousBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _previousResizeMode = ResizeMode;
        _previousTopmost = Topmost;
        _fullScreen = true;
        IsImmersiveFullScreen = true;
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true; // cover the taskbar on the selected display
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;

        PlaybackRoot.RowDefinitions[1].Height = new GridLength(0);
        Grid.SetRow(ControlsBar, 0);
        ControlsBar.VerticalAlignment = VerticalAlignment.Bottom;
        ControlsBar.Margin = new Thickness(16, 0, 16, 16);
        ControlsBar.CornerRadius = new CornerRadius(12);
        ControlsBar.BorderThickness = new Thickness(0);
        ControlsBar.Opacity = 0.94;
        MediaSurface.Focus();
        _lastPointerPosition = null;
        ShowFullscreenControls();
        UpdatePlaybackLabels();
    }

    private void ExitFullscreen()
    {
        if (!_fullScreen)
            return;
        _fullScreen = false;
        ControlsBar.Visibility = Visibility.Visible;
        Grid.SetRow(ControlsBar, 1);
        PlaybackRoot.RowDefinitions[1].Height = GridLength.Auto;
        ControlsBar.VerticalAlignment = VerticalAlignment.Stretch;
        ControlsBar.Margin = new Thickness(0);
        ControlsBar.CornerRadius = new CornerRadius(0);
        ControlsBar.BorderThickness = new Thickness(0, 1, 0, 0);
        ControlsBar.Opacity = 1;
        Topmost = _previousTopmost;
        ResizeMode = _previousResizeMode;
        Left = _previousBounds.Left;
        Top = _previousBounds.Top;
        Width = _previousBounds.Width;
        Height = _previousBounds.Height;
        IsImmersiveFullScreen = false;
        WindowState = _previousWindowState;
        UpdatePlaybackLabels();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SpeedSelector.IsDropDownOpen)
                SpeedSelector.IsDropDownOpen = false;
            else if (_fullScreen)
                ExitFullscreen();
            else
                Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && _isVideo)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.F && _isVideo && Keyboard.Modifiers == ModifierKeys.None
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && !SeekSlider.IsKeyboardFocusWithin
                 && !VolumeSlider.IsKeyboardFocusWithin
                 && Keyboard.FocusedElement is not ButtonBase
                 && !SpeedSelector.IsKeyboardFocusWithin
                 && Keyboard.Modifiers == ModifierKeys.None)
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.None
                 && !SeekSlider.IsKeyboardFocusWithin && !VolumeSlider.IsKeyboardFocusWithin
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            Skip(-5);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None
                 && !SeekSlider.IsKeyboardFocusWithin && !VolumeSlider.IsKeyboardFocusWithin
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            Skip(5);
            e.Handled = true;
        }
        else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            Mute_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor, Work;
        public int Flags;
    }
}
