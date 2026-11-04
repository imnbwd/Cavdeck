using System.Xml.Linq;
using Cavdeck.App.Features.Settings;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class AboutPageTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void About_resources_keep_existing_actions_and_offer_localized_support()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckSettings.xaml"));
        var tab = Assert.Single(view.Descendants(Wpf + "TabItem"), t =>
            ((string?)t.Attribute("Header"))?.Contains("Settings_TabAbout") == true);
        var attrs = string.Join(" ", tab.DescendantsAndSelf().Attributes().Select(a => a.Value));
        foreach (var name in new[] { "CheckUpdatesCommand", "CopyDiagnosticCommand", "OpenLogsCommand",
                     "OpenProjectPageCommand", "OpenDocumentationCommand", "ReportIssueCommand",
                     "OpenLicenseCommand", "About_PrivacySummary", "About_LicenseSummary" })
            Assert.Contains(name, attrs);
        Assert.DoesNotContain("http://", attrs);
        Assert.DoesNotContain("https://", attrs);
        Assert.DoesNotContain("About_OfficialWebsitePending", attrs);
    }

    [Theory]
    [InlineData(0, "https://github.com/imnbwd/Cavdeck")]
    [InlineData(1, "https://github.com/imnbwd/Cavdeck/blob/main/README.md")]
    [InlineData(2, "https://github.com/imnbwd/Cavdeck/issues/new")]
    [InlineData(3, "https://github.com/imnbwd/Cavdeck/blob/main/LICENSE")]
    public void Resource_links_are_known_https_project_routes(int resource, string expected)
    {
        var uri = AboutResourceLinks.GetUri((AboutResource)resource);
        Assert.Equal(expected, uri.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal("github.com", uri.Host);
    }

    [Fact]
    public void Unknown_resource_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AboutResourceLinks.GetUri((AboutResource)999));
}
