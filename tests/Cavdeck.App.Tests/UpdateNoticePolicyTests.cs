using Cavdeck.App.Features.Updates;
using Cavdeck.Application.Abstractions;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class UpdateNoticePolicyTests
{
    [Fact]
    public void Notifies_once_per_version_and_stage()
    {
        var policy = new UpdateNoticePolicy();
        Assert.Equal(UpdateNoticeKind.Available, policy.Take(UpdateStatus.Available, "1.0.1"));
        Assert.Null(policy.Take(UpdateStatus.Available, "1.0.1"));
        Assert.Equal(UpdateNoticeKind.Ready, policy.Take(UpdateStatus.Downloaded, "1.0.1"));
        Assert.Null(policy.Take(UpdateStatus.Downloaded, "1.0.1"));
        Assert.Equal(UpdateNoticeKind.Available, policy.Take(UpdateStatus.Available, "1.0.2"));
    }

    [Fact]
    public void Ignores_progress_and_invalid_versions()
    {
        var policy = new UpdateNoticePolicy();
        Assert.Null(policy.Take(UpdateStatus.Checking, "1.0.1"));
        Assert.Null(policy.Take(UpdateStatus.Downloading, "1.0.1"));
        Assert.Null(policy.Take(UpdateStatus.Failed, "1.0.1"));
        Assert.Null(policy.Take(UpdateStatus.Downloaded, " "));
        Assert.Null(policy.Take(UpdateStatus.Available, null));
        Assert.Equal(UpdateNoticeKind.Ready, policy.Take(UpdateStatus.Downloaded, "1.0.1"));
    }
}
