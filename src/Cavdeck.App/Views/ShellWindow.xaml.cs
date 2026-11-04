using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.ViewModels;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Services;
using Cavdeck.Application.Shell;
using Cavdeck.Application.Abstractions;

namespace Cavdeck.App.Views;

public partial class ShellWindow : AppWindow
{
    public static readonly DependencyProperty TabItemWidthProperty =
        DependencyProperty.Register(nameof(TabItemWidth), typeof(double), typeof(ShellWindow),
            new PropertyMetadata(TabStripLayout.MaxTabWidth));

    public double TabItemWidth
    {
        get => (double)GetValue(TabItemWidthProperty);
        set => SetValue(TabItemWidthProperty, value);
    }

    private readonly ShellViewModel _viewModel;
    private Border? _transitionCover;
    private bool _ready;
    private bool _closing;
    private bool _checkingExit;
    private int _sidebarAnimation;
    private ShellTabViewModel? _dragTab;
    private Point? _dragStart;
    private bool _dragging;
    private bool _fittingTabStrip;
    private bool _activeScrollQueued;
    private double? _closingTabWidth;
    private bool _sidebarResetFromDoubleClick;
    private NativeRestoredWindow? _pendingPhysicalPlacement;
    private ScreenArea? _lastNormalPhysicalBounds;

    public bool PersistLayout { get; set; } = true;

    private readonly ApplicationLifetimeCoordinator _lifetime;

    public ShellWindow(ShellViewModel viewModel, ToastService toasts, ApplicationLifetimeCoordinator lifetime)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _lifetime = lifetime;
        DataContext = viewModel;
        Toasts.DataContext = toasts;
        UseTexts(viewModel.Texts);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Tabs.CollectionChanged += OnTabsCollectionChanged;
        ContentRendered += OnContentRendered;
        LocationChanged += (_, _) => RememberNormalPhysicalBounds();
        SizeChanged += (_, e) =>
        {
            RememberNormalPhysicalBounds();
            if (e.WidthChanged)
            {
                _closingTabWidth = null;
                ShowBrandName = ActualWidth >= 940;
                var compact = ActualWidth > 0 && ActualWidth < 940;
                _viewModel.SetAutoCompactSidebar(compact);
            }
        };
        Loaded += (_, _) =>
        {
            _ready = true;
            RememberNormalPhysicalBounds();
            ShowBrandName = ActualWidth >= 940;
            FitTabStrip();
            ScrollActiveIntoView();
            RefreshChromeHitTest();
        };
        ApplySidebar(animate: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_pendingPhysicalPlacement is not { } placement)
            return;
        _pendingPhysicalPlacement = null;
        if (!WindowScreenCoordinates.TryPlace(this, placement))
            DiagnosticLog.Current.Queue("window-position",
                new InvalidOperationException("Failed to apply native window position."));
        if (placement.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    public void UseDesignLayout()
    {
        PersistLayout = false;
        if (_transitionCover is not null)
        {
            _transitionCover.Opacity = 0;
            _transitionCover.Visibility = Visibility.Collapsed;
        }
        _viewModel.UseDesignLayout();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _transitionCover = GetTemplateChild("WindowTransitionCover") as Border;
        if (_transitionCover is not null)
        {
            var showCover = PersistLayout && Motion.AnimationsEnabled;
            _transitionCover.Opacity = showCover ? 1 : 0;
            _transitionCover.Visibility = showCover ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        if (!PersistLayout || _closing || _transitionCover is null)
            return;

        var duration = WindowMotionDuration("MotionEnter", TimeSpan.FromMilliseconds(150));
        _transitionCover.Opacity = 0;
        if (duration == TimeSpan.Zero)
        {
            _transitionCover.Visibility = Visibility.Collapsed;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => _transitionCover.Visibility = Visibility.Collapsed;
        _transitionCover.BeginAnimation(OpacityProperty, animation);
    }

    public void RestoreLayout()
    {
        // Old WPF Left/Top were per-monitor DIPs; do not reinterpret them as
        // global desktop pixels. Old settings safely migrate to centered placement.
        var monitors = MonitorWorkAreas.Capture();
        if (monitors.Count == 0)
        {
            _viewModel.Layout.Clamp();
            Width = _viewModel.Layout.Width;
            Height = _viewModel.Layout.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (_viewModel.Layout.IsMaximized)
                WindowState = WindowState.Maximized;
            return;
        }

        var placement = NativeWindowPlacement.Resolve(_viewModel.Layout, monitors);
        MinWidth = Math.Min(WindowLayout.CompactMinWidth, placement.Width);
        MinHeight = Math.Min(WindowLayout.CompactMinHeight, placement.Height);
        Width = placement.Width;
        Height = placement.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _pendingPhysicalPlacement = placement;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
            return;
        // The shortcut recorder owns the next keystroke, including Esc and Ctrl+T.
        if (_viewModel.Settings.IsCapturingGlobalHotkey)
            return;
        if (e.Key == Key.Escape && _viewModel.IsStorageMenuOpen)
        {
            _viewModel.CloseStorageMenu();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && (_viewModel.ReturnFromSettings() || _viewModel.ReturnToConnectionList()))
        {
            e.Handled = true;
            return;
        }

        // Tab navigation remains available even when the current file editor has focus.
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (!ctrl)
            return;
        if (e.Key == Key.T && shift)
            _viewModel.RestoreClosedTab();
        else if (e.Key == Key.T)
            _viewModel.NewTabCommand.Execute(null);
        else if (e.Key is Key.W or Key.F4)
            _viewModel.CloseActive();
        else if (e.Key == Key.Tab)
            _viewModel.Cycle(shift ? -1 : 1);
        else if (IsTyping())
            return;
        else if (e.Key == Key.B)
            _viewModel.ToggleSidebar();
        else if (e.Key == Key.D9 || e.Key == Key.NumPad9)
            _viewModel.ActivateIndex(_viewModel.Tabs.Count - 1);
        else if (e.Key is >= Key.D1 and <= Key.D8)
            _viewModel.ActivateIndex(e.Key - Key.D1);
        else if (e.Key is >= Key.NumPad1 and <= Key.NumPad8)
            _viewModel.ActivateIndex(e.Key - Key.NumPad1);
        else
            return;
        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !PersistLayout)
            return;
        e.Cancel = true;
        if (!_closing)
            _lifetime.HandleWindowClose();
    }

    public void HideToTray()
    {
        if (!IsVisible) return;
        SaveWindowBounds();
        ShowInTaskbar = false;
        Hide();
    }

    public void RestoreFromTray()
    {
        if (!IsVisible)
        {
            // The HWND still exists while hidden. Revalidate the position using
            // physical monitor work areas in case the display setup changed.
            var monitors = MonitorWorkAreas.Capture();
            var wasMaximized = WindowState == WindowState.Maximized;
            if (monitors.Count > 0)
            {
                if (wasMaximized)
                    WindowState = WindowState.Normal;
                var placement = NativeWindowPlacement.Resolve(_viewModel.Layout, monitors);
                MinWidth = Math.Min(WindowLayout.CompactMinWidth, placement.Width);
                MinHeight = Math.Min(WindowLayout.CompactMinHeight, placement.Height);
                Width = placement.Width;
                Height = placement.Height;
                if (!WindowScreenCoordinates.TryPlace(this, placement))
                    DiagnosticLog.Current.Queue("window-position",
                        new InvalidOperationException("Failed to restore native window position."));
                if (wasMaximized)
                    WindowState = WindowState.Maximized;
            }
            ShowInTaskbar = true;
            Show();
        }
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    public async void BeginApplicationExit() => await ExitCoreAsync(null);

    public async void BeginUpdateRestart(IUpdateService updater) => await ExitCoreAsync(updater);

    private async Task ExitCoreAsync(IUpdateService? updater)
    {
        if (_closing || _checkingExit)
            return;
        _checkingExit = true;
        try
        {
            // Reuse all editor/preview discard prompts before touching the updater.
            if (!await _viewModel.CanExitAsync())
            {
                _lifetime.CancelExit();
                return;
            }

            if (updater is not null && (_lifetime.HasUnfinishedTransfers || !updater.HasPendingUpdate))
            {
                _lifetime.CancelExit();
                MessageBox.Show(this, _viewModel.Texts["Update_TransfersMessage"],
                    _viewModel.Texts["Update_TransfersTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _closing = true;
            SaveWindowBounds();
            _ = Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await _viewModel.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Current.Queue("window-close", ex);
                    // Unlike ordinary exit, an update must not proceed with unsaved state.
                    if (updater is not null)
                    {
                        _closing = false;
                        _lifetime.CancelExit();
                        MessageBox.Show(this, _viewModel.Texts["Update_SaveFailed"],
                            _viewModel.Texts["Update_RestartTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                try
                {
                    var stopped = await _lifetime.StopServicesAsync();
                    if (updater is not null)
                    {
                        // Services are no longer usable; even on a failure we must exit
                        // rather than leave a half-stopped app on screen.
                        if (!stopped)
                            throw new InvalidOperationException("Services did not stop cleanly.");
                        updater.ApplyPendingAndRestart();
                    }
                    FadeOutAndClose();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Current.Queue("window-close", ex);
                    if (updater is not null)
                        MessageBox.Show(this, _viewModel.Texts["Update_InstallFailed"],
                            _viewModel.Texts["Update_RestartTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
                    CompleteClose();
                }
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Queue("window-close", ex);
            _lifetime.CancelExit();
        }
        finally
        {
            _checkingExit = false;
        }
    }

    public void BypassCloseForSystem() => PersistLayout = false;

    private void SaveWindowBounds()
    {
        if (WindowState != WindowState.Minimized && !_viewModel.EffectiveSidebarCollapsed && !_viewModel.IsAutoCompactSidebar)
            _viewModel.SetSidebarWidth(SidebarColumn.ActualWidth);
        CaptureBounds();
    }

    private void FadeOutAndClose()
    {
        var duration = WindowMotionDuration("MotionExit", TimeSpan.FromMilliseconds(100));
        if (duration == TimeSpan.Zero || _transitionCover is null)
        {
            CompleteClose();
            return;
        }

        var animation = new DoubleAnimation
        {
            From = _transitionCover.Opacity,
            To = 1,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) => CompleteClose();
        _transitionCover.Visibility = Visibility.Visible;
        _transitionCover.BeginAnimation(OpacityProperty, animation);
    }

    private void CompleteClose()
    {
        PersistLayout = false;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private TimeSpan WindowMotionDuration(string resourceKey, TimeSpan fallback) =>
        Motion.Duration(TryFindResource(resourceKey) is Duration duration && duration.HasTimeSpan
            ? duration.TimeSpan
            : fallback);

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Tabs.CollectionChanged -= OnTabsCollectionChanged;
        base.OnClosed(e);
    }

    private void CaptureBounds()
    {
        if (WindowState == WindowState.Minimized)
            return;

        var bounds = WindowState == WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);
        if (bounds.Width < 100 || bounds.Height < 100 ||
            !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top))
            return;

        _viewModel.CaptureBounds(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized);
        RememberNormalPhysicalBounds();
        if (_lastNormalPhysicalBounds is { } native)
        {
            _viewModel.Layout.PhysicalLeft = native.Left;
            _viewModel.Layout.PhysicalTop = native.Top;
        }
    }

    private void RememberNormalPhysicalBounds()
    {
        if (!PersistLayout || !IsVisible || !IsLoaded || WindowState != WindowState.Normal)
            return;
        if (WindowScreenCoordinates.TryRead(this, out var physical))
            _lastNormalPhysicalBounds = physical;
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A pointer close holds tab widths briefly; adding or reordering tabs resets this hold.
        if (e.Action != NotifyCollectionChangedAction.Remove)
            _closingTabWidth = null;
        RefreshChromeHitTest();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded)
                return;
            FitTabStrip();
            ScrollActiveIntoView();
        }), DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SidebarColumnWidth))
            ApplySidebar(animate: _ready);
        else if (e.PropertyName == nameof(ShellViewModel.ActiveTab))
            ScrollActiveIntoView();
    }

    private void ApplySidebar(bool animate)
    {
        var generation = ++_sidebarAnimation;
        var collapsed = _viewModel.EffectiveSidebarCollapsed;
        var target = collapsed ? WindowLayout.CollapsedSidebarWidth : _viewModel.SidebarColumnWidth;
        SidebarSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SplitterColumn.Width = new GridLength(collapsed ? 0 : 5);

        var duration = Motion.Duration(TimeSpan.FromMilliseconds(160));
        if (!animate || duration == TimeSpan.Zero || Math.Abs(SidebarColumn.ActualWidth - target) < 0.5)
        {
            FinishSidebar(target, collapsed);
            return;
        }

        // Keep the wide range until the animation ends so the column can shrink or grow.
        SidebarColumn.MinWidth = WindowLayout.CollapsedSidebarWidth;
        SidebarColumn.MaxWidth = WindowLayout.MaxSidebarWidth;
        var animation = new GridLengthAnimation
        {
            From = new GridLength(SidebarColumn.ActualWidth),
            To = new GridLength(target),
            Duration = duration
        };
        var clock = animation.CreateClock();
        clock.Completed += (_, _) =>
        {
            if (generation != _sidebarAnimation)
                return;
            FinishSidebar(target, collapsed);
        };
        SidebarColumn.ApplyAnimationClock(ColumnDefinition.WidthProperty, clock);
    }

    private void FinishSidebar(double target, bool collapsed)
    {
        SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        SidebarColumn.Width = new GridLength(target);
        SidebarColumn.MinWidth = collapsed ? WindowLayout.CollapsedSidebarWidth : WindowLayout.MinSidebarWidth;
        SidebarColumn.MaxWidth = collapsed ? WindowLayout.CollapsedSidebarWidth : WindowLayout.MaxSidebarWidth;
    }

    private void SidebarSplitter_DragStarted(object sender, DragStartedEventArgs e)
    {
        _sidebarAnimation++;
        SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        SidebarColumn.MinWidth = WindowLayout.MinSidebarWidth;
        SidebarColumn.MaxWidth = WindowLayout.MaxSidebarWidth;
        SidebarColumn.Width = new GridLength(Math.Clamp(
            SidebarColumn.ActualWidth, WindowLayout.MinSidebarWidth, WindowLayout.MaxSidebarWidth));
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_sidebarResetFromDoubleClick)
        {
            _sidebarResetFromDoubleClick = false;
            return;
        }

        _viewModel.SetSidebarWidth(SidebarColumn.ActualWidth);
    }

    private void SidebarSplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _sidebarResetFromDoubleClick = true;
        _viewModel.ResetSidebarWidth();
        e.Handled = true;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pageId })
            return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            _viewModel.OpenPage(pageId);
        else
            _viewModel.Navigate(pageId);
    }

    private void Sidebar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        if (NavIdFrom(e.OriginalSource as DependencyObject) is not string pageId)
            return;
        _viewModel.OpenPage(pageId);
        e.Handled = true;
    }

    private void StorageConnection_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var openNewTab = e.ChangedButton == MouseButton.Middle
            || (e.ChangedButton == MouseButton.Left
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
        if (!openNewTab || sender is not Button { Tag: Guid id })
            return;

        _viewModel.OpenConnectionNewTabCommand.Execute(id);
        e.Handled = true;
    }

    private void TabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCloseButton(e.OriginalSource as DependencyObject))
            return;
        if (TabFrom(e.OriginalSource as DependencyObject) is not ShellTabViewModel tab)
            return;
        _dragTab = tab;
        _dragStart = e.GetPosition(TabList);
        _dragging = false;
        _viewModel.Activate(tab);
    }

    private void TabList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed || _dragStart is null)
            return;

        var position = e.GetPosition(TabList);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.Value.X) < 6)
                return;
            _dragging = true;
            TabList.CaptureMouse();
        }

        _viewModel.MoveTab(_dragTab, IndexAt(position));
        e.Handled = true;
    }

    private void TabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void TabList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        if (TabFrom(e.OriginalSource as DependencyObject) is not ShellTabViewModel tab)
            return;
        _viewModel.CloseTab(tab);
        e.Handled = true;
    }

    private void TabList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0)
            return;
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void TabCloseButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _closingTabWidth = TabItemWidth;
    }

    private void TabList_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_closingTabWidth is null || _dragging)
            return;
        _closingTabWidth = null;
        FitTabStrip();
    }

    private void TabDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ShellTabViewModel tab)
            _viewModel.DuplicateTab(tab);
    }

    private void TabCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ShellTabViewModel tab)
            _viewModel.CloseOtherTabs(tab);
    }

    private void TabMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.Items.Count < 3)
            return;
        if (menu.Items[1] is MenuItem closeOthers)
            closeOthers.IsEnabled = _viewModel.Tabs.Count > 1;
        if (menu.Items[2] is MenuItem restore)
            restore.IsEnabled = _viewModel.CanRestoreClosedTab;
    }

    private void TabRestoreClosed_Click(object sender, RoutedEventArgs e) => _viewModel.RestoreClosedTab();

    private void TabHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitTabStrip();
        ScrollActiveIntoView();
    }

    private void FitTabStrip()
    {
        if (_fittingTabStrip || TabHost.ActualWidth <= 0)
            return;

        _fittingTabStrip = true;
        try
        {
            var addWidth = NewTabButton.ActualWidth + NewTabButton.Margin.Left + NewTabButton.Margin.Right;
            var layout = TabStripLayout.Calculate(TabHost.ActualWidth, addWidth, _viewModel.Tabs.Count);
            var desiredWidth = TabStripLayout.WidthWhileClosing(layout, _closingTabWidth);
            if (Math.Abs(TabItemWidth - desiredWidth) > 0.5)
                TabItemWidth = desiredWidth;

            var overflowVisibility = layout.ShowOverflow ? Visibility.Visible : Visibility.Collapsed;
            if (TabOverflowButton.Visibility != overflowVisibility)
                TabOverflowButton.Visibility = overflowVisibility;
            if (Math.Abs(TabScroller.MaxWidth - layout.ViewportWidth) > 0.5)
                TabScroller.MaxWidth = layout.ViewportWidth;
        }
        finally
        {
            _fittingTabStrip = false;
        }
    }

    private void TabOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = TabOverflowButton,
            Placement = PlacementMode.Bottom,
            MaxWidth = 420,
            MaxHeight = 640
        };
        foreach (var tab in _viewModel.Tabs)
        {
            var header = new StackPanel { MaxWidth = 330 };
            header.Children.Add(new TextBlock
            {
                Text = tab.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 320
            });
            if (tab.FullTitle != tab.Title)
            {
                var location = new TextBlock
                {
                    Text = tab.FullTitle,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 320,
                    FontSize = 11
                };
                location.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                header.Children.Add(location);
            }
            var item = new MenuItem
            {
                Header = header,
                ToolTip = tab.FullTitle,
                IsCheckable = true,
                IsChecked = tab.IsActive
            };
            item.Click += (_, _) => _viewModel.Activate(tab);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void EndDrag()
    {
        _dragTab = null;
        _dragStart = null;
        _dragging = false;
        if (TabList.IsMouseCaptured)
            TabList.ReleaseMouseCapture();
    }

    private void ScrollActiveIntoView()
    {
        if (_activeScrollQueued)
            return;

        _activeScrollQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _activeScrollQueued = false;
            if (!IsLoaded || _viewModel.ActiveTab is null || TabScroller.ViewportWidth <= 0)
                return;
            if (TabList.ItemContainerGenerator.ContainerFromItem(_viewModel.ActiveTab) is not FrameworkElement element
                || !element.IsLoaded)
                return;

            var bounds = element.TransformToAncestor(TabScroller)
                .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
            if (bounds.Left < -0.5)
                TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset + bounds.Left);
            else if (bounds.Right > TabScroller.ViewportWidth + 0.5)
                TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset + bounds.Right - TabScroller.ViewportWidth);
        }), DispatcherPriority.Loaded);
    }

    private int IndexAt(Point position)
    {
        var last = Math.Max(0, TabList.Items.Count - 1);
        for (var i = 0; i < TabList.Items.Count; i++)
        {
            if (TabList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement element)
                continue;
            var origin = element.TranslatePoint(new Point(0, 0), TabList);
            if (position.X < origin.X + element.ActualWidth / 2)
                return i;
        }

        return last;
    }

    private static bool IsTyping() =>
        Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox;

    private static bool IsCloseButton(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Button { Tag: string tag } && tag == "close")
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static ShellTabViewModel? TabFrom(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is FrameworkElement { DataContext: ShellTabViewModel tab })
                return tab;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static string? NavIdFrom(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Button { Tag: string tag } && ShellPages.TryParse(tag, out _))
                return tag;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void StorageNavButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsStorageMenuOpen)
            return;

        _viewModel.CloseStorageMenu();
        e.Handled = true;
    }

    private void StorageMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsStorageMenuOpen)
            return;

        if (e.Key == Key.Escape)
        {
            _viewModel.CloseStorageMenu();
            StorageNavButton.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Up or Key.Down))
            return;
        var buttons = EnumerateButtons(StorageMenuList).Where(button => button.IsEnabled).ToArray();
        if (buttons.Length == 0)
            return;
        var current = Array.FindIndex(buttons, button => button.IsKeyboardFocused);
        var next = e.Key == Key.Down
            ? (current + 1) % buttons.Length
            : (current <= 0 ? buttons.Length - 1 : current - 1);
        buttons[next].Focus();
        e.Handled = true;
    }

    private void StorageMenuRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        StorageMenu.StaysOpen = true;
    }

    private void StorageMenuRowMenu_Closed(object sender, RoutedEventArgs e)
    {
        StorageMenu.StaysOpen = false;
    }

    private void StorageMenu_Opened(object sender, EventArgs e)
    {
        StorageMenuList.UpdateLayout();
        Button? first = null;
        Button? current = null;
        foreach (var button in EnumerateButtons(StorageMenuList))
        {
            first ??= button;
            if (button.Tag is Guid id && _viewModel.ActiveConnectionId == id)
                current = button;
        }
        if (current is not null || first is not null)
            (current ?? first)!.Focus();
        else
            StorageMenuAddButton.Focus();
    }

    private void SidebarConnectionMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Button primary } more || primary.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = more;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -4;
        menu.VerticalOffset = -4;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static IEnumerable<Button> EnumerateButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Button button)
                yield return button;
            foreach (var nested in EnumerateButtons(child))
                yield return nested;
        }
    }
}
