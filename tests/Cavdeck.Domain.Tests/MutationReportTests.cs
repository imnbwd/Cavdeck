using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class MutationReportTests
{
    [Theory]
    [InlineData(1, 0, 0, false, true)]
    [InlineData(1, 1, 0, false, false)]
    [InlineData(1, 0, 1, false, false)]
    [InlineData(0, 0, 0, false, false)]
    [InlineData(0, 0, 0, true, false)]
    public void Clipboard_completion_requires_every_item_to_finish(
        int succeeded, int skipped, int failed, bool blocked, bool expected)
    {
        var report = new MutationReport(succeeded, skipped, failed, blocked, 0, []);
        Assert.Equal(expected, report.IsFullSuccess);
    }
}
