using Cavdeck.App.ViewModels;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task Running_command_disables_repeat_execution_and_restores_can_execute()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var states = new List<bool>();
        var command = new AsyncRelayCommand(async _ =>
        {
            executions++;
            started.SetResult(true);
            await finish.Task;
        });
        command.CanExecuteChanged += (_, _) => states.Add(command.CanExecute(null));

        var active = command.ExecuteAsync(null);
        await started.Task;
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));

        await command.ExecuteAsync(null);
        Assert.Equal(1, executions);

        finish.SetResult(true);
        await active;
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
        Assert.Collection(states, value => Assert.False(value), value => Assert.True(value));
    }

    [Fact]
    public async Task Unexpected_error_is_reported_once_without_escaping_the_command()
    {
        var exception = new InvalidOperationException("a private object path");
        var notifications = new List<Exception>();
        var command = new AsyncRelayCommand(_ => Task.FromException(exception), notifications.Add);

        await command.ExecuteAsync(null);

        Assert.Single(notifications);
        Assert.Same(exception, notifications[0]);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Synchronous_exception_from_command_handler_is_observed()
    {
        Exception? reported = null;
        var command = new AsyncRelayCommand(_ => throw new InvalidOperationException("failed"),
            exception => reported = exception);

        await command.ExecuteAsync(null);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task User_cancellation_is_silent_and_command_can_run_again()
    {
        var notifications = 0;
        var command = new AsyncRelayCommand(
            _ => Task.FromCanceled(new CancellationToken(canceled: true)),
            _ => notifications++);

        await command.ExecuteAsync(null);
        await command.ExecuteAsync(null);

        Assert.Equal(0, notifications);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Error_feedback_failure_is_observed_without_masking_original_error()
    {
        var command = new AsyncRelayCommand(
            _ => throw new InvalidOperationException("original"),
            _ => throw new InvalidOperationException("toast error"));

        await command.ExecuteAsync(null);

        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Task_runner_observes_event_errors_and_ignores_cancellation()
    {
        var errors = new List<Exception>();
        await UiAsyncTask.RunAsync(
            () => Task.FromException(new IOException("unavailable")), errors.Add);
        await UiAsyncTask.RunAsync(
            () => Task.FromCanceled(new CancellationToken(canceled: true)), errors.Add);

        Assert.Single(errors);
        Assert.IsType<IOException>(errors[0]);
    }
}
