using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class MediaPreviewLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Window() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckMediaPreviewWindow.xaml"));

    [Fact]
    public void Playback_uses_manual_media_element_and_releases_its_source_on_close()
    {
        var view = Window();
        var player = Assert.Single(view.Descendants(Wpf + "MediaElement"));
        Assert.Equal("Manual", (string?)player.Attribute("LoadedBehavior"));
        Assert.Equal("Close", (string?)player.Attribute("UnloadedBehavior"));
        Assert.Equal("True", (string?)player.Attribute("ScrubbingEnabled"));
        Assert.Equal("Uniform", (string?)player.Attribute("Stretch"));
        Assert.NotNull(player.Attribute("MediaOpened"));
        Assert.NotNull(player.Attribute("MediaEnded"));
        Assert.NotNull(player.Attribute("MediaFailed"));
        Assert.Null(player.Attribute("Source"));
    }

    [Fact]
    public void Playback_controls_have_accessible_names_and_feedback()
    {
        var view = Window();
        var buttons = view.Descendants(Wpf + "Button").ToArray();
        Assert.Contains(buttons, button => (string?)button.Attribute(Xaml + "Name") == "PlayPauseButton" &&
            (string?)button.Attribute("IsEnabled") == "False" &&
            button.Attribute("AutomationProperties.Name") is not null);
        Assert.Contains(buttons, button => (string?)button.Attribute(Xaml + "Name") == "MuteButton" &&
            button.Attribute("AutomationProperties.Name") is not null);
        // Cancel/close lives in the shared feedback view rather than duplicating
        // a private button in each media window.

        var sliders = view.Descendants(Wpf + "Slider").ToArray();
        Assert.Contains(sliders, slider => (string?)slider.Attribute(Xaml + "Name") == "SeekSlider" &&
            (string?)slider.Attribute("IsEnabled") == "False" &&
            slider.Attribute("AutomationProperties.Name") is not null);
        Assert.Contains(sliders, slider => (string?)slider.Attribute(Xaml + "Name") == "VolumeSlider" &&
            slider.Attribute("AutomationProperties.Name") is not null);

        Assert.Contains(view.Descendants(), feedback =>
            feedback.Name.LocalName == "PreviewFeedbackView" &&
            (string?)feedback.Attribute(Xaml + "Name") == "Feedback");
    }

    [Fact]
    public void Playback_has_localized_audio_video_layout_and_keyboard_controls()
    {
        var view = Window();
        Assert.Equal("Window_PreviewKeyDown",
            (string?)view.Root?.Attribute("PreviewKeyDown"));
        Assert.Contains(view.Descendants(Wpf + "StackPanel"), panel =>
            (string?)panel.Attribute(Xaml + "Name") == "AudioArtwork");
        Assert.Contains(view.Descendants(Wpf + "TextBlock"), label =>
            ((string?)label.Attribute("Text"))?.Contains("Media_Hint") == true);
    }
}
