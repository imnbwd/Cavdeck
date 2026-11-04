using Cavdeck.Application.Sharing;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class ShareLinksTests
{
    [Fact]
    public void Public_link_escapes_each_object_key_segment_without_including_bucket()
    {
        var link = ShareLinks.Public("https://cdn.example.com/assets/", "photos/a b/#1.png");

        Assert.Equal("https://cdn.example.com/assets/photos/a%20b/%231.png", link.Url);
        Assert.True(link.IsPublic);
        Assert.Null(link.ExpiresAt);
    }

    [Fact]
    public void Public_link_preserves_dot_segments_as_object_key_data()
    {
        var link = ShareLinks.Public("https://cdn.example.com", "archive/../file.txt");

        Assert.Equal("https://cdn.example.com/archive/%2E%2E/file.txt", link.Url);
    }

    [Theory]
    [InlineData("http://cdn.example.com")]
    [InlineData("https://cdn.example.com/?token=abc")]
    [InlineData("https://user:pass@cdn.example.com")]
    [InlineData("https://cdn.example.com/#section")]
    public void Public_bucket_root_rejects_unsafe_or_ambiguous_addresses(string input)
    {
        Assert.False(ShareLinks.TryPublicBase(input, out _));
    }

    [Fact]
    public void Temporary_link_lifetime_is_bounded_to_seven_days()
    {
        Assert.False(ShareLinks.ValidLifetime(TimeSpan.Zero));
        Assert.True(ShareLinks.ValidLifetime(TimeSpan.FromMinutes(1)));
        Assert.True(ShareLinks.ValidLifetime(TimeSpan.FromDays(7)));
        Assert.False(ShareLinks.ValidLifetime(TimeSpan.FromDays(7).Add(TimeSpan.FromSeconds(1))));
    }
}
