namespace Cavdeck.Application.Shell;

/// <summary>
/// Pure presentation policy. A narrow-window expansion is temporary and must
/// never replace the user's persisted sidebar preference.
/// </summary>
public static class SidebarPresentation
{
    public static bool IsCollapsed(bool savedCollapsed, bool autoCompact, bool temporaryExpanded)
        => autoCompact ? !temporaryExpanded : savedCollapsed;
}
