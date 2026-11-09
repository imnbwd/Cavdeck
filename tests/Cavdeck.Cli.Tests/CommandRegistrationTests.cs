using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class CommandRegistrationTests
{
    [Theory]
    [InlineData("connection")]
    [InlineData("bucket")]
    [InlineData("object")]
    [InlineData("transfer")]
    [InlineData("ls")]
    [InlineData("stat")]
    [InlineData("upload")]
    [InlineData("download")]
    [InlineData("cp")]
    [InlineData("mv")]
    [InlineData("rm")]
    [InlineData("presign")]
    public async Task RegisteredCommandsRetainHelp(string command)
    {
        var result = await CommandProcessTests.InvokeAsync(command, "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.Output);
        Assert.Empty(result.Error);
    }
}
