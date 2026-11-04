using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerContextDetailTests
{
    private static readonly StorageConnection Connection = new(
        Guid.Empty, "My R2", StorageProviderKind.CloudflareR2,
        "https://example.r2.cloudflarestorage.com", "auto", "credential");

    private static ExplorerRow File(string name, string key) =>
        new(new ObjectEntry(ObjectEntryKind.File, name, key, 150,
            DateTimeOffset.UtcNow, null), new LocalizationService());

    private static ExplorerRow Folder(string name, string key) =>
        new(new ObjectEntry(ObjectEntryKind.Folder, name, key, 0,
            null, null), new LocalizationService());

    [Fact]
    public void Nested_prefix_is_a_current_folder_without_invented_metadata()
    {
        var texts = new LocalizationService();
        var detail = ExplorerContextDetailFactory.ForCurrentLocation(
            Connection, "assets", "images/2026/", texts);

        Assert.Equal(ExplorerDetailScope.Location, detail.Scope);
        Assert.Equal("2026", detail.Name);
        Assert.Equal(texts["Explorer_DetailsCurrentFolder"], detail.Summary);
        Assert.Equal("assets", detail.Bucket);
        Assert.Equal("images/2026/", detail.Key);
        Assert.Equal("s3://assets/images/2026/", detail.Uri);
        Assert.Equal(texts["Explorer_Prefix"], detail.KeyLabel);
        Assert.True(detail.ShowLocation);
        Assert.True(detail.ShowBucket);
        Assert.True(detail.ShowKey);
        Assert.True(detail.ShowUri);
        Assert.True(detail.ShowNameCopy);
        Assert.False(detail.ShowFileFacts);
        Assert.False(detail.ShowHeaders);
        Assert.False(detail.ShowEtag);
        Assert.Equal("", detail.Size);
        Assert.Equal("", detail.Modified);
    }

    [Fact]
    public void Bucket_root_keeps_a_valid_s3_uri_without_a_fake_prefix()
    {
        var detail = ExplorerContextDetailFactory.ForCurrentLocation(
            Connection, "assets", "", new LocalizationService());

        Assert.Equal("assets", detail.Name);
        Assert.Equal(ExplorerDetailScope.Location, detail.Scope);
        Assert.False(detail.ShowKey);
        Assert.True(detail.ShowBucket);
        Assert.True(detail.ShowUri);
        Assert.Equal("s3://assets/", detail.Uri);
        Assert.False(detail.ShowFileFacts);
    }

    [Fact]
    public void Restricted_prefix_can_still_be_displayed_as_current_folder()
    {
        var detail = ExplorerContextDetailFactory.ForCurrentLocation(
            Connection, "assets", "restricted/path/", new LocalizationService());

        Assert.Equal("path", detail.Name);
        Assert.Equal("restricted/path/", detail.Key);
        Assert.Equal("s3://assets/restricted/path/", detail.Uri);
    }

    [Fact]
    public void Bucket_list_shows_connection_context_without_object_actions()
    {
        var detail = ExplorerContextDetailFactory.ForCurrentLocation(
            Connection, null, "", new LocalizationService());

        Assert.Equal("My R2", detail.Name);
        Assert.Equal(ExplorerDetailScope.Connection, detail.Scope);
        Assert.False(detail.ShowLocation);
        Assert.False(detail.ShowKey);
        Assert.False(detail.ShowUri);
        Assert.False(detail.ShowBucket);
        Assert.True(detail.ShowNameCopy);
        Assert.False(detail.ShowFileFacts);
    }

    [Fact]
    public void Multi_selection_counts_only_selected_loaded_entries()
    {
        var texts = new LocalizationService();
        var detail = ExplorerContextDetailFactory.ForSelection(
            [File("a.png", "a.png"), Folder("2026", "2026/"), File("b.png", "b.png")], texts);

        Assert.Equal(ExplorerDetailScope.Selection, detail.Scope);
        Assert.Equal(string.Format(texts["Explorer_StatusSelected"], 3), detail.Name);
        Assert.Equal(string.Format(texts["Explorer_DetailsSelectionBreakdown"], 2, 1), detail.Summary);
        Assert.False(detail.ShowNameCopy);
        Assert.False(detail.ShowLocation);
        Assert.False(detail.ShowBucket);
        Assert.False(detail.ShowKey);
        Assert.False(detail.ShowUri);
        Assert.False(detail.ShowFileFacts);
        Assert.Equal("", detail.Size);
        Assert.Equal("", detail.Uri);
    }

    [Fact]
    public void Context_factory_does_not_accept_zero_or_single_selection()
    {
        var texts = new LocalizationService();
        Assert.Throws<ArgumentException>(() =>
            ExplorerContextDetailFactory.ForSelection([], texts));
        Assert.Throws<ArgumentException>(() =>
            ExplorerContextDetailFactory.ForSelection([File("one.txt", "one.txt")], texts));
    }
}
