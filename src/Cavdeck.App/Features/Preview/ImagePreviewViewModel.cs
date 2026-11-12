using System.ComponentModel;
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
    private readonly PreviewService _preview;
    private readonly StorageConnection _connection;
    private readonly string _bucket;
    private readonly ImagePreviewGallery _gallery;
    private readonly PreviewSession _session = new();
    private bool _disposed;
    private bool _loading;
    private string? _errorKey;
    private string? _errorArgument;
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
        Texts.PropertyChanged += Texts_PropertyChanged;
        _image = initialImage;
        PreviousCommand = new RelayCommand(_ => Move(-1));
        NextCommand = new RelayCommand(_ => Move(1));
        RetryCommand = new RelayCommand(parameter => { _ = LoadAsync(); });
    }

    public LocalizationService Texts { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand RetryCommand { get; }
    public BitmapSource? Image => _image;
    public bool HasImage => _image is not null;
    public bool IsLoading => _loading;
    public bool ShowError => !_loading && _errorKey is not null;
    public bool ShowFeedback => _loading || ShowError;
    public string FeedbackTitle => Texts[_loading ? "Preview_Loading" : "Preview_FeedbackErrorTitle"];
    public string Error => _errorKey is null ? "" : _errorArgument is null
        ? Texts[_errorKey] : string.Format(Texts[_errorKey], _errorArgument);
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
        _errorKey = null;
        _errorArgument = null;
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
                var decoded = await Task.Run(() => PreviewImageDecoder.Decode(bytes), operation.Token);
                if (!_session.IsCurrent(operation))
                    return;
                if (decoded is not null)
                    _image = decoded;
                else
                    SetError("Preview_ImageFailed");
            }
            else if (loaded.Kind == PreviewLoadKind.TooLarge)
                SetError("Preview_TooLarge",
                    Explorer.ExplorerText.FormatBytes(loaded.Head?.SizeBytes ?? 0));
            else
                SetError("Preview_ImageFailed");
        }
        catch (OperationCanceledException)
        {
            // The next selection or closing the window owns the visible state.
        }
        catch (Exception)
        {
            if (_session.IsCurrent(operation))
                SetError("Preview_OpenFailed");
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

    private void SetError(string key, string? argument = null)
    {
        _errorKey = key;
        _errorArgument = argument;
    }

    private void Texts_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;
        OnPropertyChanged(nameof(FeedbackTitle));
        OnPropertyChanged(nameof(Error));
    }

    private void PublishState()
    {
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowFeedback));
        OnPropertyChanged(nameof(FeedbackTitle));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(PixelSize));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Texts.PropertyChanged -= Texts_PropertyChanged;
        _session.Dispose();
        _image = null;
    }
}
