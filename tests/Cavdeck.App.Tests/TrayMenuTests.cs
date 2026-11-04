using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Cavdeck.App;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TrayMenuTests
{
    [Fact]
    public void Menu_model_updates_summary_tooltip_and_existing_actions()
    {
        var calls = new List<string>();
        var model = new TrayMenuViewModel(new LocalizationService(),
            () => calls.Add("open"), () => calls.Add("transfers"),
            () => calls.Add("connections"), () => calls.Add("favorites"),
            () => calls.Add("settings"), () => calls.Add("exit"));
        Assert.Equal(0, model.TransferCount);
        Assert.False(model.HasTransfers);
        model.Refresh(new TransferTraySummary(2, 3, 1, 1));
        Assert.Equal(6, model.TransferCount);
        Assert.True(model.HasTransfers);
        Assert.True(model.HasFailures);
        Assert.Contains("2", model.Status);
        Assert.True(model.Tooltip.Length <= 63);
        model.OpenCommand.Execute(null);
        model.TransfersCommand.Execute(null);
        model.ConnectionsCommand.Execute(null);
        model.FavoritesCommand.Execute(null);
        model.SettingsCommand.Execute(null);
        model.ExitCommand.Execute(null);
        Assert.Equal(["open", "transfers", "connections", "favorites", "settings", "exit"], calls);
    }

    [Fact]
    public void Tray_update_action_is_available_only_for_actionable_versions()
    {
        var calls = 0;
        var model = new TrayMenuViewModel(new LocalizationService(),
            () => { }, () => { }, () => { }, () => { },
            () => { }, () => { }, () => calls++);

        Assert.False(model.HasUpdate);
        model.SetUpdate(Cavdeck.Application.Abstractions.UpdateStatus.Available, "1.0.1");
        Assert.True(model.HasUpdate);
        Assert.Contains("1.0.1", model.UpdateText);
        model.UpdatesCommand.Execute(null);
        Assert.Equal(1, calls);

        model.SetUpdate(Cavdeck.Application.Abstractions.UpdateStatus.Downloading, "1.0.1");
        Assert.False(model.HasUpdate);
        model.SetUpdate(Cavdeck.Application.Abstractions.UpdateStatus.Downloaded, "1.0.1");
        Assert.True(model.HasUpdate);
        Assert.Contains("1.0.1", model.UpdateText);
    }

    [Fact]
    public void Tray_resource_registers_localized_commands_icons_and_theme_brushes()
    {
        var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckApp.xaml"));
        var tray = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckTrayMenu.xaml"));
        var xml = tray.ToString();
        Assert.Contains(app.Descendants(), element =>
            (string?)element.Attribute("Source") == "Styles/TrayMenu.xaml");
        Assert.Contains("CavdeckBrandImage", xml);
        Assert.Contains("{DynamicResource PrimaryTextBrush}", xml);
        Assert.Contains("{DynamicResource MutedTextBrush}", xml);
        // Tray resources live across merged dictionaries: these must defer
        // resolution until the menu participates in the application resource scope.
        foreach (var key in new[]
                 {
                     "TrayIconStyle", "CavdeckBrandImage", "IconTrayOpen",
                     "IconTransfers", "IconConnections", "IconStar",
                     "IconInfo", "IconSettings", "IconTrayExit"
                 })
        {
            Assert.Contains($"{{DynamicResource {key}}}", xml);
            Assert.DoesNotContain($"{{StaticResource {key}}}", xml);
        }

        foreach (var command in new[] { "Open", "Transfers", "Connections", "Favorites", "Settings", "Exit" })
            Assert.Contains($"{{Binding {command}Command, Mode=OneWay}}", xml);
        Assert.Equal(7, tray.Descendants().Count(element => element.Name.LocalName == "MenuItem"));
        Assert.Contains("{Binding UpdatesCommand, Mode=OneWay}", xml);
        Assert.Contains("{Binding HasUpdate, Converter={StaticResource BoolVis}}", xml);
    }
    [Fact]
    public void Tray_icon_style_resolves_its_base_inside_the_icons_dictionary()
    {
        // StaticResource in a deferred Style.BasedOn must not depend on a
        // sibling merged dictionary or on the shell window's resources.
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfResourceTestHost.EnsurePackUriSupport();
                var icons = new ResourceDictionary
                {
                    Source = AppResourceUri.Relative("Themes/Icons.xaml")
                };
                var baseStyle = Assert.IsType<Style>(icons["IconGlyph"]);
                var trayStyle = Assert.IsType<Style>(icons["TrayIconStyle"]);
                var inherited = Assert.IsType<Style>(trayStyle.BasedOn);
                Assert.Equal(baseStyle.TargetType, inherited.TargetType);
                Assert.Equal(baseStyle.Setters.Count, inherited.Setters.Count);
                Assert.Single(trayStyle.Setters);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }

    [Fact]
    public void Tray_menu_loads_from_application_resources_without_a_shell_window()
    {
        // FindResource materializes the deferred ContextMenu XAML, including StaticResource bindings.
        // A converter defined only in ShellWindow must not be required by this detached menu.
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Load the same App.xaml dictionaries without creating another
                // Application in the test runner's AppDomain.
                var resources = WpfResourceTestHost.LoadApplicationResources();
                // The tray icon style must inherit the shared glyph style even
                // when loaded outside the main window resource lookup scope.
                var glyphStyle = Assert.IsType<Style>(resources["IconGlyph"]);
                var trayIconStyle = Assert.IsType<Style>(resources["TrayIconStyle"]);
                var baseStyle = Assert.IsType<Style>(trayIconStyle.BasedOn);
                Assert.Equal(glyphStyle.TargetType, baseStyle.TargetType);
                Assert.Equal(glyphStyle.Setters.Count, baseStyle.Setters.Count);
                var menu = Assert.IsType<ContextMenu>(resources["TrayContextMenu"]);
                Assert.Equal(7, menu.Items.OfType<MenuItem>().Count());
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }

    [Fact]
    public void Detached_tray_menu_replaces_its_theme_palette_without_stale_brushes()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var menu = new ContextMenu();
                menu.Resources["CustomMenuSetting"] = 42;
                menu.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");

                var lightBrush = new SolidColorBrush(Colors.White);
                var darkBrush = new SolidColorBrush(Colors.Black);
                var light = new ResourceDictionary { ["SurfaceBrush"] = lightBrush };
                var dark = new ResourceDictionary { ["SurfaceBrush"] = darkBrush };

                var palette = TrayIconService.ReplaceMenuPalette(menu, null, light);
                Assert.Same(lightBrush, menu.Background);

                palette = TrayIconService.ReplaceMenuPalette(menu, palette, dark);
                Assert.Same(darkBrush, menu.Background);
                Assert.Single(menu.Resources.MergedDictionaries);
                Assert.Equal(42, menu.FindResource("CustomMenuSetting"));

                palette = TrayIconService.ReplaceMenuPalette(menu, palette, light);
                Assert.Same(lightBrush, menu.Background);
                Assert.Single(menu.Resources.MergedDictionaries);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.Dark.xaml")]
    public void Tray_theme_colors_keep_menu_labels_and_status_readable(string theme)
    {
        var colors = XDocument.Load(Path.Combine(AppContext.BaseDirectory, theme))
            .Descendants().Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(element => (string)element.Attribute(XName.Get("Key",
                "http://schemas.microsoft.com/winfx/2006/xaml"))!,
                element => (string)element.Attribute("Color")!);

        Assert.True(Contrast(colors["PrimaryTextBrush"], colors["SurfaceBrush"]) >= 4.5);
        Assert.True(Contrast(colors["PrimaryTextBrush"], colors["HoverBrush"]) >= 4.5);
        Assert.True(Contrast(colors["MutedTextBrush"], colors["SurfaceBrush"]) >= 4.5);
        Assert.True(Contrast(colors["DangerTextBrush"], colors["SurfaceBrush"]) >= 4.5);
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var channels = new[] { 1, 3, 5 }.Select(offset =>
            {
                var v = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255d;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }).ToArray();
            return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Fact]
    public void Tray_menu_uses_shared_visual_rhythm_and_wraps_status_text()
    {
        var tray = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckTrayMenu.xaml"));
        var wpf = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var contextStyle = tray.Descendants(wpf + "Style").Single(node =>
            (string?)node.Attribute(xaml + "Key") == "TrayContextMenuStyle");
        Assert.Contains(contextStyle.Elements(wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Padding"
            && (string?)setter.Attribute("Value") == "6");
        Assert.Contains(contextStyle.Elements(wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "UseLayoutRounding"
            && (string?)setter.Attribute("Value") == "True");

        var itemStyle = tray.Descendants(wpf + "Style").Single(node =>
            (string?)node.Attribute(xaml + "Key") == "TrayMenuItemStyle");
        Assert.Contains(itemStyle.Elements(wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Padding"
            && (string?)setter.Attribute("Value") == "13,0");
        Assert.Contains(itemStyle.Elements(wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "MinHeight"
            && (string?)setter.Attribute("Value") == "36");

        var header = tray.Descendants(wpf + "Style").Single(node =>
            (string?)node.Attribute(xaml + "Key") == "TrayHeaderStyle");
        Assert.Equal("12,6,12,8", (string?)header.Descendants(wpf + "Grid").First().Attribute("Margin"));
        var status = header.Descendants(wpf + "TextBlock").Single(node =>
            (string?)node.Attribute("Text") == "{Binding Status, Mode=OneWay}");
        Assert.Equal("Wrap", (string?)status.Attribute("TextWrapping"));
        Assert.Equal("228", (string?)status.Attribute("MaxWidth"));
    }

}
