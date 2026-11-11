namespace Cavdeck.App.Features.Preview;

/// <summary>Small unit-testable helpers shared by the playback controls.</summary>
public static class MediaPlaybackMath
{
    public static TimeSpan Seek(TimeSpan current, double seconds, TimeSpan duration)
    {
        if (!double.IsFinite(seconds) || duration <= TimeSpan.Zero)
            return current;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration.TotalSeconds));
    }

    public static TimeSpan Skip(TimeSpan current, double deltaSeconds, TimeSpan duration) =>
        Seek(current, current.TotalSeconds + deltaSeconds, duration);

    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }
}
