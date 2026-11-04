using Cavdeck.App.Features.Diagnostics;

namespace Cavdeck.App.ViewModels;

/// <summary>
/// Observes tasks started by UI event handlers or commands. Cancellation is not a
/// failure; unexpected errors are logged without exposing exception text to the UI.
/// </summary>
public static class UiAsyncTask
{
    public static async Task RunAsync(
        Func<Task> operation, Action<Exception>? onError = null, string area = "ui")
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // Navigation, closing a tab or cancelling a transfer are normal UI actions.
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue(area, exception);
            if (onError is null)
                return;
            try
            {
                onError(exception);
            }
            catch (Exception feedbackError)
            {
                // Error feedback must never become another unobserved UI exception.
                DiagnosticLog.Current.Queue(area, feedbackError);
            }
        }
    }
}
