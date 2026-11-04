using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class ConnectionPlannerTests
{
    [Fact]
    public void R2_builds_the_account_endpoint_and_forces_path_style()
    {
        var draft = new ConnectionDraft
        {
            Name = " My R2 ",
            Provider = StorageProviderKind.CloudflareR2,
            AccountId = "AbcdEFGHijklmnop",
            ForcePathStyle = false
        };

        Assert.True(ConnectionPlanner.TryResolve(draft, out var target, out var issues));
        Assert.Empty(issues);
        Assert.Equal("https://abcdefghijklmnop.r2.cloudflarestorage.com", target!.Endpoint);
        Assert.Equal("auto", target.Region);
        Assert.True(target.ForcePathStyle);
        Assert.False(ConnectionPlanner.Capabilities(StorageProviderKind.CloudflareR2).CanReadObjectVersions);
    }

    [Fact]
    public void R2_rejects_an_account_id_that_is_really_a_url()
    {
        var draft = new ConnectionDraft
        {
            Name = "R2",
            Provider = StorageProviderKind.CloudflareR2,
            AccountId = "abc.example.com"
        };

        Assert.False(ConnectionPlanner.TryResolve(draft, out _, out var issues));
        Assert.Contains(issues, issue => issue.Field == ConnectionField.AccountId
            && issue.Code == ConnectionIssueCode.Invalid);
    }

    [Fact]
    public void Aws_uses_the_regional_endpoint_until_a_custom_one_is_set()
    {
        var draft = new ConnectionDraft
        {
            Name = "Prod",
            Provider = StorageProviderKind.AwsS3,
            Region = "US-EAST-1",
            ForcePathStyle = true
        };

        Assert.True(ConnectionPlanner.TryResolve(draft, out var target, out _));
        Assert.Equal("", target!.Endpoint);
        Assert.Equal("us-east-1", target.Region);
        Assert.False(target.ForcePathStyle);
    }

    [Fact]
    public void Custom_endpoint_rejects_userinfo_and_paths()
    {
        var withKey = new ConnectionDraft
        {
            Name = "MinIO",
            Provider = StorageProviderKind.Minio,
            Endpoint = "http://root:secret@127.0.0.1:9000"
        };
        Assert.False(ConnectionPlanner.TryResolve(withKey, out _, out var userInfo));
        Assert.Contains(userInfo, issue => issue.Code == ConnectionIssueCode.UserInfo);

        var withPath = new ConnectionDraft
        {
            Name = "MinIO",
            Provider = StorageProviderKind.Minio,
            Endpoint = "http://127.0.0.1:9000/bucket"
        };
        Assert.False(ConnectionPlanner.TryResolve(withPath, out _, out var path));
        Assert.Contains(path, issue => issue.Field == ConnectionField.Endpoint);
    }

    [Fact]
    public void Minio_keeps_http_and_normalizes_the_prefix()
    {
        var draft = new ConnectionDraft
        {
            Name = "Local",
            Provider = StorageProviderKind.Minio,
            Endpoint = "http://127.0.0.1:9000/",
            KeyPrefix = "\\images\\2026\\",
            ForcePathStyle = true
        };

        Assert.True(ConnectionPlanner.TryResolve(draft, out var target, out _));
        Assert.Equal("http://127.0.0.1:9000", target!.Endpoint);
        Assert.Equal("us-east-1", target.Region);
        Assert.Equal("images/2026/", target.KeyPrefix);
        Assert.True(target.ForcePathStyle);
    }

    [Fact]
    public void Prefix_cannot_climb_out()
    {
        var draft = new ConnectionDraft
        {
            Name = "Local",
            Provider = StorageProviderKind.Minio,
            Endpoint = "http://127.0.0.1:9000",
            KeyPrefix = "images/../secret"
        };

        Assert.False(ConnectionPlanner.TryResolve(draft, out _, out var issues));
        Assert.Contains(issues, issue => issue.Field == ConnectionField.Prefix);
    }

    [Fact]
    public void Secret_can_stay_stored_on_edit()
    {
        var issues = ConnectionPlanner.ValidateCredentials("AKIAEXAMPLE", secret: null, requireSecret: false);
        Assert.Empty(issues);

        var required = ConnectionPlanner.ValidateCredentials("AKIAEXAMPLE", secret: "  ", requireSecret: true);
        Assert.Contains(required, issue => issue.Code == ConnectionIssueCode.SecretRequired);
    }
}
