using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Preview;

public enum PreviewLoadKind
{
    Text,
    Image,
    Pdf,
    TooLarge,
    External,
    NotText,
    Failed
}

public enum PreviewSaveKind
{
    Saved,
    Conflict,
    RequiresOverwrite,
    Failed
}

public sealed record PreviewLoad(
    PreviewLoadKind Kind, ObjectHead? Head, string? Text, TextCharset Charset,
    bool Bom, byte[]? Image, StorageFailure? Failure);

public sealed record PreviewSave(PreviewSaveKind Kind, ObjectHead? Head, StorageFailure? Failure);

public sealed class PreviewService
{
    private readonly IPreviewContentReader _contentReader;
    private readonly IObjectStorageProviderFactory _providers;
    private readonly IConnectionCatalog? _catalog;

    public PreviewService(
        IPreviewContentReader contentReader,
        IObjectStorageProviderFactory providers,
        IConnectionCatalog? catalog = null)
    {
        _contentReader = contentReader ?? throw new ArgumentNullException(nameof(contentReader));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _catalog = catalog;
    }

    // Preserve the existing construction contract for tests and lightweight callers.
    // Application DI resolves the registered preview reader.
    public PreviewService(IObjectStorageProviderFactory providers, IConnectionCatalog? catalog = null)
        : this(new ProviderPreviewContentReader(providers), providers, catalog)
    {
    }

    public Task<PreviewFileLease> PrepareFileAsync(
        StorageConnection connection, string bucket, string key, string name,
        long expectedBytes, long maximumBytes, IProgress<long>? progress, CancellationToken cancellationToken) =>
        _contentReader.PrepareFileAsync(
            connection, bucket, key, name, expectedBytes, maximumBytes, progress, cancellationToken);

    public Task<PreviewLoad> OpenAsync(
        StorageConnection connection, string bucket, string key, string name, CancellationToken cancellationToken) =>
        OpenCoreAsync(connection, bucket, key, name, immersiveImage: false, cancellationToken);

    /// <summary>
    /// Explicit user-initiated full-window image loading. Keeps the side-pane cap intact,
    /// enforces a separate upper bound and does not introduce a persistent transfer task.
    /// </summary>
    public Task<PreviewLoad> OpenImageAsync(
        StorageConnection connection, string bucket, string key, string name, CancellationToken cancellationToken) =>
        OpenCoreAsync(connection, bucket, key, name, immersiveImage: true, cancellationToken);

    private async Task<PreviewLoad> OpenCoreAsync(
        StorageConnection connection, string bucket, string key, string name,
        bool immersiveImage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            var head = await _contentReader.HeadAsync(connection, bucket, key, cancellationToken);
            var decision = PreviewRules.Decide(name, false, head.SizeBytes, head.ContentType);
            var maxImageBytes = immersiveImage
                ? PreviewRules.MaxImmersiveImageBytes
                : PreviewRules.MaxImageBytes;

            if (immersiveImage)
            {
                // No other file type becomes executable or viewable merely because
                // the caller selected the image viewer.
                if (PreviewRules.Classify(name, head.ContentType) != PreviewFileType.Image)
                    return Closed(PreviewKind.External, head);
                if (head.SizeBytes < 0 || head.SizeBytes > maxImageBytes)
                    return Closed(PreviewKind.TooLarge, head);
                decision = new PreviewDecision(PreviewKind.Image);
            }

            if (decision.Kind is PreviewKind.TooLarge or PreviewKind.External or PreviewKind.Folder)
                return Closed(decision.Kind, head);

            // PDFs use a bounded temporary file so the UI can report download progress.
            if (decision.Kind == PreviewKind.Pdf)
                return new PreviewLoad(PreviewLoadKind.Pdf, head, null, TextCharset.Utf8, false, null, null);

            var max = decision.Kind == PreviewKind.Image
                ? (int)maxImageBytes
                : (int)PreviewRules.MaxTextBytes;
            var body = await _contentReader.ReadAsync(connection, bucket, key, max, cancellationToken);
            if (body.Truncated || body.Content.Length > max)
                return Closed(PreviewKind.TooLarge, body.Head);
            if (decision.Kind == PreviewKind.Image)
                return new PreviewLoad(PreviewLoadKind.Image, body.Head, null, TextCharset.Utf8, false, body.Content, null);
            if (!TextCodec.TryDecode(body.Content, out var decoded))
                return new PreviewLoad(PreviewLoadKind.NotText, body.Head, null, TextCharset.Utf8, false, null, null);
            return new PreviewLoad(
                PreviewLoadKind.Text, body.Head, decoded.Text, decoded.Charset, decoded.Bom, null, null);
        }
        catch (StorageException exception)
        {
            return new PreviewLoad(PreviewLoadKind.Failed, null, null, TextCharset.Utf8, false, null, exception.Failure);
        }
    }

    public async Task<PreviewSave> SaveTextAsync(
        StorageConnection connection, string bucket, string key,
        string text, TextCharset charset, bool bom, string? contentType, string? ifMatchETag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            connection = await Cavdeck.Application.Connections.ConnectionAccess.ResolveAsync(_catalog, connection, cancellationToken);
            ObjectAccess.Ensure(connection, bucket, key, write: true);
            var bytes = TextCodec.Encode(text, charset, bom);
            var provider = _providers.Create(connection.Provider);
            await using var stream = new MemoryStream(bytes, writable: false);
            var head = await provider.ReplaceObjectAsync(
                connection, bucket, key, stream, contentType, ifMatchETag, cancellationToken);
            return new PreviewSave(PreviewSaveKind.Saved, head, null);
        }
        catch (StorageException exception) when (exception.Failure.Kind == StorageFailureKind.PreconditionFailed)
        {
            return new PreviewSave(PreviewSaveKind.Conflict, null, exception.Failure);
        }
        catch (StorageException exception) when (exception.Failure.ErrorCode == "ConditionalOverwriteUnsupported"
            && !string.IsNullOrWhiteSpace(ifMatchETag))
        {
            return new PreviewSave(PreviewSaveKind.RequiresOverwrite, null, exception.Failure);
        }
        catch (StorageException exception)
        {
            return new PreviewSave(PreviewSaveKind.Failed, null, exception.Failure);
        }
    }

    private static PreviewLoad Closed(PreviewKind kind, ObjectHead head) =>
        new(kind == PreviewKind.TooLarge ? PreviewLoadKind.TooLarge : PreviewLoadKind.External,
            head, null, TextCharset.Utf8, false, null, null);
}
