using System.Windows.Input;
using Cavdeck.App.Features.Diagnostics;

namespace Cavdeck.App.ViewModels;

public sealed class AsyncRelayCommand(
    Func<object?, Task> execute, Action<Exception>? onError = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running;
    public bool CanExecute(object? parameter) => !_running;

    // ICommand requires void; ExecuteAsync exposes completion for callers and tests.
    public async void Execute(object? parameter)
    {
        try { await ExecuteAsync(parameter); }
        catch (Exception exception)
        {
            // Even a misbehaving CanExecuteChanged subscriber must not crash WPF.
            DiagnosticLog.Current.Queue("async-command", exception);
        }
    }

    public async Task ExecuteAsync(object? parameter)
    {
        if (_running)
            return;
        _running = true;
        try
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await UiAsyncTask.RunAsync(() => execute(parameter), onError, "async-command");
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
