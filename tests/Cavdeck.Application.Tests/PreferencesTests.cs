using Cavdeck.Application.Settings;
using Cavdeck.Application.Shell;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class PreferencesTests
{
    [Fact]
    public void Defaults_are_safe_and_local_first()
    {
        var preferences = new UserPreferences();
        Assert.Equal("system", preferences.Language);
        Assert.Equal(AppTheme.System, preferences.Theme);
        Assert.Equal("stable", preferences.UpdateChannel);
        Assert.Equal(WindowLayout.DefaultSidebarWidth, preferences.Layout.SidebarWidth);
        Assert.False(preferences.Layout.IsMaximized);
        Assert.Equal(ObjectBrowserView.List, preferences.ObjectBrowser);
        Assert.Equal(WindowCloseBehavior.Exit, preferences.WindowCloseBehavior);
        Assert.Equal(DownloadDestinationPreference.AskEveryTime, preferences.DownloadDestination);
        Assert.Equal(TransferConflictPreference.AskOnConflict, preferences.TransferConflict);
        Assert.Null(preferences.AvailableDownloadDirectory);
        Assert.True(preferences.RestorePreviousTabs);
        Assert.True(preferences.NotifyTransferCompletion);
    }

    [Fact]
    public void Malformed_preferences_fall_back_to_safe_values()
    {
        var p = new UserPreferences {
            DownloadDestination = (DownloadDestinationPreference)99,
            TransferConflict = (TransferConflictPreference)99,
            ObjectBrowser = (ObjectBrowserView)99,
            GridDensity = (ObjectGridDensity)99,
            DefaultDownloadDirectory = null!
        };
        p.Normalize();
        Assert.Equal(DownloadDestinationPreference.AskEveryTime, p.DownloadDestination);
        Assert.Equal(TransferConflictPreference.AskOnConflict, p.TransferConflict);
        Assert.Equal(ObjectBrowserView.List, p.ObjectBrowser);
        Assert.Equal(ObjectGridDensity.Comfortable, p.GridDensity);
        Assert.Equal("", p.DefaultDownloadDirectory);
    }

    [Theory]
    [InlineData("EN-us", "en-US")]
    [InlineData("ZH-cn", "zh-CN")]
    [InlineData("unsupported-value", "system")]
    [InlineData(null, "system")]
    public void Unknown_or_noncanonical_language_preferences_are_normalized(
        string? value, string expected)
    {
        var preferences = new UserPreferences { Language = value! };
        preferences.Normalize();
        Assert.Equal(expected, preferences.Language);
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("http://untrusted.example")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsupported_update_channels_normalize_to_the_official_stable_feed(string? value)
    {
        var prefs = new UserPreferences { UpdateChannel = value! };
        prefs.Normalize();
        Assert.Equal("stable", prefs.UpdateChannel);
    }

    [Fact]
    public void Legacy_prompt_every_time_setting_normalizes_to_conflict_only()
    {
        var preferences = new UserPreferences
        {
            TransferConflict = TransferConflictPreference.AskEveryTime
        };
        preferences.Normalize();
        Assert.Equal(TransferConflictPreference.AskOnConflict, preferences.TransferConflict);
    }

    [Fact]
    public void Removed_download_folder_requires_user_to_pick_again()
    {
        var p = new UserPreferences {
            DownloadDestination = DownloadDestinationPreference.DefaultFolder,
            DefaultDownloadDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        };
        Assert.Null(p.AvailableDownloadDirectory);
    }

    [Fact]
    public void Invalid_close_behavior_normalizes_to_exit()
    {
        var preferences = new UserPreferences { WindowCloseBehavior = (WindowCloseBehavior)42 };
        preferences.Normalize();
        Assert.Equal(WindowCloseBehavior.Exit, preferences.WindowCloseBehavior);
    }
}
