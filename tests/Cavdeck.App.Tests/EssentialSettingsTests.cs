using Cavdeck.App.Features.Updates;
using Cavdeck.App.Features.Settings;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class EssentialSettingsTests
{
    [Fact]
    public async Task Explorer_settings_change_live_and_signal_other_tabs()
    {
        var state = new ApplicationState();
        var changeCount = 0;
        state.ExplorerDisplayChanged += (_, _) => changeCount++;
        using var theme = new ThemeService();
        var vm = Create(state, theme);

        await vm.SetBrowserViewAsync("Icons");
        await vm.SetGridDensityAsync("Compact");
        await vm.SetShowDetailsPaneAsync(false);

        Assert.Equal(ObjectBrowserView.Icons, state.Preferences.ObjectBrowser);
        Assert.Equal(ObjectGridDensity.Compact, state.Preferences.GridDensity);
        Assert.False(state.Preferences.ShowDetailsPane);
        Assert.Equal(3, changeCount);
        await vm.SetBrowserViewAsync("unknown");
        Assert.Equal(3, changeCount);
    }

    [Fact]
    public async Task Failure_rolls_back_explorer_state_and_notifies()
    {
        var state = new ApplicationState();
        var changeCount = 0;
        state.ExplorerDisplayChanged += (_, _) => changeCount++;
        using var theme = new ThemeService();
        var vm = Create(state, theme, () => Task.FromException(new IOException("disk unavailable")));

        await vm.SetGridDensityAsync("Compact");

        Assert.Equal(ObjectGridDensity.Comfortable, state.Preferences.GridDensity);
        Assert.Equal(2, changeCount);
        Assert.NotNull(vm.SaveError);
    }

    [Fact]
    public async Task New_preferences_save_and_skip_redundant_writes()
    {
        var state = new ApplicationState();
        var count = 0;
        using var theme = new ThemeService();
        var vm = Create(state, theme, () => { count++; return Task.CompletedTask; });
        Assert.True(vm.RestorePreviousTabs);
        Assert.True(vm.NotifyTransferCompletion);
        await vm.SetRestorePreviousTabsAsync(false);
        await vm.SetNotifyTransferCompletionAsync(false);
        Assert.False(vm.RestorePreviousTabs);
        Assert.False(vm.NotifyTransferCompletion);
        Assert.Equal(2, count);
        await vm.SetRestorePreviousTabsAsync(false);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task New_preferences_roll_back_when_save_fails()
    {
        var state = new ApplicationState();
        using var theme = new ThemeService();
        var vm = Create(state, theme, () => Task.FromException(new IOException("write failed")));
        await vm.SetRestorePreviousTabsAsync(false);
        await vm.SetNotifyTransferCompletionAsync(false);
        Assert.True(vm.RestorePreviousTabs);
        Assert.True(vm.NotifyTransferCompletion);
        Assert.NotNull(vm.SaveError);
    }

    [Fact]
    public async Task Transfer_conflict_default_never_allows_unattended_overwrite()
    {
        var state = new ApplicationState();
        using var theme = new ThemeService();
        var vm = Create(state, theme);

        await vm.SetTransferConflictAsync("Rename");
        Assert.Equal(TransferConflictPreference.Rename, state.Preferences.TransferConflict);
        await vm.SetTransferConflictAsync("Overwrite");
        Assert.Equal(TransferConflictPreference.Rename, state.Preferences.TransferConflict);
    }

    [Fact]
    public async Task Download_default_requires_existing_folder_and_can_be_disabled()
    {
        var state = new ApplicationState();
        using var theme = new ThemeService();
        var vm = Create(state, theme);

        await vm.SetDownloadDestinationAsync("DefaultFolder");
        Assert.Equal(DownloadDestinationPreference.AskEveryTime, state.Preferences.DownloadDestination);

        await vm.SetDownloadDirectoryAsync(Path.GetTempPath());
        Assert.True(vm.HasDownloadDirectory);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), state.Preferences.AvailableDownloadDirectory);
        Assert.Equal(DownloadDestinationPreference.DefaultFolder, state.Preferences.DownloadDestination);

        await vm.SetDownloadDestinationAsync("AskEveryTime");
        Assert.Equal(DownloadDestinationPreference.AskEveryTime, state.Preferences.DownloadDestination);
    }

    [Fact]
    public async Task Invalid_download_folder_is_rejected_without_mutating_preference()
    {
        var state = new ApplicationState();
        using var theme = new ThemeService();
        var vm = Create(state, theme);

        await vm.SetDownloadDirectoryAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal("", state.Preferences.DefaultDownloadDirectory);
        Assert.Equal(DownloadDestinationPreference.AskEveryTime, state.Preferences.DownloadDestination);
        Assert.NotNull(vm.SaveError);
    }

    [Fact]
    public async Task Failed_download_directory_save_rolls_back_both_values()
    {
        var state = new ApplicationState();
        using var theme = new ThemeService();
        var vm = Create(state, theme, () => Task.FromException(new IOException("disk failure")));

        await vm.SetDownloadDirectoryAsync(Path.GetTempPath());
        Assert.Equal("", state.Preferences.DefaultDownloadDirectory);
        Assert.Equal(DownloadDestinationPreference.AskEveryTime, state.Preferences.DownloadDestination);
        Assert.NotNull(vm.SaveError);
    }

    [Fact]
    public async Task Concurrency_applies_immediately_and_rolls_back_when_save_fails()
    {
        var state = new ApplicationState();
        var applied = new List<int>();
        using var theme = new ThemeService();
        var vm = Create(state, theme, () => Task.FromException(new IOException("disk")), applied.Add);

        await vm.SetConcurrencyAsync(6);
        Assert.Equal(3, state.Preferences.MaxConcurrentTransfers);
        Assert.Equal(new[] { 6, 3 }, applied);
        await vm.SetConcurrencyAsync(11);
        Assert.Equal(new[] { 6, 3 }, applied);
    }

    private static SettingsViewModel Create(ApplicationState state, ThemeService theme,
        Func<Task>? save = null, Action<int>? applyConcurrency = null) =>
        new(state, save ?? (() => Task.CompletedTask), new NoUpdates(),
            theme, new LocalizationService(), applyConcurrency);

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
