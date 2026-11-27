using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class ObjectMetadataTests
{
    private static ObjectMetadataValues Values(string? contentType = "text/plain", string? expires = null,
        IReadOnlyDictionary<string, string>? custom = null) =>
        new(new(contentType, null, null, null, null, expires), custom ?? new Dictionary<string, string>());

    [Fact]
    public void Validation_rejects_duplicate_keys_and_control_characters()
    {
        Assert.Equal(MetadataValidation.DuplicateKey, ObjectMetadataRules.Validate(Values(custom:
            new Dictionary<string, string> { ["Project"] = "a", ["project"] = "b" })));
        Assert.Equal(MetadataValidation.InvalidHeader, ObjectMetadataRules.Validate(Values("text/plain\r\nInjected: yes")));
        Assert.Equal(MetadataValidation.InvalidKey, ObjectMetadataRules.Validate(Values(custom:
            new Dictionary<string, string> { ["x-amz-meta-test"] = "a" })));
        Assert.Equal(MetadataValidation.TooLarge, ObjectMetadataRules.Validate(Values(custom:
            new Dictionary<string, string> { ["k"] = new string('a', 2048) })));
        Assert.Equal(MetadataValidation.InvalidExpires, ObjectMetadataRules.Validate(Values(expires: "tomorrow")));
        Assert.Equal(MetadataValidation.None, ObjectMetadataRules.Validate(Values(expires: "Wed, 21 Oct 2026 07:28:00 GMT")));
    }

    [Fact]
    public void Metadata_only_changes_are_not_identical_revisions()
    {
        var original = new ObjectMetadataSnapshot(new("assets", "a.txt", 4, "etag", "text/plain"),
            DateTimeOffset.UtcNow, "STANDARD", "AES256", null, null, Values(), MetadataEditBlock.None);
        Assert.False(original.SameRevision(original with { Values = Values(custom:
            new Dictionary<string, string> { ["project"] = "other" }) }));
        Assert.True(original.SameRevision(original with { Values = Values() }));
    }

    [Fact]
    public void R2_is_edit_capable_but_unknown_compatible_providers_are_read_only()
    {
        Assert.True(ConnectionPlanner.Capabilities(StorageProviderKind.CloudflareR2).CanEditObjectMetadata);
        Assert.False(ConnectionPlanner.Capabilities(StorageProviderKind.S3Compatible).CanEditObjectMetadata);
    }
}
