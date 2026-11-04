namespace Cavdeck.Application.Shell;

public enum ShellPage
{
    Connections,
    Favorites,
    Recent,
    Transfers,
    Explorer,
    Settings
}

public static class ShellPages
{
    public const string Connections = "connections";
    public const string Favorites = "favorites";
    public const string Recent = "recent";
    public const string Transfers = "transfers";
    public const string Explorer = "explorer";
    public const string Settings = "settings";

    public static bool TryParse(string? value, out ShellPage page)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Connections:
                page = ShellPage.Connections;
                return true;
            case Favorites:
                page = ShellPage.Favorites;
                return true;
            case Recent:
                page = ShellPage.Recent;
                return true;
            case Transfers:
                page = ShellPage.Transfers;
                return true;
            case Explorer:
                page = ShellPage.Explorer;
                return true;
            case Settings:
                page = ShellPage.Settings;
                return true;
            default:
                page = default;
                return false;
        }
    }

    public static string ToId(ShellPage page) => page switch
    {
        ShellPage.Favorites => Favorites,
        ShellPage.Recent => Recent,
        ShellPage.Transfers => Transfers,
        ShellPage.Explorer => Explorer,
        ShellPage.Settings => Settings,
        _ => Connections
    };
}
