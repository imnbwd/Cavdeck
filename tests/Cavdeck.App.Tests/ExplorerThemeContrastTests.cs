using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerThemeContrastTests
{
    [Theory]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.Dark.xaml")]
    public void Batch_toolbar_text_and_selection_remain_readable(string theme)
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, theme));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var colors = xml.Descendants()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(element => (string)element.Attribute(xaml + "Key")!,
                element => (string)element.Attribute("Color")!);

        Assert.True(Contrast(colors["PrimaryTextBrush"], colors["SurfaceBrush"]) >= 4.5);
        Assert.True(Contrast(colors["PrimaryTextBrush"], colors["SelectedBrush"]) >= 4.5);
        Assert.True(Contrast(colors["MutedTextBrush"], colors["SurfaceBrush"]) >= 4.5);
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var channels = new[] { 1, 3, 5 }.Select(offset =>
            {
                var value = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }).ToArray();
            return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
