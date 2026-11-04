using Cavdeck.Domain.Transfers;

namespace Cavdeck.App.Features.Transfers;

// Presentation-only filtering. The engine and durable journal remain the source of truth.
public enum TransferStatusFilter { All, Running, Queued, Paused, Failed, Completed, Cancelled }

internal static class TransferViewRules
{
    internal static bool Matches(TransferState state, bool history, TransferStatusFilter filter)
    {
        var inTab = history
            ? state is TransferState.Completed or TransferState.Cancelled
            : state is not (TransferState.Completed or TransferState.Cancelled);
        if (!inTab)
            return false;
        return filter switch
        {
            TransferStatusFilter.All => true,
            TransferStatusFilter.Running => state == TransferState.Running,
            TransferStatusFilter.Queued => state is TransferState.Queued or TransferState.WaitingRetry,
            TransferStatusFilter.Paused => state == TransferState.Paused,
            TransferStatusFilter.Failed => state == TransferState.Failed,
            TransferStatusFilter.Completed => state == TransferState.Completed,
            TransferStatusFilter.Cancelled => state == TransferState.Cancelled,
            _ => false
        };
    }
}

// Never render or copy raw provider exception details: they can contain URLs, file paths,
// signed query parameters or secrets. A short provider identifier is safe to surface.
internal static class TransferFailureDiagnostics
{
    public static string? SafeCode(TransferFailure? failure)
    {
        var code = failure?.Code;
        return !string.IsNullOrEmpty(code) && code.Length <= 64
            && char.IsAsciiLetter(code[0])
            && code.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '-' or '.') ? code : null;
    }

    public static string CopyText(TransferFailure? failure, int attempt)
    {
        var kind = failure?.Kind.ToString() ?? nameof(TransferFailureKind.Unknown);
        return $"Category: {kind}\nCode: {SafeCode(failure) ?? "Unavailable"}\n" +
            $"Retryable: {failure?.Retryable == true}\nAttempts: {attempt}";
    }
}

// A quiet-period notification buffer prevents one toast per file in large transfer batches.
// A snapshot is read only after the queue has been idle for a short period.
internal sealed class TransferNoticeBuffer
{
    private int _completed;
    private int _skipped;
    private int _failed;
    private string? _oneCompletedName;
    private string? _oneFailedName;

    internal void AddCompleted(string name, bool enabled = true)
    {
        if (!enabled) return;
        _completed++;
        _oneCompletedName = _completed == 1 ? name : null;
    }

    internal void AddSkipped(bool enabled = true)
    {
        if (enabled) _skipped++;
    }

    internal void AddFailed(string name)
    {
        _failed++;
        _oneFailedName = _failed == 1 ? name : null;
    }

    internal (int Completed, int Skipped, int Failed, string? CompletedName, string? FailedName) Drain(bool enabled = true)
    {
        // Honor changes during the quiet window; do not suppress failure alerts.
        var snapshot = (enabled ? _completed : 0, enabled ? _skipped : 0,
            _failed, enabled ? _oneCompletedName : null, _oneFailedName);
        _completed = 0;
        _skipped = 0;
        _failed = 0;
        _oneCompletedName = null;
        _oneFailedName = null;
        return snapshot;
    }
}
