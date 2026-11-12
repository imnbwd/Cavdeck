using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Preview;

/// <summary>
/// Streams complete remote files into bounded temporary copies for viewers or external apps.
/// A future seekable media stream should use a separate ranged-read implementation.
/// </summary>
public sealed class PreviewFileDownloader
{
    private static readonly TimeSpan StaleFileRetention = TimeSpan.FromHours(48);
    private readonly IObjectStorageProviderFactory _providers;
    private readonly string _directory;
    private readonly object _activeGate = new();
    private readonly HashSet<string> _activeCopies = new(StringComparer.OrdinalIgnoreCase);

    public PreviewFileDownloader(IObjectStorageProviderFactory providers, string? directory = null)
    {
        _providers = providers;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cavdeck", "preview");
    }

    public async Task<string> PrepareAsync(
        StorageConnection connection, string bucket, string key, string name,
        long expectedBytes, long maximumBytes, IProgress<long>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (expectedBytes < 0 || expectedBytes > maximumBytes)
            throw new IOException("Preview file exceeds the permitted size.");
        token.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            Directory.CreateDirectory(_directory);
            CleanupStaleFiles();
        }, token);

        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + "-" + SafeName(name));
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                             FileShare.Read, 81_920, FileOptions.Asynchronous))
            {
                var bounded = new BoundedStream(stream, maximumBytes);
                await _providers.Create(connection.Provider).DownloadAsync(
                    connection, bucket, key, bounded, progress, token);
                token.ThrowIfCancellationRequested();
                if (bounded.WrittenBytes != expectedBytes)
                    throw new IOException("Downloaded file size did not match the remote metadata.");
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            lock (_activeGate)
                _activeCopies.Add(Path.GetFullPath(path));
            return path;
        }
        catch
        {
            Release(path);
            throw;
        }
    }

    public void Release(string? path)
    {
        if (!TryGetManagedPath(path, out var fullPath))
            return;

        lock (_activeGate)
            _activeCopies.Remove(fullPath);

        try
        {
            File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Active PDF renderers can keep file handles open. A later prune retries.
        }
    }

    public void Detach(string? path)
    {
        if (!TryGetManagedPath(path, out var fullPath))
            return;

        lock (_activeGate)
            _activeCopies.Remove(fullPath);
    }

    private bool TryGetManagedPath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            var expectedDirectory = Path.GetFullPath(_directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(directory, expectedDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort cleanup of expired preview copies. Only files created by this downloader
    /// are eligible; active leases and files that cannot be accessed are left untouched.
    /// </summary>
    public void CleanupStaleFiles()
    {
        if (!Directory.Exists(_directory))
            return;

        HashSet<string> active;
        lock (_activeGate)
            active = new HashSet<string>(_activeCopies, StringComparer.OrdinalIgnoreCase);

        var cutoff = DateTime.UtcNow - StaleFileRetention;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (active.Contains(fullPath) || !IsManagedCopy(fullPath))
                        continue;

                    if (File.GetLastWriteTimeUtc(fullPath) < cutoff)
                        File.Delete(fullPath);
                }
                catch (Exception ex) when (IsExpectedFileSystemException(ex)) { }
            }
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            // Cache cleanup is best-effort and must never interrupt preview or startup.
        }
    }

    private static bool IsManagedCopy(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 33
            && name[32] == '-'
            && Guid.TryParseExact(name.AsSpan(0, 32), "N", out _);
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;

    private static string SafeName(string name)
    {
        var safe = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            return "object";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        return safe.Length > 120 ? safe[^120..] : safe;
    }

    private sealed class BoundedStream(Stream destination, long maximumBytes) : Stream
    {
        public long WrittenBytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => WrittenBytes;
        public override long Position { get => WrittenBytes; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken ct) => destination.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard(count);
            destination.Write(buffer, offset, count);
            WrittenBytes += count;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Guard(buffer.Length);
            await destination.WriteAsync(buffer, ct);
            WrittenBytes += buffer.Length;
        }
        private void Guard(int count)
        {
            if (count > maximumBytes - WrittenBytes)
                throw new IOException("Preview file exceeds the permitted size.");
        }
    }
}
