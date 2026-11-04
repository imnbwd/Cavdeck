using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class TransferRulesTests
{
    [Fact]
    public void Small_files_stay_a_single_put()
    {
        Assert.False(TransferPlanner.UseMultipart(8 * 1024 * 1024, 8 * 1024 * 1024));
        Assert.True(TransferPlanner.UseMultipart(8 * 1024 * 1024 + 1, 8 * 1024 * 1024));
        Assert.False(TransferPlanner.UseMultipart(0, 8));
    }

    [Fact]
    public void Part_plan_keeps_the_last_part_short_and_respects_the_part_ceiling()
    {
        var parts = TransferPlanner.Plan(20 * 1024 * 1024, 8 * 1024 * 1024, 5 * 1024 * 1024, 10_000);

        Assert.Equal(3, parts.Count);
        Assert.Equal((0L, 8L * 1024 * 1024), (parts[0].Offset, parts[0].Length));
        Assert.Equal(4L * 1024 * 1024, parts[2].Length);
        Assert.Equal(3, parts[2].Number);

        var hugeSize = 8L * 1024 * 1024 * 10_000 + 1;
        var huge = TransferPlanner.Plan(hugeSize, 8 * 1024 * 1024, 5 * 1024 * 1024, 10_000);
        Assert.Equal(10_000, huge.Count);
        Assert.Equal(hugeSize, huge.Sum(part => part.Length));
        Assert.All(huge.Take(huge.Count - 1), part => Assert.True(part.Length >= 5 * 1024 * 1024));
    }

    [Fact]
    public void Retry_waits_only_for_transient_failures_and_caps_the_delay()
    {
        var network = new TransferFailure(TransferFailureKind.Network, true, "Timeout", null);
        var denied = new TransferFailure(TransferFailureKind.AccessDenied, false, "AccessDenied", null);

        Assert.True(RetrySchedule.ShouldRetry(network, 1, 5));
        Assert.False(RetrySchedule.ShouldRetry(network, 5, 5));
        Assert.False(RetrySchedule.ShouldRetry(denied, 1, 5));
        Assert.Equal(TimeSpan.FromMilliseconds(500), RetrySchedule.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), RetrySchedule.Delay(20));
    }

    [Fact]
    public void Integrity_accepts_size_for_multipart_etags_and_rejects_a_mismatched_md5()
    {
        Assert.Equal(IntegrityCheck.Match, IntegrityRules.Verify(4, 4, "ABCDEFABCDEFABCDEFABCDEFABCDEFAB", "\"abcdefabcdefabcdefabcdefabcdefab\"", true));
        Assert.Equal(IntegrityCheck.Mismatch, IntegrityRules.Verify(4, 4, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "\"abcdefabcdefabcdefabcdefabcdefab\"", true));
        Assert.Equal(IntegrityCheck.SizeOnly, IntegrityRules.Verify(4, 4, "ABCDEFABCDEFABCDEFABCDEFABCDEFAB", "\"abc-2\"", true));
        Assert.Equal(IntegrityCheck.Mismatch, IntegrityRules.Verify(4, 5, "ABCDEFABCDEFABCDEFABCDEFABCDEFAB", "\"abc-2\"", true));
        Assert.Equal(IntegrityCheck.SizeOnly, IntegrityRules.Verify(4, 4, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "\"abcdefabcdefabcdefabcdefabcdefab\"", false));
        Assert.False(IntegrityRules.IsWholeObjectMd5("\"abc-2\""));
    }

    [Fact]
    public void Download_resume_discards_a_partial_when_the_object_identity_changes()
    {
        var recorded = new RemoteIdentity("\"abc\"", 10, DateTimeOffset.UnixEpoch);
        var same = new RemoteIdentity("abc", 10, DateTimeOffset.UnixEpoch);
        var changed = new RemoteIdentity("\"zzz\"", 10, DateTimeOffset.UnixEpoch);

        Assert.Equal(ResumeAction.ContinueRange, ResumeRules.ForDownload(same, recorded, 4));
        Assert.Equal(ResumeAction.CommitPartial, ResumeRules.ForDownload(same, recorded, 10));
        Assert.Equal(ResumeAction.Restart, ResumeRules.ForDownload(changed, recorded, 4));
        Assert.Equal(ResumeAction.Restart, ResumeRules.ForDownload(same, null, 4));
        Assert.False(new RemoteIdentity(null, 10, null).SameContent(new RemoteIdentity(null, 10, null)));
        var timestampOnly = new RemoteIdentity(null, 10, DateTimeOffset.UnixEpoch);
        Assert.True(timestampOnly.SameContent(timestampOnly));
        Assert.Equal(ResumeAction.Restart, ResumeRules.ForDownload(timestampOnly, timestampOnly, 4));
        Assert.Equal(ResumeAction.Missing, ResumeRules.ForDownload(null, recorded, 4));
    }

    [Theory]
    [InlineData("bytes 6-10/11", 5, true)]
    [InlineData("bytes 5-10/11", 6, false)]
    [InlineData("bytes 6-9/11", 4, false)]
    [InlineData("bytes 6-10/12", 5, false)]
    [InlineData(null, 5, false)]
    public void Resumed_download_accepts_only_the_requested_tail(string? contentRange, long responseLength, bool matches)
    {
        Assert.Equal(matches, ResumeRules.MatchesRange(contentRange, 6, 11, responseLength));
    }

    [Fact]
    public void Rename_keeps_the_extension_and_does_not_reuse_a_taken_name()
    {
        Assert.Equal("assets/photo (1).png", ConflictNames.WithSuffix("assets/photo.png", 1));
        Assert.Equal("assets/folder (2)/", ConflictNames.WithSuffix("assets/folder/", 2));

        var taken = new HashSet<string>(StringComparer.Ordinal) { "assets/photo.png", "assets/photo (1).png" };
        Assert.Equal("assets/photo (2).png", ConflictNames.NextFree("assets/photo.png", taken, 10));
    }

    [Fact]
    public void Local_relative_keys_reject_escapes_and_device_names()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(TransferPaths.TryCombine(@"C:\out", "images/logo.png", used, out var path, out _));
        Assert.Equal(Path.Combine(@"C:\out", "images", "logo.png"), path);

        Assert.False(TransferPaths.TryCombine(@"C:\out", "../secret.txt", used, out _, out var error));
        Assert.Equal("Escape", error);

        Assert.True(TransferPaths.TryCombine(@"C:\out", "CON.txt", used, out var reserved, out _));
        Assert.Equal("_CON.txt", Path.GetFileName(reserved));
    }

    [Fact]
    public void Storage_failures_map_throttling_and_server_errors_as_retryable()
    {
        var slow = TransferFailures.FromStorage(new StorageException(new StorageFailure(
            StorageFailureKind.Unknown, "SlowDown", 429, null, "slow")));
        var missing = TransferFailures.FromStorage(new StorageException(new StorageFailure(
            StorageFailureKind.BucketNotFound, "NoSuchKey", 404, null, "missing")));
        var denied = TransferFailures.FromStorage(new StorageException(new StorageFailure(
            StorageFailureKind.AccessDenied, "AccessDenied", 403, null, "no")));

        Assert.Equal(TransferFailureKind.Throttled, slow.Kind);
        Assert.True(slow.Retryable);
        Assert.Equal(TransferFailureKind.NotFound, missing.Kind);
        Assert.False(missing.Retryable);
        Assert.False(denied.Retryable);
    }
}
