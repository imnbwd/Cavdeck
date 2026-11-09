using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class StorageUriTests
{
    [Theory]
    [InlineData("s3://bucket/", "", true)]
    [InlineData("s3://bucket/file.txt", "file.txt", false)]
    [InlineData("s3://bucket/a/b.txt", "a/b.txt", false)]
    [InlineData("s3://bucket/a/", "a/", true)]
    [InlineData("s3://bucket/a//b.txt", "a//b.txt", false)]
    [InlineData("s3://bucket/文件.txt", "文件.txt", false)]
    [InlineData("s3://bucket/ a%2F#?\\b ", " a%2F#?\\b ", false)]
    public void PreservesKey(string address, string key, bool slash) =>
        Assert.Equal(new StorageUri("bucket", key, slash), StorageUri.Parse(address));

    [Theory]
    [InlineData("https://bucket/key")]
    [InlineData("s3:///key")]
    [InlineData("s3://bucket/a/../b")]
    [InlineData("s3://bucket//leading-slash")]
    [InlineData("s3://bucket/a\nb")]
    public void RejectsInvalidAddress(string address) =>
        Assert.Throws<CliException>(() => StorageUri.Parse(address));

    [Fact]
    public void ChecksUtf8Bytes() =>
        Assert.Throws<CliException>(() => StorageUri.Parse("s3://bucket/" + new string('文', 341)));
}
