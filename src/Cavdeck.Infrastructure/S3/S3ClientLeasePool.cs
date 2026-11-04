using Amazon.S3;

namespace Cavdeck.Infrastructure.S3;

/// <summary>
/// Shares S3 clients across browsing and transfers for each saved connection.
/// A retired client remains alive until its last in-flight lease is released.
/// </summary>
public sealed class S3ClientLeasePool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Func<S3Session, AmazonS3Client> _createClient;
    private bool _disposed;

    public S3ClientLeasePool() : this(AwsS3Gateway.CreateClient) { }

    internal S3ClientLeasePool(Func<S3Session, AmazonS3Client> createClient) =>
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));

    internal S3ClientLease Acquire(S3Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Unsaved/probe sessions have no stable connection identity and are not cached.
            if (session.ConnectionId == Guid.Empty)
            {
                var temporary = _createClient(session);
                return new S3ClientLease(temporary, temporary.Dispose);
            }

            if (!_entries.TryGetValue(session.ConnectionId, out var entry)
                || !SameConfiguration(entry.Session, session))
            {
                // Construct first: a failed client creation must never leave a retired
                // or disposed client in the active cache.
                var replacement = new Entry(session, _createClient(session));
                if (entry is not null)
                    Retire(entry);
                _entries[session.ConnectionId] = replacement;
                entry = replacement;
            }

            entry.Leases++;
            return new S3ClientLease(entry.Client, () => Release(entry));
        }
    }

    /// <summary>
    /// Stops reusing a connection after an edit or deletion without disrupting active calls.
    /// </summary>
    public void Invalidate(Guid connectionId)
    {
        lock (_gate)
        {
            if (_entries.Remove(connectionId, out var entry))
                Retire(entry);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _entries.Values)
                Retire(entry);
            _entries.Clear();
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Leases--;
            if (entry.Retired && entry.Leases == 0)
                DisposeClient(entry);
        }
    }

    private static void Retire(Entry entry)
    {
        entry.Retired = true;
        if (entry.Leases == 0)
            DisposeClient(entry);
    }

    private static void DisposeClient(Entry entry)
    {
        if (entry.Disposed)
            return;
        entry.Disposed = true;
        entry.Client.Dispose();
    }

    private static bool SameConfiguration(S3Session left, S3Session right) =>
        left.ConnectionId == right.ConnectionId
        && left.AccessKeyId == right.AccessKeyId
        && left.SecretAccessKey == right.SecretAccessKey
        && left.Region == right.Region
        && left.ServiceUrl == right.ServiceUrl
        && left.ForcePathStyle == right.ForcePathStyle;

    private sealed class Entry(S3Session session, AmazonS3Client client)
    {
        public S3Session Session { get; } = session;
        public AmazonS3Client Client { get; } = client;
        public int Leases { get; set; }
        public bool Retired { get; set; }
        public bool Disposed { get; set; }
    }
}

internal sealed class S3ClientLease(AmazonS3Client client, Action release) : IDisposable
{
    private Action? _release = release;

    public AmazonS3Client Client { get; } = client;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
