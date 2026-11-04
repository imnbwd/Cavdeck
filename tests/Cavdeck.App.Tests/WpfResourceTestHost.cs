using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;
using Cavdeck.App.Services;

namespace Cavdeck.App.Tests;

/// <summary>
/// Loads App.xaml's resource dictionary without constructing a second WPF Application.
/// </summary>
internal static class WpfResourceTestHost
{
    public static void EnsurePackUriSupport()
    {
        // Accessing Application runs its type initializer, which registers WPF's
        // pack://application URI handlers. Do not construct an Application here:
        // WPF permits only one Application instance per AppDomain.
        _ = System.Windows.Application.Current;
    }

    public static ResourceDictionary LoadApplicationResources()
    {
        EnsurePackUriSupport();

        var appXaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckApp.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = appXaml.Root?
            .Element(presentation + "Application.Resources")?
            .Element(presentation + "ResourceDictionary")
            ?? throw new InvalidOperationException("App.xaml does not contain a root resource dictionary.");

        // The ResourceDictionary inherits xmlns declarations from Application.
        // Preserve them when parsing it as a standalone XAML root; WPF style
        // TargetType values such as "Window" need the default XML namespace.
        foreach (var declaration in appXaml.Root!.Attributes()
                     .Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);

        var sources = resources.Descendants(presentation + "ResourceDictionary")
            .Attributes("Source")
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidOperationException("App.xaml does not declare merged resource dictionaries.");

        // Load the original App.xaml dictionary as one XAML tree. Loading each
        // Source first and attaching its predecessors afterwards loses the
        // parse-time StaticResource scope (e.g. TrayIconStyle -> IconGlyph).
        // Absolute pack URIs preserve the app assembly when parsing detached XAML.
        foreach (var source in sources)
            source.Value = AppResourceUri.Absolute(source.Value).AbsoluteUri;

        using var reader = resources.CreateReader();
        return (ResourceDictionary)XamlReader.Load(reader);
    }
}
