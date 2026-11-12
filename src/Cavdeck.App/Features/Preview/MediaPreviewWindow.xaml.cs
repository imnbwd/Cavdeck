using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.ViewModels;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Localization;
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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _openTimeoutTimer = new() { Interval = MediaPlaybackMath.OpenTimeout };
    private readonly DispatcherTimer _surfaceClickTimer = new();
    private PreviewFileLease? _lease;
    private MediaElement? _player;
    private int _playbackAttempt;
    private int _automaticRetryCount;
    private bool _downloadInProgress;
    private bool _openAttemptActive;
    private string _feedbackDescriptionKey = "";
    private string _feedbackPrimaryKey = "Media_Cancel";
    private bool _feedbackShowSecondary;
    private bool _disposed;
    private bool _ready;
    private bool _playing;
    private bool _dragging;
    private bool _settingSeek;
    private bool _muted;
    private bool _ended;
    private bool _fullScreen;
    private bool _speedUnsupported;
    private bool _settingSpeed;
    private readonly bool _isVideo;
    private Rect _previousBounds;
    private WindowState _previousWindowState;
    private ResizeMode _previousResizeMode;
    private bool _previousTopmost;
    private DateTime _lastPointerActivityUtc = DateTime.UtcNow;
    private Point? _lastPointerPosition;
    private TimeSpan _duration;
    private string _feedbackKey = "Media_Loading";
    private long _downloadedBytes;

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
            Height = 380;
            MinWidth = 440;
            MinHeight = 360;
            // File identity lives in the audio panel, not in two stacked headers.
            MediaCaption.Text = $"{Texts["Media_KindAudio"]} — Cavdeck";
            // Audio artwork remains visible while the media surface stays unobtrusive.
        }

        FullscreenButton.Visibility = _isVideo ? Visibility.Visible : Visibility.Collapsed;
        _surfaceClickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _surfaceClickTimer.Tick += SurfaceClickTimer_Tick;
        // Do not cover other applications while the full-screen viewer is inactive.
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
        player.MediaOpened += (sender, args) => Player_MediaOpened(sender, args, attempt);
        player.MediaEnded += (sender, args) => Player_MediaEnded(sender, args, attempt);
        player.MediaFailed += (sender, args) => Player_MediaFailed(sender, args, attempt);
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

        UpdatePlaybackLabels();
        UpdateTimeLabel();
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
        UpdatePlaybackLabels();
    }

    private void ShowFailure(string headlineKey, string descriptionKey)
    {
        if (_disposed)
            return;

        _openTimeoutTimer.Stop();
        _openAttemptActive = false;
        _timer.Stop();
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

    private void TogglePlayback()
    {
        if (!_ready)
            return;
        if (_playing)
        {
            Player.Pause();
            _playing = false;
            _timer.Stop();
            ShowFullscreenControls();
        }
        else
        {
            if (_ended)
            {
                Player.Position = TimeSpan.Zero;
                _ended = false;
                UpdateSeekValue(0);
            }
            Player.Play();
            _playing = true;
            _lastPointerActivityUtc = DateTime.UtcNow;
            _timer.Start();
        }
        UpdatePlaybackLabels();
    }

    private void UpdatePlaybackLabels()
    {
        var playLabel = Texts[_playing ? "Media_Pause" : _ended ? "Media_Replay" : "Media_Play"];
        PlayPauseGlyph.Data = (Geometry)FindResource(_playing ? "IconMediaPause" : "IconMediaPlay");
        PlayPauseButton.ToolTip = playLabel;
        System.Windows.Automation.AutomationProperties.SetName(PlayPauseButton, playLabel);
        var muteLabel = Texts[_muted ? "Media_Unmute" : "Media_Mute"];
        MuteGlyph.Data = (Geometry)FindResource(_muted ? "IconMediaMuted" : "IconMediaVolume");
        MuteButton.ToolTip = muteLabel;
        System.Windows.Automation.AutomationProperties.SetName(MuteButton, muteLabel);
        if (_isVideo)
        {
            var fullscreenLabel = Texts[_fullScreen ? "Media_ExitFullscreen" : "Media_EnterFullscreen"];
            FullscreenGlyph.Data = (Geometry)FindResource(_fullScreen
                ? "IconMediaCollapse" : "IconMediaExpand");
            FullscreenButton.ToolTip = fullscreenLabel;
            System.Windows.Automation.AutomationProperties.SetName(FullscreenButton, fullscreenLabel);
        }
        SpeedSelector.ToolTip = Texts[_speedUnsupported ? "Media_SpeedUnsupported" : "Media_Speed"];
        if (_dragging)
            UpdateSeekPreview();
    }

    private void Texts_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;
        if (!_isVideo)
            MediaCaption.Text = $"{Texts["Media_KindAudio"]} — Cavdeck";
        SetFeedback(_feedbackKey, _feedbackPrimaryKey,
            _feedbackDescriptionKey.Length == 0 ? null : _feedbackDescriptionKey,
            _feedbackShowSecondary);
        if (_feedbackShowSecondary)
            Feedback.SecondaryButtonText = Texts["Media_Close"];
        UpdatePlaybackLabels();
        if (Feedback.ShowProgress && !Feedback.IsIndeterminate && _size > 0)
            Feedback.ProgressDescription = string.Format(Texts["Media_Progress"],
                ExplorerText.FormatBytes(_downloadedBytes), ExplorerText.FormatBytes(_size));
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateFullscreenControls();
        if (!_ready || _dragging)
            return;
        UpdateSeekValue(Player.Position.TotalSeconds);
    }

    private void UpdateSeekValue(double seconds)
    {
        _settingSeek = true;
        SeekSlider.Value = Math.Clamp(seconds, SeekSlider.Minimum, SeekSlider.Maximum);
        _settingSeek = false;
        UpdateTimeLabel();
    }

    private void UpdateTimeLabel()
    {
        var current = _dragging ? TimeSpan.FromSeconds(SeekSlider.Value)
            : _ended ? _duration
            : _ready && _player is not null ? Player.Position : TimeSpan.Zero;
        TimeLabel.Text = MediaPlaybackMath.TimeLabel(current, _duration, _ready);
    }

    private void ApplySeek(double seconds)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
            return;
        Player.Position = MediaPlaybackMath.Seek(Player.Position, seconds, _duration);
        _ended = false;
        UpdatePlaybackLabels();
        UpdateTimeLabel();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingSeek || !_ready)
            return;
        if (_dragging)
        {
            UpdateTimeLabel();
            UpdateSeekPreview();
        }
        else
            ApplySeek(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is null)
            return;
        Player.Volume = e.NewValue;
        if (_muted && e.NewValue > 0)
        {
            _muted = false;
            Player.IsMuted = false;
            UpdatePlaybackLabels();
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        if (_player is not null)
            Player.IsMuted = _muted;
        UpdatePlaybackLabels();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Skip(-5);
    private void Forward_Click(object sender, RoutedEventArgs e) => Skip(5);

    private void Skip(double seconds)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
            return;
        Player.Position = MediaPlaybackMath.Skip(Player.Position, seconds, _duration);
        _ended = false;
        UpdateSeekValue(Player.Position.TotalSeconds);
        UpdatePlaybackLabels();
        ShowFullscreenControls();
    }

    private void UpdateSeekPreview()
    {
        if (!_dragging)
            return;
        SeekPreviewText.Text = string.Format(Texts["Media_SeekPreview"],
            MediaPlaybackMath.Clock(TimeSpan.FromSeconds(SeekSlider.Value)));
    }

    private void SpeedSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyPlaybackSpeed();

    private void ApplyPlaybackSpeed()
    {
        if (!_ready || _settingSpeed || SpeedSelector.SelectedItem is not ComboBoxItem option ||
            !double.TryParse(option.Tag?.ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var ratio) ||
            !MediaPlaybackMath.IsSupportedSpeed(ratio))
            return;

        try
        {
            Player.SpeedRatio = ratio;
            _speedUnsupported = false;
        }
        catch (InvalidOperationException)
        {
            ResetUnsupportedSpeed();
        }
        catch (NotSupportedException)
        {
            ResetUnsupportedSpeed();
        }

        SpeedSelector.ToolTip = Texts[_speedUnsupported ? "Media_SpeedUnsupported" : "Media_Speed"];
    }

    private void ResetUnsupportedSpeed()
    {
        _speedUnsupported = true;
        _settingSpeed = true;
        try
        {
            SpeedSelector.SelectedIndex = 1;
        }
        finally
        {
            _settingSpeed = false;
        }
    }

    private void SurfaceClickTimer_Tick(object? sender, EventArgs e)
    {
        _surfaceClickTimer.Stop();
        TogglePlayback();
    }

    private void MediaSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_ready)
            return;

        if (_isVideo && e.ClickCount >= 2)
        {
            _surfaceClickTimer.Stop();
            ToggleFullscreen();
        }
        else if (_isVideo)
        {
            // Defer a single click until the OS double-click window has elapsed,
            // so a video double click never briefly pauses or resumes playback.
            _surfaceClickTimer.Stop();
            _surfaceClickTimer.Start();
        }
        else
        {
            TogglePlayback();
        }
        ShowFullscreenControls();
        e.Handled = true;
    }

    private void PlaybackRoot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullScreen)
            return;
        var point = e.GetPosition(PlaybackRoot);
        if (_lastPointerPosition is Point previous &&
            Math.Abs(previous.X - point.X) + Math.Abs(previous.Y - point.Y) < 1)
            return;
        _lastPointerPosition = point;
        ShowFullscreenControls();
    }

    private void ShowFullscreenControls()
    {
        if (!_fullScreen)
            return;
        _lastPointerActivityUtc = DateTime.UtcNow;
        ControlsBar.Visibility = Visibility.Visible;
    }

    private void UpdateFullscreenControls()
    {
        if (!_fullScreen)
            return;
        var interacting = _dragging || ControlsBar.IsMouseOver || SpeedSelector.IsDropDownOpen
            || SeekSlider.IsKeyboardFocusWithin || VolumeSlider.IsKeyboardFocusWithin;
        if (MediaPlaybackMath.ShouldHideControls(_playing, interacting,
                DateTime.UtcNow - _lastPointerActivityUtc))
            ControlsBar.Visibility = Visibility.Collapsed;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isVideo)
            return;
        if (_fullScreen)
            ExitFullscreen();
        else
            EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, 2); // nearest monitor
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) ||
            PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;

        var physicalTopLeft = new Point(info.Monitor.Left, info.Monitor.Top);
        var physicalBottomRight = new Point(info.Monitor.Right, info.Monitor.Bottom);
        var topLeft = target.TransformFromDevice.Transform(physicalTopLeft);
        var bottomRight = target.TransformFromDevice.Transform(physicalBottomRight);

        _previousWindowState = WindowState;
        _previousBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _previousResizeMode = ResizeMode;
        _previousTopmost = Topmost;
        _fullScreen = true;
        IsImmersiveFullScreen = true;
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true; // cover the taskbar on the selected display
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;

        PlaybackRoot.RowDefinitions[1].Height = new GridLength(0);
        Grid.SetRow(ControlsBar, 0);
        ControlsBar.VerticalAlignment = VerticalAlignment.Bottom;
        ControlsBar.Margin = new Thickness(16, 0, 16, 16);
        ControlsBar.CornerRadius = new CornerRadius(12);
        ControlsBar.BorderThickness = new Thickness(0);
        ControlsBar.Opacity = 0.94;
        MediaSurface.Focus();
        _lastPointerPosition = null;
        ShowFullscreenControls();
        UpdatePlaybackLabels();
    }

    private void ExitFullscreen()
    {
        if (!_fullScreen)
            return;
        _fullScreen = false;
        ControlsBar.Visibility = Visibility.Visible;
        Grid.SetRow(ControlsBar, 1);
        PlaybackRoot.RowDefinitions[1].Height = GridLength.Auto;
        ControlsBar.VerticalAlignment = VerticalAlignment.Stretch;
        ControlsBar.Margin = new Thickness(0);
        ControlsBar.CornerRadius = new CornerRadius(0);
        ControlsBar.BorderThickness = new Thickness(0, 1, 0, 0);
        ControlsBar.Opacity = 1;
        Topmost = _previousTopmost;
        ResizeMode = _previousResizeMode;
        Left = _previousBounds.Left;
        Top = _previousBounds.Top;
        Width = _previousBounds.Width;
        Height = _previousBounds.Height;
        IsImmersiveFullScreen = false;
        WindowState = _previousWindowState;
        UpdatePlaybackLabels();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SpeedSelector.IsDropDownOpen)
                SpeedSelector.IsDropDownOpen = false;
            else if (_fullScreen)
                ExitFullscreen();
            else
                Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && _isVideo)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.F && _isVideo && Keyboard.Modifiers == ModifierKeys.None
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && !SeekSlider.IsKeyboardFocusWithin
                 && !VolumeSlider.IsKeyboardFocusWithin
                 && Keyboard.FocusedElement is not ButtonBase
                 && !SpeedSelector.IsKeyboardFocusWithin
                 && Keyboard.Modifiers == ModifierKeys.None)
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.None
                 && !SeekSlider.IsKeyboardFocusWithin && !VolumeSlider.IsKeyboardFocusWithin
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            Skip(-5);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None
                 && !SeekSlider.IsKeyboardFocusWithin && !VolumeSlider.IsKeyboardFocusWithin
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            Skip(5);
            e.Handled = true;
        }
        else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None
                 && !SpeedSelector.IsKeyboardFocusWithin)
        {
            Mute_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
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

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor, Work;
        public int Flags;
    }
}
