namespace Cavdeck.Domain.Transfers;

public static class TransferPlanner
{
    public const long S3MinimumPartSize = 5L * 1024 * 1024;
    public const int S3MaximumParts = 10_000;

    public static bool UseMultipart(long size, long threshold) => size > threshold && size > 0;

    public static IReadOnlyList<TransferPart> Plan(long size, long preferredPart, long minimumPart, int maxParts)
    {
        if (size <= 0)
            return [];
        if (preferredPart < 1)
            preferredPart = minimumPart;
        if (minimumPart < 1)
            minimumPart = 1;
        if (maxParts < 1)
            maxParts = 1;

        var partSize = Math.Max(preferredPart, minimumPart);
        var count = (size + partSize - 1) / partSize;
        if (count > maxParts)
            partSize = Math.Max(minimumPart, (size + maxParts - 1) / maxParts);

        var parts = new List<TransferPart>();
        long offset = 0;
        var number = 1;
        while (offset < size && number <= maxParts)
        {
            var length = Math.Min(partSize, size - offset);
            parts.Add(new TransferPart(number, offset, length, null));
            offset += length;
            number++;
        }

        if (offset != size)
            throw new InvalidOperationException("Part plan did not cover the object.");
        return parts;
    }
}
