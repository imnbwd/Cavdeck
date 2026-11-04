using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cavdeck.App.Controls;

// Keyboard focus draws an inset ring. A pointer click already has hover and selection, so it does not.
public static class FocusCue
{
    private static DependencyObject? _current;
    private static DependencyObject? _focusSource;
    private static bool _fromKeyboard;

    public static readonly DependencyProperty VisibleProperty =
        DependencyProperty.RegisterAttached(
            "Visible",
            typeof(bool),
            typeof(FocusCue),
            new FrameworkPropertyMetadata(false));

    public static bool GetVisible(DependencyObject element) => (bool)element.GetValue(VisibleProperty);

    public static void SetVisible(DependencyObject element, bool value) => element.SetValue(VisibleProperty, value);

    // Opt-in for file lists: keep actual keyboard focus on DataGridCell so native
    // arrows and multi-selection work, but paint the cue on the containing row.
    public static readonly DependencyProperty RowFocusScopeProperty =
        DependencyProperty.RegisterAttached(
            "RowFocusScope", typeof(bool), typeof(FocusCue), new PropertyMetadata(false));

    public static bool GetRowFocusScope(DependencyObject element) =>
        (bool)element.GetValue(RowFocusScopeProperty);

    public static void SetRowFocusScope(DependencyObject element, bool value) =>
        element.SetValue(RowFocusScopeProperty, value);

    public static void Install()
    {
        EventManager.RegisterClassHandler(
            typeof(UIElement),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(UIElement),
            UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(UIElement),
            UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(UIElement),
            UIElement.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus),
            handledEventsToo: true);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsNavigationKey(e.Key) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            _fromKeyboard = true;
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _fromKeyboard = false;
        HideCurrent();
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender != e.NewFocus || sender is not DependencyObject focused)
            return;
        HideCurrent();
        if (!_fromKeyboard)
            return;
        _current = ResolveCueTarget(focused);
        _focusSource = focused;
        SetVisible(_current, true);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender != e.OldFocus || sender is not DependencyObject focused)
            return;
        if (ReferenceEquals(_focusSource, focused))
            HideCurrent();
    }

    private static void HideCurrent()
    {
        if (_current is not null)
            SetVisible(_current, false);
        _current = null;
        _focusSource = null;
    }

    private static DependencyObject ResolveCueTarget(DependencyObject focused)
    {
        if (focused is not DataGridCell && focused is not DataGridRow)
            return focused;

        DataGridRow? row = focused as DataGridRow;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(focused);
             parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is DataGridRow ancestorRow)
                row = ancestorRow;
            if (parent is DataGrid grid)
                return row is not null && GetRowFocusScope(grid) ? row : focused;
        }
        return focused;
    }

    private static bool IsNavigationKey(Key key) => key is
        Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right
        or Key.Enter or Key.Space or Key.Escape
        or Key.PageUp or Key.PageDown or Key.Home or Key.End;
}
