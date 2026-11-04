using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class MultipartCopyPlanTests
{
    [Theory]
    [InlineData(5L * 1024 * 1024 * 1024 + 1)]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024)]
    public void Large_copy_ranges_cover_the_object_without_gaps_or_excess_parts(long size)
    {
        var ranges = MultipartCopyPlan.Ranges(size).ToArray();

        Assert.InRange(ranges.Length, 2, 10_000);
        Assert.Equal(0, ranges[0].First);
        Assert.Equal(size - 1, ranges[^1].Last);
        for (var index = 0; index < ranges.Length; index++)
        {
            Assert.InRange(ranges[index].Last - ranges[index].First + 1, 1, 5L * 1024 * 1024 * 1024);
            if (index > 0)
                Assert.Equal(ranges[index - 1].Last + 1, ranges[index].First);
        }
    }
}
