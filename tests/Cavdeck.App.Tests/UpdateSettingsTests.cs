using System.Xml.Linq;
using Cavdeck.App.Features.Settings;
using Cavdeck.App.Features.Updates;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class UpdateSettingsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void About_exposes_manual_actions_and_nonblocking_preferences()
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckSettings.xaml"));
        var about = Assert.Single(xml.Descendants(Wpf + "TabItem")
            , x => ((string?)x.Attribute("Header"))?.Contains("Settings_TabAbout") == true);
        var bindings = string.Join(" ", about.DescendantsAndSelf()
            .SelectMany(x => x.Attributes().Select(attr => attr.Value)));
        foreach (var binding in new[]
        {
            "CheckUpdatesCommand", "DownloadUpdateCommand", "RestartUpdateCommand",
            "AutomaticUpdateChecks", "BackgroundUpdateDownloads", "UpdateStatusText"
        })
            Assert.Contains(binding, bindings);
    }

    [Fact]
    public void Update_preferences_are_on_by_default_and_save_independently()
    {
        var state = new ApplicationState();
        var saveCount = 0;
        var coordinator = new FakeCoordinator();
        using var theme = new ThemeService();
        var vm = new SettingsViewModel(state, () =>
        {
            saveCount++;
            return Task.CompletedTask;
        }, coordinator, theme, new LocalizationService());

        Assert.True(vm.AutomaticUpdateChecks);
        Assert.True(vm.BackgroundUpdateDownloads);

        vm.AutomaticUpdateChecks = false;
        vm.BackgroundUpdateDownloads = false;

        Assert.False(state.Preferences.CheckForUpdates);
        Assert.False(state.Preferences.DownloadUpdatesInBackground);
        Assert.Equal(2, saveCount);
    }

    [Fact]
    public void Coordinator_change_refreshes_localized_update_status()
    {
        var state = new ApplicationState();
        var updates = new FakeCoordinator();
        using var theme = new ThemeService();
        var texts = new LocalizationService();
        texts.ChangeLanguage("en-US");
        var vm = new SettingsViewModel(state, () => Task.CompletedTask, updates, theme, texts);
        var changed = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.UpdateStatusText)) changed++;
        };

        updates.Set(UpdateStatus.Available, "1.2.3");
        Assert.Contains("1.2.3", vm.UpdateStatusText);
        Assert.True(vm.CanDownloadUpdate);
        updates.Set(UpdateStatus.Downloaded, "1.2.3");
        Assert.True(vm.CanRestartUpdate);
        Assert.True(changed >= 2);
    }

    private sealed class FakeCoordinator : IUpdateCoordinator
    {
        public event EventHandler? Changed;
        public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
        public string? Version { get; private set; }
        public string? Diagnostic => null;
        public int Progress => 0;
        public bool IsBusy => false;
        public bool CanDownload => Status == UpdateStatus.Available;
        public bool CanRestart => Status == UpdateStatus.Downloaded;
        public Task CheckAsync() => Task.CompletedTask;
        public Task DownloadAsync() => Task.CompletedTask;
        public void RequestRestart() { }

        public void Set(UpdateStatus status, string version)
        {
            Status = status;
            Version = version;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
