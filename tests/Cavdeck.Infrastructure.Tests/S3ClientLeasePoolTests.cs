using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class S3ClientLeasePoolTests
{
    [Fact]
    public void Browsing_requests_reuse_client_for_the_same_connection()
    {
        using var pool = new S3ClientLeasePool();
        var session = Session(Guid.NewGuid());

        using var first = pool.Acquire(session);
        using var second = pool.Acquire(Session(session.ConnectionId));

        Assert.Same(first.Client, second.Client);
    }

    [Fact]
    public void Different_connections_have_independent_clients()
    {
        using var pool = new S3ClientLeasePool();
        using var first = pool.Acquire(Session(Guid.NewGuid()));
        using var second = pool.Acquire(Session(Guid.NewGuid()));

        Assert.NotSame(first.Client, second.Client);
    }

    [Fact]
    public void Credentials_and_endpoint_changes_replace_clients_without_releasing_active_leases()
    {
        using var pool = new S3ClientLeasePool();
        var id = Guid.NewGuid();
        var original = pool.Acquire(Session(id));
        var originalClient = original.Client;

        using var rotated = pool.Acquire(Session(id, secret: "rotated"));
        Assert.NotSame(originalClient, rotated.Client);
        Assert.Same(originalClient, original.Client);

        using var relocated = pool.Acquire(Session(id, secret: "rotated", endpoint: "https://second.example.test"));
        Assert.NotSame(rotated.Client, relocated.Client);
        Assert.Same(originalClient, original.Client);

        original.Dispose();
        original.Dispose(); // Leases must be safe to release more than once.
    }

    [Fact]
    public void Region_and_path_style_changes_replace_cached_clients()
    {
        using var pool = new S3ClientLeasePool();
        var id = Guid.NewGuid();

        using var original = pool.Acquire(Session(id));
        using var region = pool.Acquire(Session(id, region: "eu-west-1"));
        using var pathStyle = pool.Acquire(Session(id, region: "eu-west-1", pathStyle: false));

        Assert.NotSame(original.Client, region.Client);
        Assert.NotSame(region.Client, pathStyle.Client);
    }

    [Fact]
    public void Invalidating_a_connection_retains_old_lease_and_reuses_new_client()
    {
        using var pool = new S3ClientLeasePool();
        var session = Session(Guid.NewGuid());
        using var oldLease = pool.Acquire(session);

        pool.Invalidate(session.ConnectionId);
        pool.Invalidate(session.ConnectionId);
        using var replacement = pool.Acquire(session);
        using var nextCall = pool.Acquire(session);

        Assert.NotSame(oldLease.Client, replacement.Client);
        Assert.Same(replacement.Client, nextCall.Client);
    }

    [Fact]
    public void Unidentified_sessions_are_not_cached()
    {
        using var pool = new S3ClientLeasePool();
        using var first = pool.Acquire(Session(Guid.Empty));
        using var second = pool.Acquire(Session(Guid.Empty));

        Assert.NotSame(first.Client, second.Client);
    }

    [Fact]
    public void Shutdown_rejects_new_requests_but_existing_leases_can_be_released()
    {
        var pool = new S3ClientLeasePool();
        var session = Session(Guid.NewGuid());
        var active = pool.Acquire(session);
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Acquire(session));
        active.Dispose();
        active.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void Failed_replacement_does_not_poison_the_existing_cache()
    {
        using var pool = new S3ClientLeasePool(session =>
            session.SecretAccessKey == "invalid" ? throw new InvalidOperationException("test failure")
                : AwsS3Gateway.CreateClient(session));
        var id = Guid.NewGuid();
        var oldSession = Session(id);
        using var existing = pool.Acquire(oldSession);

        Assert.Throws<InvalidOperationException>(() => pool.Acquire(Session(id, secret: "invalid")));
        using var later = pool.Acquire(oldSession);
        Assert.Same(existing.Client, later.Client);
    }

    [Fact]
    public async Task Parallel_requests_for_one_connection_receive_one_client()
    {
        using var pool = new S3ClientLeasePool();
        var session = Session(Guid.NewGuid());

        var clients = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                using var lease = pool.Acquire(session);
                return lease.Client;
            })));

        Assert.All(clients, client => Assert.Same(clients[0], client));
    }

    private static S3Session Session(Guid connectionId, string secret = "test-secret",
        string endpoint = "https://s3.example.test", string region = "us-east-1",
        bool pathStyle = true) => new()
    {
        ConnectionId = connectionId,
        AccessKeyId = "test-access-key",
        SecretAccessKey = secret,
        ServiceUrl = endpoint,
        Region = region,
        ForcePathStyle = pathStyle
    };
}
