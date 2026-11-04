using System.Globalization;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class AppLanguagesTests
{
    [Fact]
    public void Catalog_contains_only_shipped_languages_in_display_order()
    {
        Assert.Equal(new[] { "en-US", "zh-CN", "ja-JP", "de-DE" },
            AppLanguages.Supported.Select(language => language.Code));
        Assert.Equal(new[] { "English", "简体中文", "日本語", "Deutsch" },
            AppLanguages.Supported.Select(language => language.NativeName));
    }

    [Theory]
    [InlineData("system", "system")]
    [InlineData("SYSTEM", "system")]
    [InlineData("en-us", "en-US")]
    [InlineData("zh-cn", "zh-CN")]
    [InlineData("ja-jp", "ja-JP")]
    [InlineData("de-de", "de-DE")]
    [InlineData("not-a-culture", "system")]
    [InlineData(null, "system")]
    public void Preference_normalization_is_safe_and_case_insensitive(string? input, string expected)
    {
        Assert.Equal(expected, AppLanguages.NormalizeSelection(input));
        Assert.Equal(input is not null && expected != "system" ||
                     string.Equals(input, "system", StringComparison.OrdinalIgnoreCase),
            AppLanguages.IsSupportedSelection(input));
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-TW", "en-US")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("ja", "ja-JP")]
    [InlineData("de-DE", "de-DE")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("de-CH", "de-DE")]
    [InlineData("de", "de-DE")]
    [InlineData("fr-FR", "en-US")]
    public void System_culture_uses_available_resources_or_english_fallback(
        string systemCulture, string expected)
    {
        Assert.Equal(expected, AppLanguages.Resolve("system",
            CultureInfo.GetCultureInfo(systemCulture)));
    }

    [Fact]
    public void Explicit_language_overrides_system_culture()
    {
        Assert.Equal("zh-CN", AppLanguages.Resolve("zh-CN",
            CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("en-US", AppLanguages.Resolve("en-US",
            CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Equal("ja-JP", AppLanguages.Resolve("ja-JP",
            CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("de-DE", AppLanguages.Resolve("de-DE",
            CultureInfo.GetCultureInfo("ja-JP")));
    }
}
