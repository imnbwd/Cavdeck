using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cavdeck.App.Services;

namespace Cavdeck.App.Features.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void CaptureHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || !settings.CanEditGlobalHotkey)
            return;
        settings.BeginGlobalHotkeyCapture();
        Keyboard.Focus((Button)sender);
    }

    private void CaptureHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || !settings.IsCapturingGlobalHotkey)
            return;
        if (e.IsRepeat)
        {
            e.Handled = true;
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            settings.CancelGlobalHotkeyCapture();
            e.Handled = true;
            return;
        }
        // Tab must always be able to leave the recorder and resume normal navigation.
        if (key == Key.Tab)
        {
            settings.CancelGlobalHotkeyCapture();
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        if (!GlobalHotkeyGesture.TryCapture(key, Keyboard.Modifiers, out var shortcut))
        {
            settings.ReportInvalidGlobalHotkey();
            return;
        }

        settings.CancelGlobalHotkeyCapture();
        _ = settings.SetGlobalHotkeyGestureAsync(shortcut.Text);
    }

    private void CaptureHotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
            settings.CancelGlobalHotkeyCapture();
    }
}
