using Cavdeck.Application.Settings;

namespace Cavdeck.App.Services;

public enum WindowCloseDecision { Hide, Confirm, Exit }

public static class WindowClosePolicy
{
    public static WindowCloseDecision Decide(WindowCloseBehavior behavior,
        int unfinished, bool trayAvailable)
    {
        if (behavior == WindowCloseBehavior.MinimizeToTray && trayAvailable)
            return WindowCloseDecision.Hide;
        return unfinished > 0 ? WindowCloseDecision.Confirm : WindowCloseDecision.Exit;
    }
}
