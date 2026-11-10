using Cavdeck.App.Features.Preview;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ImagePreviewTests
{
    [Fact]
    public void Gallery_preserves_display_order_and_stops_at_boundaries()
    {
        var gallery = new ImagePreviewGallery(
        [
            new ImagePreviewItem("b.png", "b.png"),
            new ImagePreviewItem("a.png", "a.png"),
            new ImagePreviewItem("c.webp", "c.webp")
        ], "a.png");

        Assert.Equal(2, gallery.Position);
        Assert.True(gallery.CanPrevious);
        Assert.True(gallery.CanNext);
        Assert.True(gallery.Move(-1));
        Assert.Equal("b.png", gallery.Current.Key);
        Assert.False(gallery.Move(-1));
        Assert.True(gallery.Move(1));
        Assert.True(gallery.Move(1));
        Assert.Equal("c.webp", gallery.Current.Key);
        Assert.False(gallery.Move(1));
    }

    [Fact]
    public void Gallery_deduplicates_keys_and_rejects_an_unknown_start()
    {
        var candidates = new[]
        {
            new ImagePreviewItem("first.png", "first"),
            new ImagePreviewItem("first.png", "duplicate"),
            new ImagePreviewItem("second.png", "second")
        };

        var gallery = new ImagePreviewGallery(candidates, "second.png");

        Assert.Equal(2, gallery.Count);
        Assert.Throws<ArgumentException>(() => new ImagePreviewGallery(candidates, "other.png"));
    }

    [Fact]
    public void Fit_never_upscales_a_small_image_and_centers_it()
    {
        var result = ImageViewportMath.Fit(200, 100, 800, 600);
        Assert.Equal(1, result.Scale);
        Assert.Equal(300, result.X);
        Assert.Equal(250, result.Y);
    }

    [Fact]
    public void Fit_scales_a_large_image_within_both_viewport_dimensions()
    {
        var result = ImageViewportMath.Fit(2000, 1000, 800, 500);
        Assert.Equal(0.4, result.Scale, 6);
        Assert.Equal(0, result.X, 6);
        Assert.Equal(50, result.Y, 6);
    }

    [Fact]
    public void Zoom_preserves_the_cursor_anchor_when_within_pan_limits()
    {
        var fit = ImageViewportMath.Fit(1000, 1000, 500, 500);
        var zoomed = ImageViewportMath.Zoom(fit, 1, 250, 250, 1000, 1000, 500, 500);

        Assert.Equal(1, zoomed.Scale);
        Assert.Equal(-250, zoomed.X, 6);
        Assert.Equal(-250, zoomed.Y, 6);
    }

    [Fact]
    public void Pan_is_clamped_so_the_entire_image_cannot_escape_the_viewport()
    {
        var start = new ImageViewportMath.View(1, -250, -250);
        var right = ImageViewportMath.Pan(start, 5000, 5000, 1000, 1000, 500, 500);
        var left = ImageViewportMath.Pan(start, -5000, -5000, 1000, 1000, 500, 500);

        Assert.Equal(0, right.X);
        Assert.Equal(0, right.Y);
        Assert.Equal(-500, left.X);
        Assert.Equal(-500, left.Y);
    }

    [Fact]
    public void Zoom_caps_extreme_scales()
    {
        var start = new ImageViewportMath.View(1, 0, 0);
        var minimum = ImageViewportMath.Zoom(start, 0.00001, 0, 0, 1000, 1000, 500, 500);
        var maximum = ImageViewportMath.Zoom(start, 10000, 0, 0, 1000, 1000, 500, 500);

        Assert.Equal(ImageViewportMath.MinimumScale, minimum.Scale);
        Assert.Equal(ImageViewportMath.MaximumScale, maximum.Scale);
    }
}
