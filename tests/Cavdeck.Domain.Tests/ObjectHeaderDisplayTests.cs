using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class ObjectHeaderDisplayTests
{
    [Fact]
    public void Headers_apply_to_files_only()
    {
        Assert.True(ObjectHeaderDisplay.AppliesTo(ObjectEntryKind.File));
        Assert.False(ObjectHeaderDisplay.AppliesTo(ObjectEntryKind.Folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_header_is_an_em_dash(string? header) =>
        Assert.Equal("—", ObjectHeaderDisplay.Value(header));

    [Theory]
    [InlineData(" image/png ", "image/png")]
    [InlineData("public, max-age=3600", "public, max-age=3600")]
    public void Header_value_is_trimmed(string header, string expected) =>
        Assert.Equal(expected, ObjectHeaderDisplay.Value(header));
}
