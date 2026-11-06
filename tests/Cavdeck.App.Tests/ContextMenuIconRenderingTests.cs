using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml.Linq;
using Xunit;
using WpfApplication = System.Windows.Application;

namespace Cavdeck.App.Tests;

public sealed class ContextMenuIconRenderingTests
{
    [Fact]
    public void Icons_actually_render_with_visible_strokes_in_both_themes_and_disabled_states()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Run a real WPF template and rasterization test on its required STA thread.
                // Static XAML checks alone cannot catch invisible icons caused by broken bindings.
                var app = new WpfApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LoadAppResources(app);
                Assert.True(app.Resources.MergedDictionaries.Count > 0);

                VerifyTheme(app, "Colors.Light.xaml");
                ExplorerConnectionReconnectTests.VerifyGoToPathHeaderLayout();
                GoToPathDialogChecks.Verify(app, accept: true);
                app.Resources.MergedDictionaries[0] = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Cavdeck;component/Themes/Colors.Dark.xaml")
                };
                VerifyTheme(app, "Colors.Dark.xaml");
                ExplorerConnectionReconnectTests.VerifyGoToPathHeaderLayout();
                GoToPathDialogChecks.Verify(app, accept: false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF icon rendering test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void LoadAppResources(WpfApplication application)
    {
        // Load the production resources without starting connection or transfer services.
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckApp.xaml"));
        var dictionary = app.Root!.Element(wpf + "Application.Resources")!.Element(wpf + "ResourceDictionary")!;
        var baseUri = new Uri("pack://application:,,,/Cavdeck;component/App.xaml");
        foreach (var source in dictionary.Element(wpf + "ResourceDictionary.MergedDictionaries")!.Elements())
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(baseUri, source.Attribute("Source")!.Value)
            });
        }
        foreach (var element in dictionary.Elements(wpf + "Style"))
        {
            var style = (Style)XamlReader.Parse(element.ToString(), new ParserContext { BaseUri = baseUri });
            application.Resources.Add(style.TargetType, style);
        }
    }

    private static void VerifyTheme(WpfApplication app, string theme)
    {
        var foreground = (SolidColorBrush)app.FindResource("PrimaryTextBrush");
        var danger = (SolidColorBrush)app.FindResource("DangerTextBrush");
        Assert.NotEqual(Colors.Transparent, foreground.Color);
        Assert.NotEqual(Colors.Transparent, danger.Color);

        AssertIcon(app, "IconCopy", "ContextMenuIconStyle", foreground.Color, enabled: true, theme);
        // Redrawn menu glyphs must survive actual WPF rasterization in both themes.
        AssertIcon(app, "IconShare", "ContextMenuIconStyle", foreground.Color, enabled: true, theme);
        AssertIcon(app, "IconCut", "ContextMenuIconStyle", foreground.Color, enabled: true, theme);
        AssertIcon(app, "IconPaste", "ContextMenuIconStyle", foreground.Color, enabled: false, theme);
        AssertIcon(app, "IconOpenNewTab", "ContextMenuIconStyle", foreground.Color, enabled: true, theme);
        AssertIcon(app, "IconDownload", "ContextMenuIconStyle", foreground.Color, enabled: false, theme);
        AssertIcon(app, "IconTrash", "ContextMenuDangerIconStyle", danger.Color, enabled: true, theme);
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            AssertIcon(app, "IconGoToPath", "ContextMenuIconStyle", foreground.Color, enabled: true, theme, dpi);
            AssertIcon(app, "IconGoToPath", "ContextMenuIconStyle", foreground.Color, enabled: false, theme, dpi);
        }
    }

    private static void AssertIcon(
        WpfApplication app, string geometryKey, string iconStyleKey,
        Color expectedStroke, bool enabled, string theme, int dpi = 96)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = (Geometry)app.FindResource(geometryKey),
            Style = (Style)app.FindResource(iconStyleKey)
        };
        var item = new MenuItem
        {
            Header = geometryKey,
            Icon = path,
            IsEnabled = enabled,
            Style = (Style)app.FindResource(typeof(MenuItem))
        };

        item.Measure(new Size(240, 32));
        item.Arrange(new Rect(0, 0, 240, 32));
        item.ApplyTemplate();
        item.UpdateLayout();

        var iconHost = (ContentPresenter?)item.Template.FindName("IconHost", item);
        Assert.NotNull(iconHost);
        Assert.Same(path, iconHost.Content);

        var stroke = Assert.IsType<SolidColorBrush>(path.Stroke);
        Assert.Equal(expectedStroke, stroke.Color);
        Assert.True(path.Data!.Bounds.Width > 0);
        Assert.True(path.Data.Bounds.Height > 0);

        var width = 240 * dpi / 96;
        var height = 32 * dpi / 96;
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(item);

        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var visibleIconPixels = 0;
        // The glyph is in the left 40 DIPs. Header text starts in the next column.
        for (var y = 2 * dpi / 96; y < 30 * dpi / 96; y++)
        for (var x = 12 * dpi / 96; x < 38 * dpi / 96; x++)
            if (pixels[(y * width + x) * 4 + 3] > 12)
                visibleIconPixels++;

        Assert.True(visibleIconPixels >= 8,
            $"{geometryKey} ({theme}, {dpi} DPI, enabled={enabled}): no visible icon pixels were rendered.");
    }
}
