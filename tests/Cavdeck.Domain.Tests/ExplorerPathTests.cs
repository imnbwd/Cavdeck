using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class ExplorerPathTests
{
    [Theory]
    [InlineData("assets/images/", false, "source", "scope/current/assets/images/", true)]
    [InlineData("assets/images/", true, "assets", "scope/images/", true)]
    [InlineData("assets\\images\\", true, "assets", "scope/images/", true)]
    [InlineData("s3://assets", false, "assets", "scope/", true)]
    [InlineData("S3://assets/", true, "assets", "scope/", true)]
    [InlineData("s3://assets/scope/資料%20#?.pdf ", false, "assets", "scope/資料%20#?.pdf ", false)]
    [InlineData("s3://assets/scope/literal\\file", false, "assets", "scope/literal\\file", false)]
    public void Parses_explicit_bucket_targets_without_changing_ordinary_paths(
        string text, bool bucketPath, string bucket, string key, bool directoryOnly)
    {
        var issue = ExplorerPath.ParseTarget(new(text, bucketPath), "source", "scope/", "scope/current/",
            out var resolvedBucket, out var resolvedPath);

        Assert.Equal(ExplorerPathIssue.None, issue);
        Assert.Equal(bucket, resolvedBucket);
        Assert.Equal(new ExplorerPath(key, directoryOnly), resolvedPath);
    }

    [Theory]
    [InlineData("s3://")]
    [InlineData("s3:///folder")]
    [InlineData("s3://user@assets/folder")]
    [InlineData("s3://assets:443/folder")]
    [InlineData("s3://assets/../outside")]
    [InlineData("s3://assets/folder//file")]
    [InlineData("s3://assets/literal\\folder/file")]
    public void Rejects_malformed_uri_or_unbrowsable_parent(string input) =>
        Assert.Equal(ExplorerPathIssue.Invalid,
            ExplorerPath.ParseTarget(new(input), "source", "", "", out _, out _));

    [Theory]
    [InlineData("photos/", "scope/images/2026/photos/", true)]
    [InlineData("  photos\\Avatar  ", "scope/images/2026/photos/Avatar", false)]
    [InlineData("/資料/大きい 写真.png", "scope/資料/大きい 写真.png", false)]
    [InlineData("/ space /a<*?>/", "scope/ space /a<*?>/", true)]
    [InlineData("/", "scope/", true)]
    [InlineData("\\", "scope/", true)]
    public void Parses_bucket_paths_without_changing_key_segments(string input, string key, bool directory)
    {
        var issue = ExplorerPath.Parse(input, "scope/", "scope/images/2026/", out var path);

        Assert.Equal(ExplorerPathIssue.None, issue);
        Assert.Equal(new ExplorerPath(key, directory), path);
    }

    [Theory]
    [InlineData(" ", ExplorerPathIssue.Required)]
    [InlineData(".", ExplorerPathIssue.Invalid)]
    [InlineData("/../secret", ExplorerPathIssue.Invalid)]
    [InlineData("folder/./file", ExplorerPathIssue.Invalid)]
    [InlineData("folder//file", ExplorerPathIssue.Invalid)]
    [InlineData("folder//", ExplorerPathIssue.Invalid)]
    [InlineData("a\tb", ExplorerPathIssue.Invalid)]
    [InlineData("C:\\folder", ExplorerPathIssue.Unsupported)]
    [InlineData("D:/folder", ExplorerPathIssue.Unsupported)]
    [InlineData("\\\\server\\share", ExplorerPathIssue.Unsupported)]
    [InlineData("s3://other-bucket/a", ExplorerPathIssue.Unsupported)]
    public void Rejects_unsupported_or_ambiguous_paths(string input, ExplorerPathIssue expected) =>
        Assert.Equal(expected, ExplorerPath.Parse(input, "scope/", "scope/", out _));

    [Fact]
    public void Limit_counts_the_effective_key_and_trailing_separator_in_UTF8()
    {
        var input = new string('界', 340);
        Assert.Equal(ExplorerPathIssue.None, ExplorerPath.Parse(input, "", "abc/", out var file));
        Assert.False(ExplorerPath.FitsKey(file.Key + "/"));
        Assert.Equal(ExplorerPathIssue.TooLong, ExplorerPath.Parse(input + "/", "", "abc/", out _));
        Assert.Equal(ExplorerPathIssue.None, ExplorerPath.Parse(input + "/", "", "", out _));
        Assert.Equal(ExplorerPathIssue.Invalid, ExplorerPath.Parse("\uD800", "", "", out _));
    }
}
