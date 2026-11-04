using System.Windows;

namespace Cavdeck.App.Controls;

internal static class Motion
{
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public static TimeSpan Duration(TimeSpan normal) =>
        AnimationsEnabled ? normal : TimeSpan.Zero;
}
