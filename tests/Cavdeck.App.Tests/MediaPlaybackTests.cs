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

    [Theory]
    [InlineData(0.75, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(1.5, true)]
    [InlineData(2.0, true)]
    [InlineData(0, false)]
    [InlineData(3.0, false)]
    [InlineData(double.NaN, false)]
    public void Speed_options_are_bounded(double ratio, bool expected) =>
        Assert.Equal(expected, MediaPlaybackMath.IsSupportedSpeed(ratio));

    [Theory]
    [InlineData(true, false, 2.9, false)]
    [InlineData(true, false, 3.0, true)]
    [InlineData(true, true, 5.0, false)]
    [InlineData(false, false, 10.0, false)]
    public void Fullscreen_controls_hide_only_when_playing_and_idle(
        bool playing, bool interacting, double seconds, bool expected) =>
        Assert.Equal(expected, MediaPlaybackMath.ShouldHideControls(
            playing, interacting, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Unknown_duration_and_invalid_seek_values_do_not_change_position()
    {
        var position = TimeSpan.FromSeconds(10);
        Assert.Equal(position, MediaPlaybackMath.Seek(position, 5, TimeSpan.Zero));
        Assert.Equal(position, MediaPlaybackMath.Seek(position, double.NaN, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void Open_timeout_allows_only_one_automatic_retry(int retryCount, bool expected) =>
        Assert.Equal(expected, MediaPlaybackMath.ShouldRetryAfterOpenTimeout(retryCount));

    [Fact]
    public void Invalidating_a_failed_open_rejects_late_native_callbacks()
    {
        var activeAttempt = 3;
        var callbackAttempt = activeAttempt;
        Assert.True(MediaPlaybackMath.IsCurrentAttempt(activeAttempt, callbackAttempt));

        activeAttempt++;
        Assert.False(MediaPlaybackMath.IsCurrentAttempt(activeAttempt, callbackAttempt));
    }

    [Fact]
    public void Time_label_distinguishes_opening_from_unknown_duration()
    {
        Assert.Equal("--:-- / --:--",
            MediaPlaybackMath.TimeLabel(TimeSpan.Zero, TimeSpan.Zero, ready: false));
        Assert.Equal("00:00 / --:--",
            MediaPlaybackMath.TimeLabel(TimeSpan.Zero, TimeSpan.Zero, ready: true));
        Assert.Equal("00:12 / 05:56",
            MediaPlaybackMath.TimeLabel(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(356), ready: true));
    }
}
