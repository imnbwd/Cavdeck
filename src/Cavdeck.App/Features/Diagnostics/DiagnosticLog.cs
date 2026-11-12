using System.Reflection;
using System.IO;
using Cavdeck.Domain.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.Configuration;
using System.Text;

namespace Cavdeck.App.Features.Diagnostics;

// Only allowlisted values reach disk or the clipboard. Exception messages and stacks can
// contain object keys, local paths, signed URLs, or credentials, so never serialize them.
public sealed class DiagnosticLog
{
    private readonly object _gate = new();
    private readonly string _directory;
    private const long MaxFileBytes = 1024 * 1024;
    private const int KeepDays = 7;
    private static readonly HashSet<string> Areas =
        ["startup", "process", "background-task", "ui", "async-command", "window-close", "tray", "instance-activation", "copy-diagnostic", "open-logs", "update-check", "connection-test", "config-recovery", "transfer", "media-playback", "other"];
    private static readonly HashSet<string> Categories =
        ["Storage", "IO", "Access", "Network", "Cancellation", "InvalidState", "Argument", "Other",
         "MediaDownloadCompleted", "MediaDownloadFailed", "MediaSourceAssigned", "MediaPlayRequested",
         "MediaOpened", "MediaFailed", "MediaOpenTimeout", "MediaAutoRetry", "MediaManualRetry",
         "MediaRetryExhausted", "MediaOpenStartFailed", "MediaReleaseFailed",
         .. Enum.GetNames<TransferFailureKind>()];
    private static readonly HashSet<string> MediaEvents =
        ["MediaDownloadCompleted", "MediaDownloadFailed", "MediaSourceAssigned", "MediaPlayRequested",
         "MediaOpened", "MediaFailed", "MediaOpenTimeout", "MediaAutoRetry", "MediaManualRetry",
         "MediaRetryExhausted", "MediaOpenStartFailed", "MediaReleaseFailed"];

    public static DiagnosticLog Current { get; } = new();
    public string DirectoryPath => _directory;

    public DiagnosticLog(string? directory = null)
    {
        _directory = directory ?? Path.Combine(CavdeckDataPaths.LocalDirectory, "Logs");
    }

    public string Record(string area, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Write(area, Category(exception));
    }

    public string RecordTransferFailure(TransferFailureKind kind) =>
        Write("transfer", kind.ToString());

    public string RecordMediaPlaybackEvent(string eventName, Exception? exception = null)
    {
        var category = MediaEvents.Contains(eventName) ? eventName : "Other";
        var hresult = exception is null ? null : $"0x{unchecked((uint)exception.HResult):X8}";
        return Write("media-playback", category, hresult);
    }

    // Routine diagnostics should never make the dispatcher wait for a busy disk.
    // Fatal handlers still call Record directly so their entry is flushed before exit.
    public void Queue(string area, Exception exception) =>
        _ = Task.Run(() => Record(area, exception));

    public void QueueTransferFailure(TransferFailureKind kind) =>
        _ = Task.Run(() => RecordTransferFailure(kind));

    private string Write(string area, string category, string? detail = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var line = $"{DateTimeOffset.UtcNow:O} | {id} | {(Areas.Contains(area) ? area : "other")} | {category}";
        if (detail is not null)
            line += $" | {detail}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"cavdeck-{DateTime.UtcNow:yyyy-MM-dd}.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
                    path = Path.Combine(_directory, $"cavdeck-{DateTime.UtcNow:yyyy-MM-dd}-overflow.log");
                if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes)
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                Prune();
            }
            catch (Exception) { /* Diagnostics must never cause another application failure. */ }
        }
        return id;
    }

    public string CreateReport()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var lines = new List<string>
        {
            "Cavdeck diagnostic report",
            $"Generated (UTC): {DateTimeOffset.UtcNow:O}",
            $"Version: {version}",
            $"OS: Windows {Environment.OSVersion.Version}",
            $"Runtime: {Environment.Version}",
            "Events (UTC | ID | area | category | optional HRESULT):"
        };
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    foreach (var path in Directory.GetFiles(_directory, "cavdeck-*.log")
                                 .OrderByDescending(File.GetLastWriteTimeUtc).Take(KeepDays * 2))
                    {
                        // All files are written by this allowlist formatter. Validate again in case
                        // an old or externally changed file contains arbitrary text.
                        foreach (var line in File.ReadLines(path).TakeLast(200))
                            if (IsSafeLine(line)) lines.Add(line);
                    }
                }
            }
            catch (Exception) { lines.Add("Log files unavailable."); }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void Prune()
    {
        foreach (var path in Directory.GetFiles(_directory, "cavdeck-*.log"))
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-KeepDays))
                File.Delete(path);
    }

    private static string Category(Exception exception) => exception switch
    {
        StorageException => "Storage",
        IOException => "IO",
        UnauthorizedAccessException => "Access",
        System.Net.Http.HttpRequestException => "Network",
        OperationCanceledException => "Cancellation",
        InvalidOperationException => "InvalidState",
        ArgumentException => "Argument",
        _ => "Other"
    };

    private static bool IsSafeLine(string line)
    {
        var parts = line.Split(" | ");
        return (parts.Length == 4 || parts.Length == 5) && DateTimeOffset.TryParse(parts[0], out _)
            && parts[1].Length == 12 && parts[1].All(char.IsAsciiHexDigit)
            && Areas.Contains(parts[2]) && Categories.Contains(parts[3])
            && (parts.Length == 4 || parts[4].Length == 10 && parts[4].StartsWith("0x", StringComparison.Ordinal)
                && parts[4].AsSpan(2).ToArray().All(char.IsAsciiHexDigit));
    }
}
