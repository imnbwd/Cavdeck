using System.Globalization;
using System.Xml.Linq;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Features.Transfers;
using Cavdeck.App.Localization;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ErrorLocalizationTests
{
    [Fact]
    public void Normal_error_messages_exclude_provider_prose_but_keep_technical_identifiers()
    {
        var texts = new LocalizationService();
        var failure = new StorageFailure(StorageFailureKind.AccessDenied,
            "AccessDenied", 403, "request-123", "Untranslated server prose with a path");
        var identifiers = ConnectionText.Technical(texts, failure);
        var visible = ExplorerText.FailureDetail(texts, failure);

        Assert.Contains("AccessDenied", identifiers);
        Assert.Contains("403", identifiers);
        Assert.Contains("request-123", identifiers);
        Assert.DoesNotContain("Untranslated server prose", identifiers);
        Assert.DoesNotContain("Untranslated server prose", visible);
        Assert.Contains("request-123", visible);
        Assert.Contains("Untranslated server prose",
            ConnectionText.Technical(texts, failure, includeProviderDetail: true));
    }

    [Fact]
    public void Batch_error_is_localized_and_raw_reason_only_appears_in_technical_detail()
    {
        var originalUi = CultureInfo.DefaultThreadCurrentUICulture;
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            var texts = new LocalizationService();
            var batch = MakeBatch("AccessDenied:AccessDenied");
            var row = new CrossBatchRowViewModel(batch, texts);

            foreach (var language in new[] { "en-US", "zh-CN" })
            {
                texts.ChangeLanguage(language);
                row.RefreshText();
                Assert.Equal(texts["Connection_Failure_AccessDenied"], row.Error);
                Assert.Equal("AccessDenied: AccessDenied", row.TechnicalError);
                Assert.DoesNotContain("AccessDenied:AccessDenied", row.Error!);
            }

            row.Apply(MakeBatch("IOException"), new CrossBatchProgress(0, 0, 0, 0, 0, 0));
            Assert.Equal(texts["Transfer_BatchScanError"], row.Error);
            Assert.Equal("IOException", row.TechnicalError);

            row.Apply(MakeBatch(null) with { State = CrossBatchState.Completed },
                new CrossBatchProgress(0, 0, 0, 0, 0, 0));
            Assert.Null(row.Error);
            Assert.Null(row.TechnicalError);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = originalUi;
            CultureInfo.DefaultThreadCurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Connection_result_and_exception_notices_retranslate_when_language_changes()
    {
        var originalUi = CultureInfo.DefaultThreadCurrentUICulture;
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            var texts = new LocalizationService();
            var connection = new StorageConnection(Guid.NewGuid(), "Test", StorageProviderKind.AwsS3,
                "", "us-east-1", "credential-ref");
            var item = new ConnectionItemViewModel(connection);

            item.BeginTest(texts);
            Assert.Equal(texts["Connection_Testing"], item.Status);
            item.ApplyUnexpectedTestFailure(texts);
            Assert.Equal(texts["Connection_TestUnexpectedHint"], item.StatusDetail);
            Assert.Equal(texts["Connection_Failure_Unknown"], item.Status);

            texts.ChangeLanguage("zh-CN");
            item.Refresh(texts);
            Assert.Equal(texts["Connection_TestUnexpectedHint"], item.StatusDetail);
            Assert.Equal(texts["Connection_Failure_Unknown"], item.Status);

            var probe = StorageProbeResult.Failed(new StorageFailure(
                StorageFailureKind.AccessDenied, "AccessDenied", 403, null, "Remote English error"));
            item.ApplyTestResult(probe, null, texts);
            Assert.Equal(texts["Connection_Failure_AccessDenied"], item.Status);
            Assert.Equal(texts["Connection_Failure_AccessDeniedHint"], item.StatusDetail);

            texts.ChangeLanguage("en-US");
            item.Refresh(texts);
            Assert.Equal(texts["Connection_Failure_AccessDenied"], item.Status);
            Assert.Equal(texts["Connection_Failure_AccessDeniedHint"], item.StatusDetail);
            Assert.DoesNotContain("Remote English error", item.StatusDetail);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = originalUi;
            CultureInfo.DefaultThreadCurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Transfer_sizes_use_the_same_culture_aware_format_as_explorer()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var job = TransferJob.Create(TransferKind.Download, Guid.NewGuid(), "bucket",
                "object.bin", "object.bin", 2048, TransferConflictPolicy.Skip) with
            {
                State = TransferState.Running,
                BytesTransferred = 1536
            };
            var row = new TransferRowViewModel(job, new LocalizationService());

            Assert.Equal($"{ExplorerText.FormatBytes(1536)} / {ExplorerText.FormatBytes(2048)}",
                row.DetailText);
            Assert.Contains("1,5", row.DetailText);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Unknown_transfer_state_never_exposes_enum_name()
    {
        var texts = new LocalizationService();
        var job = TransferJob.Create(TransferKind.Download, Guid.NewGuid(), "bucket",
            "object.bin", "object.bin", 1024, TransferConflictPolicy.Skip) with
        {
            State = (TransferState)99
        };
        var row = new TransferRowViewModel(job, texts);
        Assert.Contains(texts["Transfer_UnknownState"], row.StatusText);
        Assert.DoesNotContain("99", row.StatusText);
    }

    [Fact]
    public void Batch_failure_shows_localized_copy_and_technical_hover_detail()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckTransfersView.xaml"));
        var label = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{Binding Error}");
        Assert.Equal("{Binding TechnicalError}", (string?)label.Attribute("ToolTip"));
    }

    private static CrossTransferBatch MakeBatch(string? error) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "source", "destination", "",
            [], TransferConflictPolicy.Skip, 0, null, null, 0, CrossBatchState.Failed,
            error, DateTimeOffset.UtcNow);
}
