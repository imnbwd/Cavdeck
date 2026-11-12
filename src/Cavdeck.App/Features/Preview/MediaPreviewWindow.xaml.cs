using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Preview;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Preview;

/// <summary>
/// A bounded, user-initiated local media player. We do not hand an S3 URL or secret
/// to the Windows media stack. The temporary lease lives exactly as long as the window.
/// Native codec support varies by installed Windows components.
/// </summary>
public partial class MediaPreviewWindow : AppWindow
{
    private readonly PreviewService _preview;
    private readonly StorageConnection _connection;
    private readonly string _bucket;
    private readonly string _key;
    private readonly long _size;
    private readonly bool _isVideo;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _openTimeoutTimer = new() { Interval = MediaPlaybackMath.OpenTimeout };

    private PreviewFileLease? _lease;
    private MediaElement? _player;
    private int _playbackAttempt;
    private int _automaticRetryCount;
    private bool _downloadInProgress;
    private bool _openAttemptActive;
    private bool _disposed;
    private string _feedbackKey = "Media_Loading";
    private string _feedbackDescriptionKey = "";
    private string _feedbackPrimaryKey = "Media_Cancel";
    private bool _feedbackShowSecondary;
    private long _downloadedBytes;
    private TimeSpan _duration;

    private MediaElement Player => _player ?? throw new InvalidOperationException("The media player is not initialized.");

    public MediaPreviewWindow(PreviewService preview, StorageConnection connection, string bucket,
        string key, string name, long size, PreviewFileType type, LocalizationService texts)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(texts);
        if (type is not (PreviewFileType.Audio or PreviewFileType.Video)
            || size < 0 || size > PreviewRules.MaxMediaBytes)
            throw new ArgumentException("Only bounded media files can be opened.", nameof(type));

        _preview = preview;
        _connection = connection;
        _bucket = bucket;
        _key = key;
        _size = size;
        FileName = name;
        Texts = texts;
        _isVideo = type == PreviewFileType.Video;

        InitializeComponent();
        DataContext = this;
        UseTexts(texts);
        AudioArtwork.Visibility = type == PreviewFileType.Audio
            ? Visibility.Visible : Visibility.Collapsed;
        if (type == PreviewFileType.Audio)
        {
            Width = 640;
            Height = 405;
            MinWidth = 440;
            MinHeight = 360;
            // File identity lives in the audio panel, not in two stacked headers.
            MediaCaption.Text = $"{Texts["Media_KindAudio"]} — Cavdeck";
            // Audio artwork remains visible while the media surface stays unobtrusive.
        }

        FullscreenButton.Visibility = _isVideo ? Visibility.Visible : Visibility.Collapsed;
        _surfaceClickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _surfaceClickTimer.Tick += SurfaceClickTimer_Tick;
        // Activation only controls fullscreen z-order. Losing focus is not a close
        // request: keep the playback session and its temporary file lease alive.
        Activated += (_, _) =>
        {
            if (_fullScreen)
                Topmost = true;
        };
        Deactivated += (_, _) =>
        {
            if (_fullScreen)
                Topmost = false;
        };
        Feedback.PrimaryCommand = new RelayCommand(_ => Close());
        Feedback.SecondaryCommand = new RelayCommand(_ => Close());
        Feedback.ShowPrimaryButton = true;
        Feedback.ShowSecondaryButton = false;
        Feedback.IsIndeterminate = true;
        Feedback.ShowProgress = true;

        UpdatePlaybackLabels();
        _timer.Tick += Timer_Tick;
        _openTimeoutTimer.Tick += OpenTimeoutTimer_Tick;
        SeekSlider.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) =>
            {
                if (!_ready)
                    return;
                _dragging = true;
                UpdateSeekPreview();
                SeekPreviewTip.IsOpen = true;
                ShowFullscreenControls();
            }), true);
        SeekSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                SeekPreviewTip.IsOpen = false;
                if (!_dragging)
                    return;
                var target = SeekSlider.Value;
                _dragging = false;
                ApplySeek(target);
            }), true);
        Texts.PropertyChanged += Texts_PropertyChanged;
        Loaded += (_, _) => _ = LoadAsync();
        Closed += (_, _) => DisposePlayback();
    }

    public string FileName { get; }
    public LocalizationService Texts { get; }
    public string MediaSummary =>
        $"{Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant()} · {ExplorerText.FormatBytes(_size)}";

    private async Task LoadAsync()
    {
        if (_disposed || _downloadInProgress || _lease is not null)
            return;

        _downloadInProgress = true;
        var token = _lifetime.Token;
        Feedback.Visibility = Visibility.Visible;
        SetFeedback("Media_Loading", "Media_Cancel");
        Feedback.PrimaryCommand = new RelayCommand(_ => Close());
        Feedback.ShowPrimaryButton = true;
        Feedback.ShowProgress = true;
        Feedback.IsIndeterminate = false;
        Feedback.ProgressValue = 0;
        _downloadedBytes = 0;
        Feedback.ProgressDescription = "";
        PreviewFileLease? pending = null;
        try
        {
            var progress = new Progress<long>(read =>
            {
                if (_disposed || token.IsCancellationRequested)
                    return;
                _downloadedBytes = read;
                Feedback.ProgressValue = _size > 0 ? Math.Clamp(read * 100.0 / _size, 0, 100) : 0;
                Feedback.ProgressDescription = string.Format(Texts["Media_Progress"],
                    ExplorerText.FormatBytes(read), ExplorerText.FormatBytes(_size));
            });

            pending = await _preview.PrepareFileAsync(
                _connection, _bucket, _key, FileName, _size,
                PreviewRules.MaxMediaBytes, progress, token);
            token.ThrowIfCancellationRequested();
            if (_disposed)
                return;

            _lease = pending;
            pending = null;
            DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaDownloadCompleted");
            StartMediaOpen();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closing cancels the download; the downloader removes partial files.
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaDownloadFailed", exception);
                ShowFailure("Preview_FeedbackErrorTitle", "Media_DownloadFailed");
            }
        }
        finally
        {
            try
            {
                if (pending is not null)
                    await pending.DisposeAsync();
            }
            finally
            {
                _downloadInProgress = false;
            }
        }
    }

    private MediaElement CreateMediaPlayer(int attempt)
    {
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Opacity = _isVideo ? 1 : 0,
            Volume = VolumeSlider.Value,
            IsMuted = _muted
        };
        player.MediaOpened += (sender, args) => Player_MediaOpened(sender!, args, attempt);
        player.MediaEnded += (sender, args) => Player_MediaEnded(sender!, args, attempt);
        player.MediaFailed += (sender, args) => Player_MediaFailed(sender!, args, attempt);
        MediaHost.Children.Add(player);
        _player = player;
        return player;
    }

    private void ReleasePlayer()
    {
        var previous = _player;
        _player = null;
        if (previous is null)
            return;

        MediaHost.Children.Remove(previous);
        try { previous.Stop(); }
        catch (Exception exception) { DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaReleaseFailed", exception); }
        try { previous.Close(); }
        catch (Exception exception) { DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaReleaseFailed", exception); }
        try { previous.Source = null; }
        catch (Exception exception) { DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaReleaseFailed", exception); }
    }

    private bool IsCurrentPlayer(object sender, int attempt) =>
        !_disposed && attempt == _playbackAttempt && ReferenceEquals(sender, _player);

    private void StartMediaOpen()
    {
        if (_disposed || _lease is null)
            return;

        _openTimeoutTimer.Stop();
        _openAttemptActive = false;
        _timer.Stop();
        ResetPlaybackControls();

        // A retry gets a fresh MediaElement. Late native callbacks from the previous
        // player are ignored by the attempt identity check.
        _playbackAttempt++;
        ReleasePlayer();
        var player = CreateMediaPlayer(_playbackAttempt);

        Feedback.Visibility = Visibility.Visible;
        SetFeedback("Media_Opening", "Media_Cancel");
        Feedback.PrimaryCommand = new RelayCommand(_ => Close());
        Feedback.ShowPrimaryButton = true;
        Feedback.ShowProgress = true;
        Feedback.IsIndeterminate = true;
        Feedback.ProgressValue = 0;
        Feedback.ProgressDescription = "";

        try
        {
            _openAttemptActive = true;
            _openTimeoutTimer.Start();
            player.Source = new Uri(Path.GetFullPath(_lease.FilePath));
            DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaSourceAssigned");
            player.Play();
            DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaPlayRequested");
        }
        catch (Exception exception)
        {
            _openTimeoutTimer.Stop();
            _openAttemptActive = false;
            DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaOpenStartFailed", exception);
            ShowFailure("Preview_FeedbackErrorTitle", "Media_Unsupported");
        }

    }

    private void ResetPlaybackControls()
    {
        _ready = false;
        _playing = false;
        _ended = false;
        _duration = TimeSpan.Zero;
        _settingSeek = true;
        SeekSlider.Value = 0;
        SeekSlider.Maximum = 1;
        _settingSeek = false;
        SeekSlider.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        UpdatePlaybackLabels();
        UpdateTimeLabel();
    }

    private void OpenTimeoutTimer_Tick(object? sender, EventArgs e)
    {
        _openTimeoutTimer.Stop();
        if (_disposed || _ready || !_openAttemptActive)
            return;

        _openAttemptActive = false;
        DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaOpenTimeout");
        if (MediaPlaybackMath.ShouldRetryAfterOpenTimeout(_automaticRetryCount))
        {
            _automaticRetryCount++;
            DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaAutoRetry");
            StartMediaOpen();
            return;
        }

        DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaRetryExhausted");
        ShowFailure("Media_OpenTimedOut", "Media_OpenTimeoutDescription");
    }

    private void RetryPlayback()
    {
        if (_disposed)
            return;
        if (_lease is null)
        {
            _ = LoadAsync();
            return;
        }

        _automaticRetryCount = 0;
        DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaManualRetry");
        StartMediaOpen();
    }

    private void SetFeedback(string headlineKey, string primaryKey,
        string? descriptionKey = null, bool showSecondary = false)
    {
        _feedbackKey = headlineKey;
        _feedbackPrimaryKey = primaryKey;
        _feedbackDescriptionKey = descriptionKey ?? "";
        _feedbackShowSecondary = showSecondary;
        Feedback.Headline = Texts[headlineKey];
        Feedback.Description = descriptionKey is null ? "" : Texts[descriptionKey];
        Feedback.PrimaryButtonText = Texts[primaryKey];
        Feedback.ShowSecondaryButton = showSecondary;
        if (showSecondary)
            Feedback.SecondaryButtonText = Texts["Media_Close"];
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e, int attempt)
    {
        if (!IsCurrentPlayer(sender, attempt))
            return;

        _openTimeoutTimer.Stop();
        _openAttemptActive = false;
        DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaOpened");
        _ready = true;
        _ended = false;
        _duration = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        SeekSlider.IsEnabled = _duration > TimeSpan.Zero;
        SeekSlider.Maximum = Math.Max(1, _duration.TotalSeconds);
        PlayPauseButton.IsEnabled = true;
        BackButton.IsEnabled = _duration > TimeSpan.Zero;
        ForwardButton.IsEnabled = _duration > TimeSpan.Zero;
        Feedback.Visibility = Visibility.Collapsed;
        Feedback.ShowSecondaryButton = false;
        ApplyPlaybackSpeed();
        UpdateTimeLabel();
        Player.Play();
        _playing = true;
        _timer.Start();
        UpdatePlaybackLabels();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e, int attempt)
    {
        if (!IsCurrentPlayer(sender, attempt))
            return;
        Player.Pause();
        _playing = false;
        _ended = true;
        _timer.Stop();
        UpdateSeekValue(_duration.TotalSeconds);
        ShowFullscreenControls();
        UpdatePlaybackLabels();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e, int attempt)
    {
        if (!IsCurrentPlayer(sender, attempt))
            return;

        _openTimeoutTimer.Stop();
        _openAttemptActive = false;
        DiagnosticLog.Current.RecordMediaPlaybackEvent("MediaFailed", e.ErrorException);
        _timer.Stop();
        ShowFullscreenControls();
        ShowFailure("Preview_FeedbackErrorTitle", "Media_Unsupported");
    }

    private void ShowFailure(string headlineKey, string descriptionKey)
    {
        if (_disposed)
            return;

        _openTimeoutTimer.Stop();
        _openAttemptActive = false;
        _timer.Stop();

        // Invalidate callbacks before releasing the native player. A late MediaOpened
        // event must not dismiss the failure UI or start playback after a timeout.
        _playbackAttempt++;
        ReleasePlayer();
        ResetPlaybackControls();
        Feedback.Visibility = Visibility.Visible;
        SetFeedback(headlineKey, "Preview_Retry", descriptionKey, showSecondary: true);
        Feedback.ShowProgress = false;
        Feedback.IsIndeterminate = false;
        Feedback.ProgressDescription = "";
        Feedback.PrimaryCommand = new RelayCommand(_ => RetryPlayback());
        Feedback.SecondaryCommand = new RelayCommand(_ => Close());
        Feedback.ShowPrimaryButton = true;
    }

    private void DisposePlayback()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _openTimeoutTimer.Stop();
        _openTimeoutTimer.Tick -= OpenTimeoutTimer_Tick;
        _surfaceClickTimer.Stop();
        _surfaceClickTimer.Tick -= SurfaceClickTimer_Tick;
        SeekPreviewTip.IsOpen = false;
        Texts.PropertyChanged -= Texts_PropertyChanged;
        _playbackAttempt++;
        ReleasePlayer();
        try
        {
            if (_lease is not null)
            {
                _ = _lease.DisposeAsync();
                _lease = null;
            }
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
