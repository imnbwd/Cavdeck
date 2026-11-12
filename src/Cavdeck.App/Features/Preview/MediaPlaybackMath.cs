namespace Cavdeck.App.Features.Preview;

/// <summary>Small unit-testable helpers shared by the playback controls.</summary>
public static class MediaPlaybackMath
{
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);
    public const int AutomaticOpenRetryLimit = 1;

    public static TimeSpan Seek(TimeSpan current, double seconds, TimeSpan duration)
    {
        if (!double.IsFinite(seconds) || duration <= TimeSpan.Zero)
            return current;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration.TotalSeconds));
    }

    public static TimeSpan Skip(TimeSpan current, double deltaSeconds, TimeSpan duration) =>
        Seek(current, current.TotalSeconds + deltaSeconds, duration);

    public static bool ShouldRetryAfterOpenTimeout(int automaticRetryCount) =>
        automaticRetryCount < AutomaticOpenRetryLimit;

    public static bool IsCurrentAttempt(int activeAttempt, int callbackAttempt) =>
        activeAttempt == callbackAttempt;

    public static bool IsSupportedSpeed(double ratio) =>
        ratio is 0.75 or 1.0 or 1.25 or 1.5 or 2.0;

    public static bool ShouldHideControls(bool playing, bool interacting, TimeSpan idle) =>
        playing && !interacting && idle >= TimeSpan.FromSeconds(3);

    public static string TimeLabel(TimeSpan current, TimeSpan duration, bool ready)
    {
        if (!ready)
            return "--:-- / --:--";

        var durationText = duration > TimeSpan.Zero ? Clock(duration) : "--:--";
        return $"{Clock(current)} / {durationText}";
    }

    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }
}
