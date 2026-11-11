using Cavdeck.App.Features.Preview;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class MediaPlaybackTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(6, "00:06")]
    [InlineData(62, "01:02")]
    [InlineData(3601, "1:00:01")]
    public void Clock_formats_short_and_long_playback(double seconds, string expected) =>
        Assert.Equal(expected, MediaPlaybackMath.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Seeking_and_skipping_are_clamped_to_the_duration()
    {
        var duration = TimeSpan.FromSeconds(30);
        Assert.Equal(TimeSpan.Zero,
            MediaPlaybackMath.Seek(TimeSpan.FromSeconds(10), -50, duration));
        Assert.Equal(duration,
            MediaPlaybackMath.Seek(TimeSpan.FromSeconds(10), 999, duration));
        Assert.Equal(TimeSpan.FromSeconds(5),
            MediaPlaybackMath.Skip(TimeSpan.FromSeconds(10), -5, duration));
        Assert.Equal(duration,
            MediaPlaybackMath.Skip(TimeSpan.FromSeconds(29), 5, duration));
    }

    [Fact]
    public void Unknown_duration_and_invalid_seek_values_do_not_change_position()
    {
        var position = TimeSpan.FromSeconds(10);
        Assert.Equal(position, MediaPlaybackMath.Seek(position, 5, TimeSpan.Zero));
        Assert.Equal(position, MediaPlaybackMath.Seek(position, double.NaN, TimeSpan.FromMinutes(1)));
    }
}
