namespace Cavdeck.Infrastructure.S3;

internal static class MultipartCopyPlan
{
    private const long MinimumPartSize = 128L * 1024 * 1024;
    private const int MaximumParts = 10_000;

    public static IEnumerable<(long First, long Last)> Ranges(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var partSize = Math.Max(MinimumPartSize, (length + MaximumParts - 1) / MaximumParts);
        for (long first = 0; first < length; first += partSize)
            yield return (first, Math.Min(length - 1, first + partSize - 1));
    }
}
