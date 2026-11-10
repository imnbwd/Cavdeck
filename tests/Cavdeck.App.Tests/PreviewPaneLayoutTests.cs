using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class PreviewPaneLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckPreviewPane.xaml"));

    private static XDocument Progress() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckPreviewProgressView.xaml"));

    [Fact]
    public void Shared_progress_view_replaces_pdf_specific_progress_controls()
    {
        var pane = View();
        Assert.Contains(pane.Descendants(), node =>
            node.Name.LocalName == "PreviewProgressView" &&
            ((string?)node.Attribute("Visibility"))?.Contains("ShowPreviewDownload") == true);
        var progress = Progress();
        Assert.Contains(progress.Descendants(Wpf + "ProgressBar"), bar =>
            ((string?)bar.Attribute("Value"))?.Contains("PreviewProgress") == true);
        Assert.Contains(progress.Descendants(Wpf + "Button"), button =>
            ((string?)button.Attribute("Command"))?.Contains("CancelPreviewCommand") == true &&
            ((string?)button.Attribute("AutomationProperties.Name"))?.Contains("Preview_CancelDownload") == true);
    }

    [Fact]
    public void Unsaved_and_conflict_prompts_reflow_without_covering_document()
    {
        var view = View();
        var host = view.Descendants(Wpf + "StackPanel").Single(element =>
            (string?)element.Attribute("Grid.Row") == "2");
        Assert.Null(host.Attribute("Panel.ZIndex"));
        Assert.Contains(host.Descendants(Wpf + "Border"), element =>
            ((string?)element.Attribute("Visibility"))?.Contains("ShowLeaveBar") == true);
        Assert.Contains(host.Descendants(Wpf + "Border"), element =>
            ((string?)element.Attribute("Visibility"))?.Contains("ShowConflictBar") == true);
        Assert.Contains(view.Descendants(Wpf + "Grid"), element =>
            (string?)element.Attribute("Grid.Row") == "3" &&
            element.Elements().Any(child => child.Name == Wpf + "TextBox"));
    }

    [Fact]
    public void File_actions_share_a_single_header_row_without_reserving_space_in_document_modes()
    {
        var view = View();
        var header = view.Descendants(Wpf + "Grid").Single(element =>
            (string?)element.Attribute("Grid.Row") == "0" &&
            element.Elements(Wpf + "Grid.ColumnDefinitions").Any());
        var actions = header.Elements(Wpf + "StackPanel").Single(element =>
            (string?)element.Attribute("Grid.Column") == "1");

        Assert.Equal("Horizontal", (string?)actions.Attribute("Orientation"));
        var buttons = actions.Elements(Wpf + "Button").ToArray();
        Assert.Equal(4, buttons.Length);
        Assert.Contains("ShellCommand", (string?)buttons[0].Attribute("Command"));
        Assert.Contains("DownloadCommand", (string?)buttons[1].Attribute("Command"));
        Assert.Contains("ImageViewerCommand", (string?)buttons[2].Attribute("Command"));
        Assert.Contains("ShowImageViewer", (string?)buttons[2].Attribute("Visibility"));
        Assert.Contains("ToggleExpandedCommand", (string?)buttons[3].Attribute("Command"));
        Assert.All(buttons, button =>
        {
            Assert.NotNull(button.Attribute("ToolTip"));
            Assert.NotNull(button.Attribute("AutomationProperties.Name"));
        });

        Assert.Contains(header.Descendants(Wpf + "TextBlock"), title =>
            ((string?)title.Attribute("Text"))?.Contains("{Binding Title}") == true &&
            (string?)title.Attribute("TextTrimming") == "CharacterEllipsis");
        Assert.Contains(header.Descendants(Wpf + "ColumnDefinition"), column =>
            (string?)column.Attribute("Width") == "*" &&
            (string?)column.Attribute("MinWidth") == "0");

        var editorModes = view.Descendants(Wpf + "StackPanel").Single(element =>
            (string?)element.Attribute("Grid.Row") == "1");
        Assert.Contains("ShowEditor", (string?)editorModes.Attribute("Visibility"));
        Assert.Equal(2, editorModes.Elements(Wpf + "Button").Count());
        Assert.DoesNotContain(editorModes.Descendants(Wpf + "Button"), button =>
            ((string?)button.Attribute("Command"))?.Contains("ShellCommand") == true ||
            ((string?)button.Attribute("Command"))?.Contains("DownloadCommand") == true);
    }

    [Fact]
    public void Contextual_toolbar_and_protection_actions_keep_accessible_commands()
    {
        var buttons = View().Descendants(Wpf + "Button").ToArray();
        foreach (var command in new[] {
                     "PreviewCommand", "EditCommand", "ToggleExpandedCommand", "ImageViewerCommand",
                     "ShellCommand", "DownloadCommand", "SaveCommand",
                     "RevertCommand", "DiscardLeaveCommand", "StayCommand" })
            Assert.Contains(buttons, button =>
                ((string?)button.Attribute("Command"))?.Contains(command) == true &&
                button.Attribute("AutomationProperties.Name") is not null);

        Assert.Contains(buttons, button =>
            ((string?)button.Attribute("ToolTip"))?.Contains("ExpandLabel") == true);
    }

    [Fact]
    public void Preview_notices_are_centered_with_a_theme_aware_file_icon_and_retry()
    {
        var notice = View().Descendants(Wpf + "StackPanel").Single(element =>
            ((string?)element.Attribute("Visibility"))?.Contains("ShowNotice") == true);

        Assert.Contains("PageEmptyHost", (string?)notice.Attribute("Style"));
        Assert.Equal("Center", (string?)notice.Attribute("TextBlock.TextAlignment"));

        var icon = notice.Elements(Wpf + "Path").Single();
        Assert.Contains("PageEmptyGlyph", (string?)icon.Attribute("Style"));
        Assert.Contains("IconGenericFile", (string?)icon.Attribute("Data"));
        Assert.Contains("MutedTextBrush", (string?)icon.Attribute("Stroke"));

        var message = notice.Elements(Wpf + "TextBlock").Single();
        Assert.Contains("Notice", (string?)message.Attribute("Text"));
        Assert.Equal("Wrap", (string?)message.Attribute("TextWrapping"));

        var retry = notice.Elements(Wpf + "Button").Single();
        Assert.Equal("Center", (string?)retry.Attribute("HorizontalAlignment"));
        Assert.Contains("RetryCommand", (string?)retry.Attribute("Command"));
        Assert.Contains("ShowRetry", (string?)retry.Attribute("Visibility"));
    }

    [Fact]
    public void Text_preview_keeps_its_inset_stable_when_focus_changes()
    {
        var style = View().Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "EditorBox");
        var setters = style.Elements(Wpf + "Setter").ToArray();

        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "Padding" &&
            (string?)setter.Attribute("Value") == "10,8");
        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "BorderThickness" &&
            (string?)setter.Attribute("Value") == "1");

        var template = style.Descendants(Wpf + "ControlTemplate").Single();
        Assert.DoesNotContain(template.Descendants(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "BorderThickness");

        var readOnly = template.Descendants(Wpf + "Trigger").Single(trigger =>
            (string?)trigger.Attribute("Property") == "IsReadOnly" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(readOnly.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "Bd" &&
            (string?)setter.Attribute("Property") == "BorderBrush" &&
            ((string?)setter.Attribute("Value"))?.Contains("SurfaceBrush") == true);

        var focused = template.Descendants(Wpf + "Trigger").Single(trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocusWithin");
        Assert.Contains(focused.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "Bd" &&
            (string?)setter.Attribute("Property") == "BorderBrush" &&
            ((string?)setter.Attribute("Value"))?.Contains("FocusBorderBrush") == true);

        var hover = template.Descendants(Wpf + "MultiTrigger").Single();
        Assert.Contains(hover.Descendants(Wpf + "Condition"), condition =>
            (string?)condition.Attribute("Property") == "IsReadOnly" &&
            (string?)condition.Attribute("Value") == "False");
    }


    [Theory]
    [InlineData("Colors.Light.xaml", 1.8)]
    [InlineData("Colors.Dark.xaml", 1.9)]
    public void Text_selection_remains_visible_without_covering_glyphs_in_both_themes(
        string theme, double minimumBackgroundContrast)
    {
        var style = View().Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "EditorBox");
        var setters = style.Elements(Wpf + "Setter").ToDictionary(
            element => (string)element.Attribute("Property")!,
            element => (string)element.Attribute("Value")!);

        Assert.Equal("{DynamicResource EditorSelectionBrush}", setters["SelectionBrush"]);
        Assert.Equal("{DynamicResource PrimaryTextBrush}", setters["SelectionTextBrush"]);

        // WPF's adorner overlays the glyphs as well as the background:
        // at opacity 1 the text vanishes, even when SelectionTextBrush is set.
        var opacity = double.Parse(setters["SelectionOpacity"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(opacity, 0.25, 0.45);

        var palette = XDocument.Load(Path.Combine(AppContext.BaseDirectory, theme));
        var colors = palette.Descendants(Wpf + "SolidColorBrush").ToDictionary(
            element => (string)element.Attribute(Xaml + "Key")!,
            element => (string)element.Attribute("Color")!);

        Assert.NotEqual(colors["SelectedBrush"], colors["EditorSelectionBrush"]);
        foreach (var surfaceKey in new[] { "SurfaceBrush", "InputBackgroundBrush" })
        {
            var background = colors[surfaceKey];
            var selection = BlendColor(colors["EditorSelectionBrush"], background, opacity);
            var selectedGlyphs = BlendColor(colors["EditorSelectionBrush"], colors["PrimaryTextBrush"], opacity);
            Assert.True(ColorContrast(selection, background) >= minimumBackgroundContrast,
                $"Selection is too subtle on {surfaceKey} in {theme}.");
            Assert.True(ColorContrast(selection, selectedGlyphs) >= 4.5,
                $"Selection overlay obscures selected text on {surfaceKey} in {theme}.");
        }
    }

    private static string BlendColor(string overlay, string underneath, double opacity)
    {
        return "#" + string.Concat(new[] { 1, 3, 5 }.Select(offset =>
        {
            var top = Convert.ToInt32(overlay.Substring(offset, 2), 16);
            var bottom = Convert.ToInt32(underneath.Substring(offset, 2), 16);
            return Convert.ToInt32(Math.Round(top * opacity + bottom * (1 - opacity))).ToString("X2");
        }));
    }

    private static double ColorContrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var channels = new[] { 1, 3, 5 }.Select(index =>
            {
                var channel = Convert.ToInt32(hex.Substring(index, 2), 16) / 255d;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }).ToArray();
            return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Fact]
    public void Read_only_document_surface_is_visually_distinct_from_editing()
    {
        var view = View();
        var style = view.Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "EditorBox");
        Assert.Contains(style.Descendants(Wpf + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsReadOnly" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(view.Descendants(Wpf + "TextBox"), editor =>
            ((string?)editor.Attribute("IsReadOnly"))?.Contains("IsReadOnly") == true);
    }
}
