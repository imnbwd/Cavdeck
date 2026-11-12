using Cavdeck.App.Features.Preview;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class MediaViewerLifecycleTests
{
    [Fact]
    public void Preview_pane_context_is_independent_from_media_viewer_lifetime()
    {
        // The preview pane follows Explorer selection. It must not publish a
        // context-change signal that implicitly closes an independent player.
        Assert.Null(typeof(PreviewPaneViewModel).GetEvent("MediaContextChanged"));
    }
}
