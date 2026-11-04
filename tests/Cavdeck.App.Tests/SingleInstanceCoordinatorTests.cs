using Cavdeck.App.Services;
using Xunit;

namespace Cavdeck.App.Tests;

#pragma warning disable xUnit1031 // Mutex ownership requires acquisition and release on this test thread.
public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void Second_instance_activates_primary_and_mutex_is_released()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        using (var first = new SingleInstanceCoordinator(scope))
        {
            Assert.True(first.IsPrimary);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Listen(() => received.TrySetResult());
            var activated = Task.Run(async () =>
            {
                using var second = new SingleInstanceCoordinator(scope);
                Assert.False(second.IsPrimary);
                return await second.ActivatePrimaryAsync();
            }).GetAwaiter().GetResult();
            Assert.True(activated);
            received.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        using var next = new SingleInstanceCoordinator(scope);
        Assert.True(next.IsPrimary);
    }
}
#pragma warning restore xUnit1031
