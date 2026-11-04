using Cavdeck.App.Features.Updates;
using System.Globalization;
using Cavdeck.App.Features.Settings;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class LanguageSettingsTests
{
    [Fact]
    public async Task Language_choices_come_from_catalog_and_refresh_system_label()
    {
        var originalUi = CultureInfo.DefaultThreadCurrentUICulture;
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            var state = new ApplicationState();
            using var theme = new ThemeService();
            var texts = new LocalizationService();
            var saves = 0;
            var vm = Create(state, theme, texts, () =>
            {
                saves++;
                return Task.CompletedTask;
            });

            Assert.Equal(AppLanguages.Supported.Select(x => x.Code).Prepend(AppLanguages.System),
                vm.LanguageOptions.Select(x => x.Code));
            Assert.Equal(AppLanguages.Supported.Select(x => x.NativeName),
                vm.LanguageOptions.Skip(1).Select(x => x.DisplayName));

            var systemOption = vm.LanguageOptions[0];
            await vm.SetLanguageAsync("zh-CN");
            Assert.Equal("zh-CN", state.Preferences.Language);
            Assert.Equal("zh-CN", texts.CurrentLanguage);
            Assert.Equal(texts["Settings_FollowSystem"], systemOption.DisplayName);
            Assert.Contains("简体中文", vm.LanguageDetail);
            Assert.Same(systemOption, vm.LanguageOptions[0]);

            await vm.SetLanguageAsync("en-US");
            Assert.Equal("en-US", state.Preferences.Language);
            Assert.Equal("en-US", texts.CurrentLanguage);
            Assert.Equal(texts["Settings_FollowSystem"], systemOption.DisplayName);
            Assert.Contains("English", vm.LanguageDetail);

            await vm.SetLanguageAsync("not-a-culture");
            Assert.Equal("en-US", state.Preferences.Language);
            Assert.Equal(2, saves);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = originalUi;
            CultureInfo.DefaultThreadCurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task Save_failure_restores_preference_and_effective_language()
    {
        var originalUi = CultureInfo.DefaultThreadCurrentUICulture;
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            var state = new ApplicationState { Preferences = new UserPreferences { Language = "en-US" } };
            using var theme = new ThemeService();
            var texts = new LocalizationService();
            texts.ChangeLanguage("en-US");
            var vm = Create(state, theme, texts,
                () => Task.FromException(new IOException("settings write failed")));

            await vm.SetLanguageAsync("zh-CN");

            Assert.Equal("en-US", state.Preferences.Language);
            Assert.Equal("en-US", texts.CurrentLanguage);
            Assert.NotNull(vm.SaveError);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = originalUi;
            CultureInfo.DefaultThreadCurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("ja-JP", "日本語", "言語")]
    [InlineData("de-DE", "Deutsch", "Sprache")]
    public async Task New_languages_can_be_selected_saved_and_switched_live(
        string locale, string nativeName, string languageLabel)
    {
        var previousUi = CultureInfo.DefaultThreadCurrentUICulture;
        var previousCulture = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            var state = new ApplicationState();
            using var theme = new ThemeService();
            var texts = new LocalizationService();
            var saveCount = 0;
            var vm = Create(state, theme, texts, () =>
            {
                saveCount++;
                return Task.CompletedTask;
            });
            await vm.SetLanguageAsync(locale);

            Assert.Equal(locale, texts.CurrentLanguage);
            Assert.Equal(locale, state.Preferences.Language);
            Assert.Equal(1, saveCount);
            Assert.Contains(nativeName, vm.LanguageDetail);
            Assert.Equal(languageLabel, texts["Label_Language"]);
            Assert.Equal(texts["Settings_FollowSystem"], vm.LanguageOptions[0].DisplayName);

            await vm.SetLanguageAsync("en-US");
            Assert.Equal("en-US", texts.CurrentLanguage);
            Assert.Equal(2, saveCount);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = previousUi;
            CultureInfo.DefaultThreadCurrentCulture = previousCulture;
        }
    }

    private static SettingsViewModel Create(ApplicationState state, ThemeService theme,
        LocalizationService texts, Func<Task> save) =>
        new(state, save, new NoUpdates(), theme, texts);

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
