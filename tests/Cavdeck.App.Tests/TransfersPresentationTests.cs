using System.Globalization;
using System.Resources;
using System.Xml.Linq;
using Cavdeck.App.Features.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TransfersPresentationTests
{
    [Fact]
    public void Transfer_toolbar_uses_clear_language_in_both_supported_locales()
    {
        var resourceManager = new ResourceManager(
            "Cavdeck.App.Resources.Strings", typeof(TransfersView).Assembly);
        foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP", "de-DE" })
        {
            var locale = CultureInfo.GetCultureInfo(culture);
            foreach (var key in new[]
            {
                "Transfer_Concurrency", "Transfer_ConcurrencyHelp",
                "Transfer_ConcurrencySaveFailed", "Transfer_ActionFailed",
                "Transfer_BatchProgress", "Transfer_ClearFailed",
                "Transfer_ClearHistory", "Transfer_RemoveRecord",
                "Transfer_FailureCategory", "Transfer_ErrorCode", "Transfer_CopyFailure"
            })
                Assert.False(string.IsNullOrWhiteSpace(resourceManager.GetString(key, locale)));
        }

        Assert.Equal("Transfers at once", resourceManager.GetString(
            "Transfer_Concurrency", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("同时传输数", resourceManager.GetString(
            "Transfer_Concurrency", CultureInfo.GetCultureInfo("zh-CN")));
    }

    [Fact]
    public void Transfer_toolbar_preserves_accessible_help_and_explicit_one_way_progress()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckTransfersView.xaml"));
        var concurrency = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "ComboBox" &&
            ((string?)element.Attribute("ItemsSource"))?.Contains("ConcurrencyChoices") == true);

        Assert.Equal("{Binding Transfers.Concurrency, Mode=TwoWay}",
            (string?)concurrency.Attribute("SelectedItem"));
        Assert.Equal("{Binding Texts[Transfer_ConcurrencyHelp]}",
            (string?)concurrency.Attribute("ToolTip"));

        var progress = document.Descendants().Where(element =>
            element.Name.LocalName == "ProgressBar").ToArray();
        Assert.Equal(2, progress.Length);
        Assert.Contains(progress, bar => (string?)bar.Attribute("Value")
            == "{Binding Progress, Mode=OneWay}");
        Assert.Contains(progress, bar => (string?)bar.Attribute("Value")
            == "{Binding ProgressPercent, Mode=OneWay}" &&
               (string?)bar.Attribute("IsIndeterminate")
            == "{Binding IsProgressIndeterminate, Mode=OneWay}");
    }
}
