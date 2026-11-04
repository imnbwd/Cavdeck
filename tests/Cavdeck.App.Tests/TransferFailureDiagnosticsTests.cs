using System.Xml.Linq;
using Cavdeck.App.Features.Transfers;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TransferFailureDiagnosticsTests
{
    [Theory]
    [InlineData("AccessDenied", "AccessDenied")]
    [InlineData("SignatureDoesNotMatch", "SignatureDoesNotMatch")]
    [InlineData("https://signed.example/?token=secret", null)]
    [InlineData(@"C:\private\key", null)]
    [InlineData("Space and credentials", null)]
    [InlineData("", null)]
    public void Only_short_provider_identifiers_are_safe_to_render(string code, string? expected)
    {
        var failure = new TransferFailure(TransferFailureKind.Unknown, false, code, "raw detail");
        Assert.Equal(expected, TransferFailureDiagnostics.SafeCode(failure));
    }

    [Fact]
    public void Clipboard_diagnostics_never_include_raw_detail_or_paths()
    {
        var failure = new TransferFailure(TransferFailureKind.Unknown, false,
            "https://signed.example/?token=secret", "password=topsecret; C:\\private\\keys");
        var copied = TransferFailureDiagnostics.CopyText(failure, 3);
        Assert.Contains("Unknown", copied);
        Assert.Contains("Unavailable", copied);
        Assert.DoesNotContain("topsecret", copied);
        Assert.DoesNotContain("signed.example", copied);
        Assert.DoesNotContain("private", copied);
    }

    [Fact]
    public void Transfer_page_exposes_record_cleanup_and_failure_diagnostics()
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckTransfersView.xaml"));
        var commands = doc.Descendants().Attributes("Command")
            .Select(attribute => attribute.Value).ToArray();
        foreach (var action in new[] { "ClearFailedCommand", "ClearFinishedCommand",
                     "RemoveRecordCommand", "CopyFailureCommand" })
            Assert.Contains(commands, command => command.Contains(action, StringComparison.Ordinal));
        Assert.Contains(doc.Descendants().Attributes("Text"),
            attr => attr.Value.Contains("FailureCode", StringComparison.Ordinal));
    }
}
