namespace Cavdeck.Application.Preview;

/// <summary>
/// Owns a temporary preview file. Dispose deletes it; Detach transfers cleanup responsibility
/// to an external consumer and leaves stale-file pruning as the fallback.
/// </summary>
public sealed class PreviewFileLease : IAsyncDisposable
{
    private Action<string>? _release;
    private readonly Action<string> _detach;

    public PreviewFileLease(string filePath, long length, Action<string> release, Action<string> detach)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(detach);

        FilePath = filePath;
        Length = length;
        _release = release;
        _detach = detach;
    }

    public string FilePath { get; }
    public long Length { get; }

    /// <summary>Transfers the file to an external application without deleting it on disposal.</summary>
    public string Detach()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null)
            throw new InvalidOperationException("The preview file lease is already released or detached.");

        _detach(FilePath);
        return FilePath;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke(FilePath);
        return ValueTask.CompletedTask;
    }
}
