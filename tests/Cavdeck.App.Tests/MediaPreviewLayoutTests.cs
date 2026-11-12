using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class MediaPreviewLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Window() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckMediaPreviewWindow.xaml"));

    private static XDocument Inputs() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckInputs.xaml"));

    [Fact]
    public void Playback_uses_a_dedicated_host_for_fresh_media_elements_per_attempt()
    {
        var view = Window();
        var host = Assert.Single(view.Descendants(Wpf + "Grid").Where(grid =>
            (string?)grid.Attribute(Xaml + "Name") == "MediaHost"));
        Assert.Equal("False", (string?)host.Attribute("IsHitTestVisible"));
        Assert.Empty(view.Descendants(Wpf + "MediaElement"));
    }

    [Fact]
    public void Loading_feedback_stays_inside_the_media_surface_and_does_not_cover_controls()
    {
        var view = Window();
        var feedback = Assert.Single(view.Descendants().Where(element =>
            element.Name.LocalName == "PreviewFeedbackView" &&
            (string?)element.Attribute(Xaml + "Name") == "Feedback"));
        Assert.Equal("0", (string?)feedback.Attribute("Grid.Row"));
        Assert.Null(feedback.Attribute("Grid.RowSpan"));

        var controls = Assert.Single(view.Descendants(Wpf + "Border").Where(border =>
            (string?)border.Attribute(Xaml + "Name") == "ControlsBar"));
        Assert.Equal("1", (string?)controls.Attribute("Grid.Row"));
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
        Assert.Contains(view.Descendants(Wpf + "Grid"), grid =>
            (string?)grid.Attribute(Xaml + "Name") == "AudioArtwork");
        Assert.DoesNotContain(view.Descendants(Wpf + "TextBlock"), label =>
            ((string?)label.Attribute("Text"))?.Contains("Media_Hint") == true);
    }

    [Fact]
    public void Audio_and_video_share_the_file_name_titlebar_without_duplicate_audio_identity()
    {
        var view = Window();
        Assert.Equal("1060", (string?)view.Root?.Attribute("Width"));
        Assert.Equal("{Binding FileName}", (string?)view.Root?.Attribute("Title"));
        Assert.Equal("False", (string?)view.Root?.Attribute("ShowBrandName"));

        var caption = Assert.Single(view.Descendants(Wpf + "TextBlock").Where(label =>
            (string?)label.Attribute(Xaml + "Name") == "MediaCaption"));
        Assert.Equal("{Binding FileName}", (string?)caption.Attribute("Text"));
        Assert.Equal("CharacterEllipsis", (string?)caption.Attribute("TextTrimming"));
        Assert.Equal("{Binding FileName}", (string?)caption.Attribute("ToolTip"));

        var artwork = Assert.Single(view.Descendants(Wpf + "Grid").Where(grid =>
            (string?)grid.Attribute(Xaml + "Name") == "AudioArtwork"));
        var audioLabels = artwork.Descendants(Wpf + "TextBlock").ToArray();
        Assert.DoesNotContain(audioLabels, label =>
            (string?)label.Attribute("Text") == "{Binding FileName}");
        var audioTypeLabel = Assert.Single(audioLabels, label =>
            ((string?)label.Attribute("Text"))?.Contains("Media_KindAudio") == true);
        Assert.Equal("{DynamicResource FontSizeTitle}", (string?)audioTypeLabel.Attribute("FontSize"));
        Assert.Equal("SemiBold", (string?)audioTypeLabel.Attribute("FontWeight"));
        Assert.Contains(audioLabels, label =>
            (string?)label.Attribute("Text") == "{Binding MediaSummary}");
        Assert.Equal("0", (string?)Assert.Single(view.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") == "Feedback"))
            .Attribute("Grid.Row"));
    }

    [Fact]
    public void Video_has_fullscreen_controls_and_speed_choices_with_dynamic_media_host()
    {
        var view = Window();
        var fullscreen = Assert.Single(view.Descendants(Wpf + "Button").Where(button =>
            (string?)button.Attribute(Xaml + "Name") == "FullscreenButton"));
        Assert.Equal("Fullscreen_Click", (string?)fullscreen.Attribute("Click"));
        Assert.NotNull(fullscreen.Attribute("AutomationProperties.Name"));
        var speeds = Assert.Single(view.Descendants(Wpf + "ComboBox").Where(box =>
            (string?)box.Attribute(Xaml + "Name") == "SpeedSelector"));
        Assert.Equal("SpeedSelector_SelectionChanged", (string?)speeds.Attribute("SelectionChanged"));
        Assert.Equal(new[] { "0.75", "1", "1.25", "1.5", "2" },
            speeds.Elements(Wpf + "ComboBoxItem").Select(option => (string?)option.Attribute("Tag")).ToArray());
        Assert.Empty(view.Descendants(Wpf + "MediaElement"));
        Assert.Single(view.Descendants(Wpf + "Grid"), grid =>
            (string?)grid.Attribute(Xaml + "Name") == "MediaHost");
        Assert.Contains(view.Descendants(Wpf + "ToolTip"), tip =>
            (string?)tip.Attribute(Xaml + "Name") == "SeekPreviewTip");
        Assert.Equal("PlaybackRoot_PreviewMouseMove", (string?)Assert.Single(
            view.Descendants(Wpf + "Grid").Where(element =>
                (string?)element.Attribute(Xaml + "Name") == "PlaybackRoot"))
            .Attribute("PreviewMouseMove"));
    }

    [Fact]
    public void Playback_has_icon_buttons_and_themed_seek_and_volume_controls()
    {
        var view = Window();
        foreach (var name in new[] { "PlayPauseButton", "BackButton", "ForwardButton", "MuteButton" })
        {
            var button = Assert.Single(view.Descendants(Wpf + "Button").Where(element =>
                (string?)element.Attribute(Xaml + "Name") == name));
            Assert.NotEmpty(button.Descendants(Wpf + "Path"));
            Assert.Empty(button.Descendants(Wpf + "TextBlock"));
        }

        foreach (var name in new[] { "SeekSlider", "VolumeSlider" })
        {
            var slider = Assert.Single(view.Descendants(Wpf + "Slider").Where(element =>
                (string?)element.Attribute(Xaml + "Name") == name));
            Assert.Equal("{StaticResource MediaSliderStyle}", (string?)slider.Attribute("Style"));
        }

        var seekSlider = Assert.Single(view.Descendants(Wpf + "Slider").Where(element =>
            (string?)element.Attribute(Xaml + "Name") == "SeekSlider"));
        Assert.Equal("True", (string?)seekSlider.Attribute("IsMoveToPointEnabled"));

        var volumeSlider = Assert.Single(view.Descendants(Wpf + "Slider").Where(element =>
            (string?)element.Attribute(Xaml + "Name") == "VolumeSlider"));
        Assert.Equal("True", (string?)volumeSlider.Attribute("IsMoveToPointEnabled"));
        Assert.Equal("False", (string?)volumeSlider.Attribute(
            XName.Get("InputAssist.ShowFocusIndicator", "clr-namespace:Cavdeck.App.Controls")));
        Assert.Null(volumeSlider.Attribute("Focusable"));

        var inputs = Inputs();
        var mediaSliderStyle = Assert.Single(inputs.Descendants(Wpf + "Style").Where(element =>
            (string?)element.Attribute(Xaml + "Key") == "MediaSliderStyle"));
        Assert.Contains(mediaSliderStyle.Descendants(Wpf + "Track"), track =>
            (string?)track.Attribute(Xaml + "Name") == "PART_Track");
        Assert.DoesNotContain(view.Descendants(Wpf + "Style"), element =>
            (string?)element.Attribute(Xaml + "Key") == "MediaSliderStyle");
    }
}
