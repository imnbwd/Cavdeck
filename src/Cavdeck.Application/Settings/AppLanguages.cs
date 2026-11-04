using System.Globalization;

namespace Cavdeck.Application.Settings;

// Register only languages with a complete shipped Strings.<culture>.resx resource.
// Adding a language here also makes it available in Settings and system-language matching.
public sealed record AppLanguage(string Code, string NativeName, IReadOnlyList<string> SystemAliases);

public static class AppLanguages
{
    public const string System = "system";
    public const string DefaultLanguage = "en-US";

    public static IReadOnlyList<AppLanguage> Supported { get; } =
    [
        new("en-US", "English", ["en"]),
        new("zh-CN", "简体中文", ["zh-Hans", "zh-SG"]),
        new("ja-JP", "日本語", ["ja"]),
        new("de-DE", "Deutsch", ["de"])
    ];

    public static bool IsSupportedSelection(string? value) =>
        string.Equals(value, System, StringComparison.OrdinalIgnoreCase) ||
        Supported.Any(language => string.Equals(language.Code, value, StringComparison.OrdinalIgnoreCase));

    // Never persist an unsupported UI culture; corrupted or older preferences use the system option.
    public static string NormalizeSelection(string? value) =>
        string.Equals(value, System, StringComparison.OrdinalIgnoreCase) ? System :
        Supported.FirstOrDefault(language =>
            string.Equals(language.Code, value, StringComparison.OrdinalIgnoreCase))?.Code ?? System;

    public static AppLanguage Find(string? code) =>
        Supported.FirstOrDefault(language =>
            string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) ??
        Supported[0];

    public static string Resolve(string? selection, CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);

        var normalized = NormalizeSelection(selection);
        if (normalized != System) return normalized;

        // Exact culture first, then explicit regional/script aliases and parent cultures.
        // Do not silently show simplified Chinese to users of traditional Chinese.
        for (var culture = systemCulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            var match = Supported.FirstOrDefault(language =>
                string.Equals(language.Code, culture.Name, StringComparison.OrdinalIgnoreCase) ||
                language.SystemAliases.Any(alias =>
                    string.Equals(alias, culture.Name, StringComparison.OrdinalIgnoreCase)));
            if (match is not null) return match.Code;
        }

        return DefaultLanguage;
    }
}
