using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Preview;

/// <summary>
/// Streams complete remote files into bounded temporary copies for viewers or external apps.
/// A future seekable media stream should use a separate ranged-read implementation.
/// </summary>
public sealed class PreviewFileDownloader
{
    private readonly IObjectStorageProviderFactory _providers;
    private readonly string _directory;

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
            PruneOldCopies();
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
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                    Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return;
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Active PDF renderers and external apps can keep file handles open.
            // Prune aged copies on the next preview operation.
        }
    }

    private void PruneOldCopies()
    {
        var cutoff = DateTime.UtcNow.AddDays(-2);
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

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
