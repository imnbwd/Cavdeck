using Cavdeck.Application.Shell;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class SidebarPresentationTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public void Compact_presentation_respects_temporary_override(
        bool persisted, bool compact, bool expanded, bool expected)
    {
        Assert.Equal(expected, SidebarPresentation.IsCollapsed(persisted, compact, expanded));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Leaving_compact_mode_restores_persisted_preference(bool persisted)
    {
        Assert.Equal(persisted, SidebarPresentation.IsCollapsed(persisted, false, false));
    }
}
