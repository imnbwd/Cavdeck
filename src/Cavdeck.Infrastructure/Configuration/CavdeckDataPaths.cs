namespace Cavdeck.Infrastructure.Configuration;

/// <summary>
/// Keeps mutable local data outside Velopack's fixed application directory
/// (%LOCALAPPDATA%\Cavdeck). Installation and uninstallation must never own
/// transfer history or diagnostics.
/// </summary>
public static class CavdeckDataPaths
{
    public static string LocalDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CavdeckData");
}
