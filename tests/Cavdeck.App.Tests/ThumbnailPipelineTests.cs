using Cavdeck.App.Features.Explorer;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ThumbnailPipelineTests
{
    [Fact]
    public void Lru_evicts_the_oldest_entry_and_respects_a_byte_budget()
    {
        var cache = new ThumbnailLruCache<string, string>(6);
        cache.Put("a", "A", 2);
        cache.Put("b", "B", 2);
        cache.Put("c", "C", 2);
        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("A", value);

        cache.Put("d", "D", 3);
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.False(cache.TryGet("c", out _));
        Assert.Equal(5, cache.Bytes);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Lru_replacements_and_oversized_entries_cannot_exceed_budget()
    {
        var cache = new ThumbnailLruCache<int, string>(5);
        cache.Put(1, "one", 3);
        cache.Put(1, "replacement", 4);
        Assert.Equal(1, cache.Count);
        Assert.Equal(4, cache.Bytes);
        cache.Put(2, "too large", 6);
        Assert.False(cache.TryGet(2, out _));
        Assert.Equal(4, cache.Bytes);

        cache.Clear();
        Assert.Equal(0, cache.Bytes);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Lru_can_cache_a_negative_result()
    {
        var cache = new ThumbnailLruCache<string, object?>(3);
        cache.Put("unreadable-image", null, 1);
        Assert.True(cache.TryGet("unreadable-image", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void File_eligibility_filters_non_images_folders_and_large_sources()
    {
        Assert.True(ThumbnailPipeline.IsEligible(Entry("photo.PNG", 1024)));
        Assert.True(ThumbnailPipeline.IsEligible(Entry("photo.webp", ThumbnailPipeline.MaximumSourceBytes)));
        Assert.False(ThumbnailPipeline.IsEligible(Entry("huge.png", ThumbnailPipeline.MaximumSourceBytes + 1)));
        Assert.False(ThumbnailPipeline.IsEligible(Entry("empty.jpg", 0)));
        Assert.False(ThumbnailPipeline.IsEligible(Entry("logo.svg", 4096)));
        Assert.False(ThumbnailPipeline.IsEligible(Entry("text.txt", 4096)));
        Assert.False(ThumbnailPipeline.IsEligible(Entry("folder.png", 4096, ObjectEntryKind.Folder)));
    }

    private static ObjectEntry Entry(
        string name, long size, ObjectEntryKind kind = ObjectEntryKind.File) =>
        new(kind, name, name, size, DateTimeOffset.UtcNow, "etag");
}
