using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cavdeck.App.Localization;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class LocalizationResourceTests
{
    private static readonly string ResourceDirectory =
        Path.Combine(AppContext.BaseDirectory, "Localization");

    [Fact]
    public void Every_registered_translation_has_the_complete_base_resource_key_set()
    {
        var english = LoadKeys(Path.Combine(ResourceDirectory, "Strings.resx"));
        Assert.NotEmpty(english);

        foreach (var language in AppLanguages.Supported.Where(x =>
                     x.Code != AppLanguages.DefaultLanguage))
        {
            var path = Path.Combine(ResourceDirectory, $"Strings.{language.Code}.resx");
            Assert.True(File.Exists(path), $"Missing translation file: {path}");
            Assert.Equal(english.OrderBy(key => key, StringComparer.Ordinal),
                LoadKeys(path).OrderBy(key => key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Every_translation_has_nonempty_values_and_matching_format_placeholders()
    {
        var english = LoadValues(Path.Combine(ResourceDirectory, "Strings.resx"));

        foreach (var language in AppLanguages.Supported.Where(x =>
                     x.Code != AppLanguages.DefaultLanguage))
        {
            var path = Path.Combine(ResourceDirectory, $"Strings.{language.Code}.resx");
            var localized = LoadValues(path);

            foreach (var (key, original) in english)
            {
                Assert.True(localized.TryGetValue(key, out var translation),
                    $"Missing {language.Code}: {key}");
                Assert.False(string.IsNullOrWhiteSpace(translation),
                    $"Empty {language.Code}: {key}");

                Assert.Equal(Placeholders(original), Placeholders(translation!));
            }
        }
    }

    [Theory]
    [InlineData("ja-JP", "言語", "接続", "保存")]
    [InlineData("de-DE", "Sprache", "Verbindungen", "Speichern")]
    public void Newly_shipped_satellite_resources_resolve_in_runtime(
        string cultureName, string languageLabel, string connectionsLabel, string saveLabel)
    {
        var manager = new ResourceManager("Cavdeck.App.Resources.Strings",
            typeof(LocalizationService).Assembly);
        var culture = CultureInfo.GetCultureInfo(cultureName);

        Assert.Equal(languageLabel, manager.GetString("Label_Language", culture));
        Assert.Equal(connectionsLabel, manager.GetString("Header_Connections", culture));
        Assert.Equal(saveLabel, manager.GetString("Connection_Save", culture));
        Assert.DoesNotContain("[", manager.GetString("Connection_Failure_Unknown", culture));
    }

    private static string[] Placeholders(string source) =>
        Regex.Matches(source, @"\{[0-9]+(?:,[+-]?[0-9]+)?(?::[^{}]+)?\}")
            .Select(match => match.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static Dictionary<string, string> LoadValues(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in XDocument.Load(path).Root!.Elements("data"))
        {
            var key = (string?)element.Attribute("name");
            if (string.IsNullOrEmpty(key)) continue;
            Assert.True(result.TryAdd(key, (string?)element.Element("value") ?? ""),
                $"Duplicate resource key in {path}: {key}");
        }
        return result;
    }

    private static HashSet<string> LoadKeys(string path)
    {
        Assert.True(File.Exists(path), $"Missing resource file: {path}");
        return XDocument.Load(path).Root!.Elements("data")
            .Select(node => (string?)node.Attribute("name"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
