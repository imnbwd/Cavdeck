using Cavdeck.App.Features.Updates;
using Cavdeck.App.Features.Settings;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class CloseBehaviorSettingsTests
{
    [Fact]
    public async Task Save_failure_rolls_back_close_behavior_and_reports_error()
    {
        var state = new ApplicationState();
        using var theme = new ThemeService();
        var vm = new SettingsViewModel(state,
            () => Task.FromException(new IOException("test failure")),
            new NoUpdates(), theme, new LocalizationService());

        await vm.SetCloseBehaviorAsync(nameof(WindowCloseBehavior.MinimizeToTray));

        Assert.Equal(WindowCloseBehavior.Exit, state.Preferences.WindowCloseBehavior);
        Assert.NotNull(vm.SaveError);
    }

    [Fact]
    public async Task Successful_save_preserves_other_preferences()
    {
        var state = new ApplicationState();
        state.Preferences.MaxConcurrentTransfers = 7;
        var saved = false;
        using var theme = new ThemeService();
        var vm = new SettingsViewModel(state, () =>
        {
            saved = true;
            return Task.CompletedTask;
        }, new NoUpdates(), theme, new LocalizationService());

        await vm.SetCloseBehaviorAsync(nameof(WindowCloseBehavior.MinimizeToTray));

        Assert.True(saved);
        Assert.Equal(WindowCloseBehavior.MinimizeToTray, state.Preferences.WindowCloseBehavior);
        Assert.Equal(7, state.Preferences.MaxConcurrentTransfers);
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
