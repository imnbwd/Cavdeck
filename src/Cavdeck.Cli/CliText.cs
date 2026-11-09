using System.Globalization;
using System.Resources;

namespace Cavdeck.Cli;

internal static class CliText
{
    private static readonly ResourceManager Resources = new("Cavdeck.Cli.Resources.Strings", typeof(CliText).Assembly);

    public static string Get(string key) => Resources.GetString(key)
        ?? throw new MissingManifestResourceException(key);

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
