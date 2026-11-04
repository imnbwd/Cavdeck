using System.Reflection;
using System.Threading;
using System.Windows;
using Cavdeck.App;
using Cavdeck.App.Services;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ApplicationIdentityTests
{
    [Fact]
    public void Wpf_application_assembly_uses_the_product_name()
    {
        var assembly = typeof(App).Assembly;

        Assert.Equal("Cavdeck", assembly.GetName().Name);
        Assert.Equal("Cavdeck", assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
        Assert.Equal("Cavdeck", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }

    [Theory]
    [InlineData("Themes/Colors.Light.xaml")]
    [InlineData("Themes/Colors.Dark.xaml")]
    [InlineData("Assets/Cavdeck.ico")]
    public void Wpf_resource_uris_follow_the_actual_assembly_name(string path)
    {
        WpfResourceTestHost.EnsurePackUriSupport();
        var name = typeof(App).Assembly.GetName().Name;

        Assert.Equal($"/{name};component/{path}", AppResourceUri.Relative(path).OriginalString);
        Assert.Equal($"pack://application:,,,/{name};component/{path}",
            AppResourceUri.Absolute(path).OriginalString);
    }

    [Theory]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.Dark.xaml")]
    public void Theme_palettes_load_from_the_compiled_assembly(string palette)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfResourceTestHost.EnsurePackUriSupport();
                var colors = new ResourceDictionary
                {
                    Source = AppResourceUri.Relative($"Themes/{palette}")
                };
                Assert.True(colors.Contains("WindowBackgroundBrush"));
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }
}
