using Cavdeck.App.Services;
using Cavdeck.Application.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class WindowClosePolicyTests
{
    [Theory]
    [InlineData(WindowCloseBehavior.Exit, 0, true, WindowCloseDecision.Exit)]
    [InlineData(WindowCloseBehavior.Exit, 2, true, WindowCloseDecision.Confirm)]
    [InlineData(WindowCloseBehavior.MinimizeToTray, 0, true, WindowCloseDecision.Hide)]
    [InlineData(WindowCloseBehavior.MinimizeToTray, 2, true, WindowCloseDecision.Hide)]
    [InlineData(WindowCloseBehavior.MinimizeToTray, 0, false, WindowCloseDecision.Exit)]
    [InlineData(WindowCloseBehavior.MinimizeToTray, 2, false, WindowCloseDecision.Confirm)]
    public void Normal_close_decision_follows_preference_and_tray_availability(
        WindowCloseBehavior behavior, int unfinished, bool trayAvailable,
        WindowCloseDecision expected) =>
        Assert.Equal(expected, WindowClosePolicy.Decide(behavior, unfinished, trayAvailable));
}
