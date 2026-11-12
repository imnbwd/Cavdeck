using Cavdeck.App.Features.Diagnostics;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class DiagnosticLogTests
{
    [Fact]
    public void Default_diagnostic_logs_are_outside_the_installer_directory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var log = new DiagnosticLog();

        Assert.Equal(Path.Combine(localAppData, "CavdeckData", "Logs"), log.DirectoryPath);
        Assert.NotEqual(Path.Combine(localAppData, "Cavdeck"), Path.GetDirectoryName(log.DirectoryPath));
    }

    [Fact]
    public void Media_playback_events_include_only_allowlisted_state_and_numeric_hresult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cavdeck-media-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(directory);
            const string secret = "signed-url private-object-key local-temp-path";
            log.RecordMediaPlaybackEvent("MediaFailed", new InvalidOperationException(secret));
            log.RecordMediaPlaybackEvent("MediaOpenTimeout");
            log.RecordMediaPlaybackEvent(secret);

            var report = log.CreateReport();
            Assert.Contains("media-playback | MediaFailed | 0x80131509", report);
            Assert.Contains("media-playback | MediaOpenTimeout", report);
            Assert.DoesNotContain(secret, report);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReportOmitsExceptionTextAndRejectsTamperedLogLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cavdeck-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(directory);
            const string secret = "AKIAEXAMPLESECRET signed-token local-file-name";
            var id = log.Record("ui", new InvalidOperationException(secret));
            log.RecordTransferFailure(TransferFailureKind.Network);
            var path = Directory.GetFiles(directory, "*.log").Single();
            Assert.DoesNotContain(secret, File.ReadAllText(path));
            File.AppendAllText(path, $"2026-01-01T00:00:00Z | AAAAAAAAAAAA | ui | {secret}{Environment.NewLine}");

            var report = log.CreateReport();
            Assert.Contains(id, report);
            Assert.Contains("InvalidState", report);
            Assert.Contains("transfer | Network", report);
            Assert.DoesNotContain(secret, report);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
