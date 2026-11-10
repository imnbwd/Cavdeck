using Cavdeck.Application.Preview;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class PreviewSessionTests
{
    [Fact]
    public void Begin_cancels_previous_operation_and_late_completion_cannot_clear_current()
    {
        using var session = new PreviewSession();
        var first = session.Begin(Id("first"));
        var second = session.Begin(Id("second"));

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(session.IsCurrent(first));
        Assert.True(session.IsCurrent(second));
        Assert.False(session.Complete(first));
        Assert.True(session.IsCurrent(second));
        Assert.True(session.Complete(second));
        Assert.False(session.IsCurrent(second));
    }

    [Fact]
    public void CancelCurrent_requests_cancellation_without_invalidating_operation()
    {
        using var session = new PreviewSession();
        var operation = session.Begin(Id("current"));

        session.CancelCurrent();

        Assert.True(operation.Token.IsCancellationRequested);
        Assert.True(session.IsCurrent(operation));
        Assert.True(session.Complete(operation));
    }

    [Fact]
    public void Reset_cancels_and_invalidates_current_operation()
    {
        using var session = new PreviewSession();
        var operation = session.Begin(Id("current"));

        session.Reset();

        Assert.True(operation.Token.IsCancellationRequested);
        Assert.False(session.IsCurrent(operation));
        Assert.False(session.Complete(operation));
    }

    [Fact]
    public void Dispose_is_idempotent_and_prevents_new_operations()
    {
        var session = new PreviewSession();
        var operation = session.Begin(Id("current"));

        session.Dispose();
        session.Dispose();

        Assert.True(operation.Token.IsCancellationRequested);
        Assert.False(session.IsCurrent(operation));
        Assert.Throws<ObjectDisposedException>(() => session.Begin(Id("next")));
        Assert.False(session.Complete(operation));
    }

    [Fact]
    public void Cancel_reset_and_completion_can_race_without_disposed_token_source_errors()
    {
        using var session = new PreviewSession();
        var operation = session.Begin(Id("current"));

        var exception = Record.Exception(() => Parallel.Invoke(
            session.CancelCurrent,
            session.Reset,
            () => session.Complete(operation)));

        Assert.Null(exception);
    }

    private static PreviewObjectId Id(string key) => new(Guid.NewGuid(), "assets", key);
}
