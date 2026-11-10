using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Preview;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Preview;

/// <summary>Loads one gallery image at a time; visual zoom and pan belong to the Window.</summary>
public sealed class ImagePreviewViewModel : ViewModelBase, IDisposable
{
    private const int MaximumDecodeSide = 6144;
    private readonly PreviewService _preview;
    private readonly StorageConnection _connection;
    private readonly string _bucket;
    private readonly ImagePreviewGallery _gallery;
    private readonly PreviewSession _session = new();
    private bool _disposed;
    private bool _loading;
    private string _error = "";
    private BitmapSource? _image;

    public ImagePreviewViewModel(
        PreviewService preview,
        StorageConnection connection,
        string bucket,
        ImagePreviewGallery gallery,
        LocalizationService texts,
        BitmapSource? initialImage = null)
    {
        _preview = preview;
        _connection = connection;
        _bucket = bucket;
        _gallery = gallery;
        Texts = texts;
        _image = initialImage;
        PreviousCommand = new RelayCommand(_ => Move(-1));
        NextCommand = new RelayCommand(_ => Move(1));
        RetryCommand = new RelayCommand(_ => _ = LoadAsync());
    }

    public LocalizationService Texts { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand RetryCommand { get; }
    public BitmapSource? Image => _image;
    public bool IsLoading => _loading;
    public bool ShowError => !_loading && _error.Length > 0;
    public string Error => _error;
    public string Title => _gallery.Current.Name;
    public string PositionLabel => $"{_gallery.Position} / {_gallery.Count}";
    public string PixelSize => _image is null ? "" : $"{_image.PixelWidth} × {_image.PixelHeight}";
    public bool CanPrevious => _gallery.CanPrevious;
    public bool CanNext => _gallery.CanNext;

    public void Start()
    {
        if (!_disposed && _image is null)
            _ = LoadAsync();
    }

    public void Move(int direction)
    {
        if (_disposed || !_gallery.Move(direction))
            return;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_disposed)
            return;

        var current = _gallery.Current;
        var operation = _session.Begin(new PreviewObjectId(_connection.Id, _bucket, current.Key));
        _image = null;
        _error = "";
        _loading = true;
        PublishState();

        try
        {
            var loaded = await _preview.OpenImageAsync(
                _connection, _bucket, current.Key, current.Name, operation.Token);
            if (!_session.IsCurrent(operation))
                return;

            if (loaded.Kind == PreviewLoadKind.Image && loaded.Image is { Length: > 0 } bytes)
            {
                var decoded = await Task.Run(() => Decode(bytes), operation.Token);
                if (!_session.IsCurrent(operation))
                    return;
                if (decoded is not null)
                    _image = decoded;
                else
                    _error = Texts["Preview_ImageFailed"];
            }
            else if (loaded.Kind == PreviewLoadKind.TooLarge)
                _error = string.Format(Texts["Preview_TooLarge"],
                    Explorer.ExplorerText.FormatBytes(loaded.Head?.SizeBytes ?? 0));
            else
                _error = Texts["Preview_ImageFailed"];
        }
        catch (OperationCanceledException)
        {
            // The next selection or closing the window owns the visible state.
        }
        catch (Exception)
        {
            if (_session.IsCurrent(operation))
                _error = Texts["Preview_OpenFailed"];
        }
        finally
        {
            if (_session.Complete(operation))
            {
                _loading = false;
                PublishState();
            }
        }
    }

    private void PublishState()
    {
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(PixelSize));
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                return null;

            var frame = decoder.Frames[0];
            var maxSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
            var width = maxSide > MaximumDecodeSide
                ? Math.Max(1, (int)Math.Round((double)frame.PixelWidth * MaximumDecodeSide / maxSide))
                : 0;

            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (width > 0)
                bitmap.DecodePixelWidth = width;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or InvalidOperationException or FileFormatException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Dispose();
        _image = null;
    }
}
