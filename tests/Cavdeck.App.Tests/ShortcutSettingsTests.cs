using Cavdeck.App.Features.Updates;
using System.IO;
using System.Xml.Linq;
using Cavdeck.App.Features.Settings;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ShortcutSettingsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Reference_only_lists_currently_supported_contextual_shortcuts()
    {
        var groups = ShortcutCatalog.Create(new LocalizationService());
        Assert.Equal(3, groups.Count);
        Assert.Equal(8, groups[0].Items.Count);
        Assert.Equal(10, groups[1].Items.Count);
        Assert.Equal(2, groups[2].Items.Count);
        Assert.All(groups, group =>
        {
            Assert.False(string.IsNullOrWhiteSpace(group.Title));
            Assert.All(group.Items, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Action));
                Assert.False(string.IsNullOrWhiteSpace(item.Keys));
            });
        });
        Assert.Contains(groups[0].Items, item => item.Keys == "Ctrl + Shift + Tab");
        Assert.Contains(groups[1].Items, item => item.Keys == "Ctrl + V");
        Assert.Contains(groups[1].Items, item => item.Keys == "Ctrl + G"
            && item.Action == new LocalizationService()["ExplorerPath_Title"]);
        Assert.Contains(groups[2].Items, item => item.Keys == "Ctrl + S");
    }

    [Fact]
    public void Shortcuts_page_is_a_read_only_reference_with_an_explicit_capture_control()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckSettings.xaml"));
        var tab = xaml.Descendants(Wpf + "TabItem").Single(element =>
            ((string?)element.Attribute("Header"))?.Contains("Settings_TabShortcuts") == true);
        Assert.Contains(tab.Descendants(Wpf + "ItemsControl"), control =>
            ((string?)control.Attribute("ItemsSource"))?.Contains("ShortcutGroups") == true);
        Assert.DoesNotContain(tab.Descendants(Wpf + "TextBox"), _ => true);
        var button = Assert.Single(tab.Descendants(Wpf + "Button"));
        Assert.Equal("CaptureHotkey_Click", (string?)button.Attribute("Click"));
        Assert.Equal("CaptureHotkey_PreviewKeyDown", (string?)button.Attribute("PreviewKeyDown"));
        Assert.Equal("CaptureHotkey_LostKeyboardFocus", (string?)button.Attribute("LostKeyboardFocus"));
        Assert.NotNull(button.Attribute("AutomationProperties.Name"));
        var enable = Assert.Single(tab.Descendants(Wpf + "CheckBox"));
        Assert.Contains("GlobalHotkeyEnabled", (string?)enable.Attribute("IsChecked"));
        Assert.Contains("CanToggleGlobalHotkey", (string?)enable.Attribute("IsEnabled"));
        Assert.Contains(tab.Descendants(Wpf + "TextBlock"), element =>
            ((string?)element.Attribute("Text"))?.Contains("GlobalHotkeyStatus") == true &&
            (string?)element.Attribute("AutomationProperties.LiveSetting") == "Polite" &&
            element.Descendants(Wpf + "DataTrigger").Any(trigger =>
                ((string?)trigger.Attribute("Binding"))?.Contains("HasGlobalHotkeyWarning") == true));
    }

    [Fact]
    public async Task Disabled_shortcut_can_be_changed_without_registering_anything()
    {
        var state = new ApplicationState();
        var saves = 0;
        using var theme = new ThemeService();
        var settings = new SettingsViewModel(state, () =>
            { saves++; return Task.CompletedTask; }, new NoUpdates(), theme, new LocalizationService());

        await settings.SetGlobalHotkeyGestureAsync("Ctrl+Shift+F6");

        Assert.False(state.Preferences.GlobalHotkeyEnabled);
        Assert.Equal("Ctrl+Shift+F6", state.Preferences.GlobalHotkeyGesture);
        Assert.Equal(1, saves);
        await settings.ConfigureGlobalHotkeyAsync(true, "Ctrl+Shift+F6");
        Assert.False(state.Preferences.GlobalHotkeyEnabled);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Invalid_recording_is_never_saved()
    {
        var state = new ApplicationState();
        var saves = 0;
        using var theme = new ThemeService();
        var settings = new SettingsViewModel(state, () =>
            { saves++; return Task.CompletedTask; }, new NoUpdates(), theme, new LocalizationService());

        await settings.SetGlobalHotkeyGestureAsync("Win+L");
        Assert.Equal(0, saves);
        Assert.Equal(GlobalHotkeyGesture.Default, state.Preferences.GlobalHotkeyGesture);
        Assert.False(string.IsNullOrWhiteSpace(settings.GlobalHotkeyStatus));
    }

    [Fact]
    public async Task Hotkey_preference_rolls_back_when_storage_fails()
    {
        var state = new ApplicationState();
        using var theme = new ThemeService();
        var settings = new SettingsViewModel(state,
            () => Task.FromException(new IOException("disk unavailable")),
            new NoUpdates(), theme, new LocalizationService());

        await settings.SetGlobalHotkeyGestureAsync("Ctrl+Shift+F3");

        Assert.Equal(GlobalHotkeyGesture.Default, state.Preferences.GlobalHotkeyGesture);
        Assert.NotNull(settings.SaveError);
    }

    private sealed class NoUpdates : IUpdateCoordinator
    {
        public event EventHandler? Changed { add { } remove { } }
        public UpdateStatus Status => UpdateStatus.Idle;
        public string? Version => null;
        public string? Diagnostic => null;
        public int Progress => 0;
        public bool IsBusy => false;
        public bool CanDownload => false;
        public bool CanRestart => false;
        public Task CheckAsync() => Task.CompletedTask;
        public Task DownloadAsync() => Task.CompletedTask;
        public void RequestRestart() { }
    }}
