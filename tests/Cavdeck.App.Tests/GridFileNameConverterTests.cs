using System.Globalization;
using Cavdeck.App.Features.Explorer;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class GridFileNameConverterTests
{
    private readonly GridFileNameConverter _converter = new();

    [Fact]
    public void Short_names_remain_complete()
    {
        Assert.Equal("01-cloud-foundations.md", Display("01-cloud-foundations.md", compact: false));
    }

    [Fact]
    public void Long_names_keep_the_extension_in_both_densities()
    {
        const string name = "case-study-template-production-observability-final.pptx";
        foreach (var compact in new[] { false, true })
        {
            var displayed = Display(name, compact);
            Assert.Contains('…', displayed);
            Assert.EndsWith(".pptx", displayed);
            Assert.StartsWith("case", displayed);
        }
    }

    [Fact]
    public void Long_chinese_names_keep_the_file_type()
    {
        var displayed = Display("超长项目架构方案与多环境部署操作指南最终版本.md", compact: true);
        Assert.EndsWith(".md", displayed);
        Assert.Contains('…', displayed);
    }

    private string Display(string name, bool compact) => (string)_converter.Convert(
        [name, compact], typeof(string), null!, CultureInfo.InvariantCulture);
}
