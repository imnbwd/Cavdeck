using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Explorer;
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
    private PreviewFileLease? _lease;
    private bool _disposed;
    private bool _ready;
    private bool _playing;
    private bool _dragging;
    private bool _settingSeek;
    private bool _muted;
    private readonly bool _isVideo;
    private TimeSpan _duration;
    private string _feedbackKey = "Media_Loading";
    private long _downloadedBytes;

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
        KindLabel = texts[_isVideo ? "Media_KindVideo" : "Media_KindAudio"];

        InitializeComponent();
        DataContext = this;
        UseTexts(texts);
        AudioArtwork.Visibility = type == PreviewFileType.Audio
            ? Visibility.Visible : Visibility.Collapsed;
        if (type == PreviewFileType.Audio)
        {
            Width = 740;
            Height = 390;
            MinHeight = 340;
            // Keep the media element loaded for sound, but do not display a black frame.
            Player.Opacity = 0;
        }

        Player.Volume = VolumeSlider.Value;
        Feedback.PrimaryCommand = new RelayCommand(_ => Close());
        Feedback.ShowPrimaryButton = true;
        Feedback.IsIndeterminate = true;
        Feedback.ShowProgress = true;
        KindLabelLabel.Text = KindLabel;
        UpdatePlaybackLabels();
        _timer.Tick += Timer_Tick;
        SeekSlider.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _dragging = true), true);
        SeekSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                _dragging = false;
                ApplySeek(SeekSlider.Value);
            }), true);
        Texts.PropertyChanged += Texts_PropertyChanged;
        Loaded += (_, _) => _ = LoadAsync();
        Closed += (_, _) => DisposePlayback();
    }

    public string FileName { get; }
    public string KindLabel { get; }
    public LocalizationService Texts { get; }

    private async Task LoadAsync()
    {
        var token = _lifetime.Token;
        Feedback.Visibility = Visibility.Visible;
        _feedbackKey = "Media_Loading";
        Feedback.Headline = Texts[_feedbackKey];
        Feedback.PrimaryButtonText = Texts["Media_Cancel"];
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
            _feedbackKey = "Media_Opening";
            Feedback.Headline = Texts[_feedbackKey];
            Feedback.ShowProgress = true;
            Feedback.IsIndeterminate = true;
            Feedback.ProgressDescription = "";
            Player.Source = new Uri(Path.GetFullPath(_lease.FilePath));
            // LoadedBehavior=Manual requires an explicit Play call to start
            // opening the local source on all supported WPF media backends.
            Player.Play();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closing cancels the download; the downloader removes partial files.
        }
        catch (Exception)
        {
            if (!_disposed)
                ShowFailure("Media_DownloadFailed");
        }
        finally
        {
            if (pending is not null)
                await pending.DisposeAsync();
        }
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_disposed)
            return;
        _ready = true;
        _duration = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        SeekSlider.IsEnabled = _duration > TimeSpan.Zero;
        SeekSlider.Maximum = Math.Max(1, _duration.TotalSeconds);
        PlayPauseButton.IsEnabled = true;
        BackButton.IsEnabled = _duration > TimeSpan.Zero;
        ForwardButton.IsEnabled = _duration > TimeSpan.Zero;
        Feedback.Visibility = Visibility.Collapsed;
        UpdateTimeLabel();
        Player.Play();
        _playing = true;
        _timer.Start();
        UpdatePlaybackLabels();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
            return;
        Player.Stop();
        _playing = false;
        _timer.Stop();
        UpdateSeekValue(0);
        UpdatePlaybackLabels();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_disposed)
            return;
        _ready = false;
        _playing = false;
        _timer.Stop();
        PlayPauseButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        ShowFailure("Media_Unsupported");
        UpdatePlaybackLabels();
    }

    private void ShowFailure(string key)
    {
        if (_disposed)
            return;
        _feedbackKey = key;
        Feedback.Visibility = Visibility.Visible;
        Feedback.Headline = Texts[key];
        Feedback.ShowProgress = false;
        Feedback.IsIndeterminate = false;
        Feedback.ProgressDescription = "";
        Feedback.PrimaryButtonText = Texts["Media_Close"];
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
        }
        else
        {
            Player.Play();
            _playing = true;
            _timer.Start();
        }
        UpdatePlaybackLabels();
    }

    private void UpdatePlaybackLabels()
    {
        PlayPauseButton.Content = Texts[_playing ? "Media_Pause" : "Media_Play"];
        PlayPauseButton.ToolTip = PlayPauseButton.Content;
        System.Windows.Automation.AutomationProperties.SetName(PlayPauseButton,
            (string)PlayPauseButton.Content);
        MuteButton.Content = Texts[_muted ? "Media_Unmute" : "Media_Mute"];
        MuteButton.ToolTip = MuteButton.Content;
        System.Windows.Automation.AutomationProperties.SetName(MuteButton, (string)MuteButton.Content);
    }

    private void Texts_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;
        UpdatePlaybackLabels();
        KindLabelLabel.Text = Texts[_isVideo ? "Media_KindVideo" : "Media_KindAudio"];
        Feedback.Headline = Texts[_feedbackKey];
        Feedback.PrimaryButtonText = Texts[_feedbackKey is "Media_Unsupported" or "Media_DownloadFailed"
            ? "Media_Close" : "Media_Cancel"];
        if (Feedback.ShowProgress && !Feedback.IsIndeterminate && _size > 0)
            Feedback.ProgressDescription = string.Format(Texts["Media_Progress"],
                ExplorerText.FormatBytes(_downloadedBytes), ExplorerText.FormatBytes(_size));
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
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
        var current = _dragging ? TimeSpan.FromSeconds(SeekSlider.Value) : Player.Position;
        TimeLabel.Text = $"{MediaPlaybackMath.Clock(current)} / {MediaPlaybackMath.Clock(_duration)}";
    }

    private void ApplySeek(double seconds)
    {
        if (!_ready || _duration <= TimeSpan.Zero)
            return;
        Player.Position = MediaPlaybackMath.Seek(Player.Position, seconds, _duration);
        UpdateTimeLabel();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingSeek || !_ready)
            return;
        if (_dragging)
            UpdateTimeLabel();
        else
            ApplySeek(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Player is null)
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
        UpdateSeekValue(Player.Position.TotalSeconds);
    }

    private void MediaSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _ready)
        {
            TogglePlayback();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && _isVideo)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
        else if (e.Key == Key.Space && !SeekSlider.IsKeyboardFocusWithin
                 && !VolumeSlider.IsKeyboardFocusWithin
                 && Keyboard.FocusedElement is not ButtonBase
                 && Keyboard.Modifiers == ModifierKeys.None)
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.None
                 && !SeekSlider.IsKeyboardFocusWithin && !VolumeSlider.IsKeyboardFocusWithin)
        {
            Skip(-5);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None
                 && !SeekSlider.IsKeyboardFocusWithin && !VolumeSlider.IsKeyboardFocusWithin)
        {
            Skip(5);
            e.Handled = true;
        }
        else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None)
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
        Texts.PropertyChanged -= Texts_PropertyChanged;
        try
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
        }
        finally
        {
            // On some Windows codecs handles are released slightly after Close.
            // PreviewFileDownloader also prunes stale temp files as a safety net.
            if (_lease is not null)
            {
                _ = _lease.DisposeAsync();
                _lease = null;
            }
            _lifetime.Dispose();
        }
    }
}
