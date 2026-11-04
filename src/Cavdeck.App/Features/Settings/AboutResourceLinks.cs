namespace Cavdeck.App.Features.Settings;

// Links point to existing GitHub routes rather than a speculative official site.
internal enum AboutResource { ProjectPage, Documentation, ReportIssue, License }

internal static class AboutResourceLinks
{
    private const string Repository = "https://github.com/imnbwd/Cavdeck";

    internal static Uri GetUri(AboutResource resource) => resource switch
    {
        AboutResource.ProjectPage => new Uri(Repository),
        AboutResource.Documentation => new Uri(Repository + "/blob/main/README.md"),
        AboutResource.ReportIssue => new Uri(Repository + "/issues/new"),
        AboutResource.License => new Uri(Repository + "/blob/main/LICENSE"),
        _ => throw new ArgumentOutOfRangeException(nameof(resource))
    };
}
