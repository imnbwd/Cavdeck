using Cavdeck.Application.Settings;
using Cavdeck.Infrastructure.Settings;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class JsonSettingsStoreTests
{
    [Theory]
    [InlineData("{}", WindowCloseBehavior.Exit)]
    [InlineData("{\"WindowCloseBehavior\":\"MinimizeToTray\"}", WindowCloseBehavior.MinimizeToTray)]
    [InlineData("{\"WindowCloseBehavior\":\"unknown\"}", WindowCloseBehavior.Exit)]
    [InlineData("{\"WindowCloseBehavior\":42}", WindowCloseBehavior.Exit)]
    public async Task Close_behavior_handles_old_and_invalid_json(string json, WindowCloseBehavior expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            var preferences = await new JsonSettingsStore(path).LoadAsync();
            Assert.Equal(expected, preferences.WindowCloseBehavior);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public async Task Close_behavior_roundtrips_without_losing_other_preferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            await store.SaveAsync(new UserPreferences
            {
                WindowCloseBehavior = WindowCloseBehavior.MinimizeToTray,
                MaxConcurrentTransfers = 6
            });
            var loaded = await store.LoadAsync();
            Assert.Equal(WindowCloseBehavior.MinimizeToTray, loaded.WindowCloseBehavior);
            Assert.Equal(6, loaded.MaxConcurrentTransfers);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
    [Fact]
    public async Task Concurrent_saves_leave_one_complete_settings_document()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cavdeck-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var saves = Enumerable.Range(1, 12)
                .Select(index => store.SaveAsync(new UserPreferences { MaxConcurrentTransfers = index }));
            await Task.WhenAll(saves);

            var loaded = await store.LoadAsync();
            Assert.InRange(loaded.MaxConcurrentTransfers, 1, 12);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            foreach (var path in Directory.GetFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
