using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class UpdateNotificationUiTests
{
    [Fact]
    public void Toast_host_supports_optional_action_without_changing_dismiss()
    {
        var file = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckToastHost.xaml"));
        var xml = file.ToString();
        Assert.Contains("{Binding ActionCommand}", xml);
        Assert.Contains("{Binding HasAction, Converter={StaticResource BoolVis}}", xml);
        Assert.Contains("{Binding DismissCommand}", xml);
    }

    [Fact]
    public void All_supported_languages_have_update_notification_labels()
    {
        var names = new[]
        {
            "Update_NoticeAvailableTitle", "Update_NoticeReadyTitle",
            "Update_ViewDetails", "Update_TrayAvailable", "Update_TrayReady"
        };
        foreach (var resource in new[] { "Strings.resx", "Strings.zh-CN.resx",
                     "Strings.ja-JP.resx", "Strings.de-DE.resx" })
        {
            var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Localization", resource));
            var keys = xml.Descendants("data")
                .Select(element => (string?)element.Attribute("name")).ToHashSet();
            foreach (var name in names)
                Assert.Contains(name, keys);
        }
    }
}
