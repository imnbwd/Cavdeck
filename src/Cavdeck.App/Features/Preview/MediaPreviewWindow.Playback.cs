using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Features.Explorer;

namespace Cavdeck.App.Features.Preview;

public partial class MediaPreviewWindow
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _surfaceClickTimer = new();
    private bool _ready;
    private bool _playing;
    private bool _dragging;
    private bool _settingSeek;
    private bool _muted;
    private bool _ended;
    private bool _speedUnsupported;
    private bool _settingSpeed;

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
}
