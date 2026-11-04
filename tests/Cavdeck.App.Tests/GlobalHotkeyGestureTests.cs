using System.Windows.Input;
using Cavdeck.App.Services;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class GlobalHotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+C", "Ctrl+Alt+C", 0x0043u, 0x0003u)]
    [InlineData("ctrl + shift + 0", "Ctrl+Shift+0", 0x0030u, 0x0006u)]
    [InlineData("Ctrl+Alt+Shift+F11", "Ctrl+Alt+Shift+F11", 0x007Au, 0x0007u)]
    [InlineData("Control+Shift+A", "Ctrl+Shift+A", 0x0041u, 0x0006u)]
    public void Canonical_combinations_are_parsed_to_windows_virtual_keys(
        string input, string canonical, uint virtualKey, uint modifiers)
    {
        Assert.True(GlobalHotkeyGesture.TryParse(input, out var parsed));
        Assert.Equal(canonical, parsed.Text);
        Assert.Equal(virtualKey, parsed.VirtualKey);
        Assert.Equal(modifiers, parsed.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+C")]
    [InlineData("Alt+C")]
    [InlineData("Win+Alt+C")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl++Alt+C")]
    [InlineData("Ctrl+Ctrl+C")]
    [InlineData("Ctrl+Alt+F12")]
    [InlineData("Ctrl+Shift+T")]
    [InlineData("Ctrl+Alt+W")]
    [InlineData("Ctrl+Shift+B")]
    [InlineData("Ctrl+Alt+7")]
    [InlineData("Ctrl+Shift+Delete")]
    [InlineData("Ctrl+Alt+F2")]
    [InlineData("Ctrl+Shift+F4")]
    [InlineData("Ctrl+Alt+F5")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Alt+Ctrl")]
    public void Unmodified_system_and_reserved_keys_are_never_accepted(string gesture) =>
        Assert.False(GlobalHotkeyGesture.TryParse(gesture, out _));

    [Fact]
    public void Capture_uses_keyboard_modifiers_and_does_not_accept_windows_key()
    {
        Assert.True(GlobalHotkeyGesture.TryCapture(Key.F6,
            ModifierKeys.Control | ModifierKeys.Shift, out var gesture));
        Assert.Equal("Ctrl+Shift+F6", gesture.Text);

        Assert.True(GlobalHotkeyGesture.TryCapture(Key.D0,
            ModifierKeys.Control | ModifierKeys.Alt, out gesture));
        Assert.Equal("Ctrl+Alt+0", gesture.Text);

        Assert.False(GlobalHotkeyGesture.TryCapture(Key.B,
            ModifierKeys.Windows | ModifierKeys.Control | ModifierKeys.Alt, out _));
        Assert.False(GlobalHotkeyGesture.TryCapture(Key.F12,
            ModifierKeys.Control | ModifierKeys.Alt, out _));
    }

    [Fact]
    public void Existing_preferences_keep_global_hotkey_opt_in()
    {
        var prefs = new UserPreferences();
        prefs.Normalize();
        Assert.False(prefs.GlobalHotkeyEnabled);
        Assert.Equal(GlobalHotkeyGesture.Default, prefs.GlobalHotkeyGesture);

        prefs.GlobalHotkeyGesture = "";
        prefs.Normalize();
        Assert.Equal(GlobalHotkeyGesture.Default, prefs.GlobalHotkeyGesture);
    }
}
