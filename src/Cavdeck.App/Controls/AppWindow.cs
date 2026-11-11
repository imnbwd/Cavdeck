using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Shell;
using System.Windows.Threading;
using Cavdeck.App.Localization;

namespace Cavdeck.App.Controls;

public class AppWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int MonitorDefaultToNearest = 2;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const double TitleBarHeight = 48;

    private readonly WindowChrome _chrome;
    private HwndSource? _source;
    private LocalizationService? _texts;
    private UIElement? _titleBar;
    private Button? _systemMenuButton;
    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;

    public static readonly DependencyProperty TitleBarContentProperty =
        DependencyProperty.Register(
            nameof(TitleBarContent),
            typeof(object),
            typeof(AppWindow),
            new PropertyMetadata(null, OnChromeContentChanged));

    public static readonly DependencyProperty TitleBarCenterProperty =
        DependencyProperty.Register(
            nameof(TitleBarCenter),
            typeof(object),
            typeof(AppWindow),
            new PropertyMetadata(null, OnChromeContentChanged));

    // The shell can reclaim title-bar space without affecting other AppWindow surfaces.
    public static readonly DependencyProperty ShowBrandNameProperty =
        DependencyProperty.Register(nameof(ShowBrandName), typeof(bool), typeof(AppWindow),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsImmersiveFullScreenProperty =
        DependencyProperty.Register(nameof(IsImmersiveFullScreen), typeof(bool), typeof(AppWindow),
            new PropertyMetadata(false, (d, _) => ((AppWindow)d).UpdateChromeMetrics()));

    public static readonly DependencyProperty MaximizedPaddingProperty =
        DependencyProperty.Register(
            nameof(MaximizedPadding),
            typeof(Thickness),
            typeof(AppWindow),
            new PropertyMetadata(new Thickness(0)));

    public AppWindow()
    {
        _chrome = new WindowChrome
        {
            CaptionHeight = TitleBarHeight + 1,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, _chrome);
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        StateChanged += (_, _) =>
        {
            UpdateChromeMetrics();
            UpdateChromeText();
        };
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                UpdateChromeMetrics();
        };
    }

    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public object? TitleBarCenter
    {
        get => GetValue(TitleBarCenterProperty);
        set => SetValue(TitleBarCenterProperty, value);
    }

    public bool ShowBrandName
    {
        get => (bool)GetValue(ShowBrandNameProperty);
        set => SetValue(ShowBrandNameProperty, value);
    }

    public bool IsImmersiveFullScreen
    {
        get => (bool)GetValue(IsImmersiveFullScreenProperty);
        set => SetValue(IsImmersiveFullScreenProperty, value);
    }

    public Thickness MaximizedPadding
    {
        get => (Thickness)GetValue(MaximizedPaddingProperty);
        set => SetValue(MaximizedPaddingProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DetachChromeButtons();
        _titleBar = GetTemplateChild("TitleBar") as UIElement;
        _systemMenuButton = GetTemplateChild("SystemMenuButton") as Button;
        _minimizeButton = GetTemplateChild("MinimizeButton") as Button;
        _maximizeButton = GetTemplateChild("MaximizeButton") as Button;
        _closeButton = GetTemplateChild("CloseButton") as Button;
        if (_titleBar != null)
            _titleBar.MouseRightButtonUp += TitleBar_MouseRightButtonUp;

        if (_systemMenuButton != null)
        {
            _systemMenuButton.Click += SystemMenu_Click;
            _systemMenuButton.PreviewMouseLeftButtonDown += SystemMenu_PreviewMouseLeftButtonDown;
        }

        if (_minimizeButton != null)
            _minimizeButton.Click += Minimize_Click;
        if (_maximizeButton != null)
            _maximizeButton.Click += Maximize_Click;
        if (_closeButton != null)
            _closeButton.Click += Close_Click;

        UpdateChromeText();
        RefreshChromeHitTest();
    }

    public void UseTexts(LocalizationService texts)
    {
        if (_texts != null)
            _texts.PropertyChanged -= OnTextsChanged;
        _texts = texts;
        ApplyUiLanguage();
        _texts.PropertyChanged += OnTextsChanged;
        UpdateChromeText();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WindowMessageHook);
        ApplyCornerPreference();
        UpdateChromeMetrics();
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        if (_texts != null)
            _texts.PropertyChanged -= OnTextsChanged;
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            ShowSystemMenuAtDevicePoint(PointToScreen(new Point(8, TitleBarHeight)));
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmDpiChanged or WmDisplayChange)
        {
            // Let WPF handle WM_DPICHANGED and its suggested bounds. Refresh
            // only our custom chrome AFTER the new monitor DPI/layout is applied.
            Dispatcher.BeginInvoke(UpdateChromeMetrics, DispatcherPriority.Loaded);
        }
        return IntPtr.Zero;
    }

    private void ToggleMaximize()
    {
        if (ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
            return;

        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void UpdateChromeMetrics()
    {
        if (IsImmersiveFullScreen)
        {
            MaximizedPadding = new Thickness(0);
            _chrome.CaptionHeight = 0;
            _chrome.ResizeBorderThickness = new Thickness(0);
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            MaximizedPadding = GetMaximizedInset();
            _chrome.CaptionHeight = MaximizedPadding.Top + TitleBarHeight;
            _chrome.ResizeBorderThickness = new Thickness(0);
        }
        else
        {
            MaximizedPadding = new Thickness(0);
            _chrome.CaptionHeight = TitleBarHeight + 1;
            _chrome.ResizeBorderThickness = new Thickness(6);
        }
    }

    private void UpdateChromeText()
    {
        if (_texts is null)
            return;

        SetChromeText(_systemMenuButton, "Window_SystemMenu");
        SetChromeText(_minimizeButton, "Window_Minimize");
        SetChromeText(_closeButton, "Window_Close");
        SetChromeText(_maximizeButton, WindowState == WindowState.Maximized ? "Window_Restore" : "Window_Maximize");
    }

    private void SetChromeText(Button? button, string key)
    {
        if (button is null || _texts is null)
            return;
        var label = _texts[key];
        button.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationService.CurrentLanguage))
            ApplyUiLanguage();

        UpdateChromeText();
    }

    private void ApplyUiLanguage()
    {
        if (_texts is null)
            return;

        // WPF uses FrameworkElement.Language when selecting glyphs from
        // GlobalUserInterface.CompositeFont (e.g. Microsoft YaHei UI for zh-CN).
        Language = XmlLanguage.GetLanguage(_texts.CurrentLanguage);
    }

    private void SystemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_systemMenuButton is null)
            return;
        // Anchor the native menu immediately below the icon, aligned to its left edge.
        var origin = _systemMenuButton.PointToScreen(new Point(0, _systemMenuButton.ActualHeight));
        ShowSystemMenuAtDevicePoint(origin);
    }

    private void ShowSystemMenuAtDevicePoint(Point deviceScreenLocation)
    {
        // PointToScreen returns physical pixels. SystemCommands.ShowSystemMenu takes
        // logical screen coordinates and applies the window DPI scale internally.
        // Convert exactly once to avoid misplaced menus on high-DPI / mixed-DPI displays.
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;

        var logicalScreenLocation = DeviceToLogicalScreenPoint(deviceScreenLocation, target.TransformFromDevice);
        SystemCommands.ShowSystemMenu(this, logicalScreenLocation);
    }

    internal static Point DeviceToLogicalScreenPoint(Point deviceScreenLocation, Matrix transformFromDevice) =>
        transformFromDevice.Transform(deviceScreenLocation);

    private void SystemMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;
        e.Handled = true;
        SystemCommands.CloseWindow(this);
    }

    private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            return;
        ShowSystemMenuAtDevicePoint(PointToScreen(e.GetPosition(this)));
    }

    public void RefreshChromeHitTest() =>
        Dispatcher.BeginInvoke(ApplyChromeHitTest, DispatcherPriority.Loaded);

    private void ApplyChromeHitTest()
    {
        // Buttons opt into client hits. Empty caption space stays draggable.
        if (GetTemplateChild("TitleExtras") is DependencyObject extras)
            MarkHitTestVisible(extras);
        if (GetTemplateChild("TitleCenter") is DependencyObject center)
            MarkInteractiveChrome(center);
    }

    private static void OnChromeContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppWindow window)
            window.RefreshChromeHitTest();
    }

    private static void MarkInteractiveChrome(DependencyObject node)
    {
        if (node is Button or TextBoxBase or ComboBox)
            WindowChrome.SetIsHitTestVisibleInChrome((IInputElement)node, true);
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            MarkInteractiveChrome(VisualTreeHelper.GetChild(node, i));
    }

    private static void MarkHitTestVisible(DependencyObject node)
    {
        if (node is IInputElement input)
            WindowChrome.SetIsHitTestVisibleInChrome(input, true);
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            MarkHitTestVisible(VisualTreeHelper.GetChild(node, i));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void DetachChromeButtons()
    {
        if (_titleBar != null)
            _titleBar.MouseRightButtonUp -= TitleBar_MouseRightButtonUp;
        if (_systemMenuButton != null)
        {
            _systemMenuButton.Click -= SystemMenu_Click;
            _systemMenuButton.PreviewMouseLeftButtonDown -= SystemMenu_PreviewMouseLeftButtonDown;
        }

        if (_minimizeButton != null)
            _minimizeButton.Click -= Minimize_Click;
        if (_maximizeButton != null)
            _maximizeButton.Click -= Maximize_Click;
        if (_closeButton != null)
            _closeButton.Click -= Close_Click;
    }

    private void ApplyCornerPreference()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    private Thickness GetMaximizedInset()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return new Thickness(7);

        // Maximized windows extend past the monitor. Pad to the work area, not the monitor,
        // so the caption and the taskbar are both fully on screen.
        if (!GetWindowRect(hwnd, out var window))
            return new Thickness(7);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Thickness(
            Math.Max(0, (info.Work.Left - window.Left) / dpi.DpiScaleX),
            Math.Max(0, (info.Work.Top - window.Top) / dpi.DpiScaleY),
            Math.Max(0, (window.Right - info.Work.Right) / dpi.DpiScaleX),
            Math.Max(0, (window.Bottom - info.Work.Bottom) / dpi.DpiScaleY));
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectBounds lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

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
