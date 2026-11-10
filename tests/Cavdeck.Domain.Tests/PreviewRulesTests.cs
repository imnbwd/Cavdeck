using Cavdeck.Domain.Preview;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class PreviewRulesTests
{
    [Theory]
    [InlineData("logo.PNG", false, 32_000, PreviewKind.Image)]
    [InlineData("anim.gif", false, 1_000, PreviewKind.Image)]
    [InlineData("photo.webp", false, 1_000, PreviewKind.Image)]
    [InlineData("notes.md", false, 20, PreviewKind.Text)]
    [InlineData("app.json", false, 20, PreviewKind.Text)]
    [InlineData("page.xml", false, 20, PreviewKind.Text)]
    [InlineData("sheet.csv", false, 20, PreviewKind.Text)]
    [InlineData("readme", false, 20, PreviewKind.Text)]
    [InlineData("index.html", false, 20, PreviewKind.Text)]
    [InlineData("icon.svg", false, 20, PreviewKind.Text)]
    [InlineData("Program.cs", false, 20, PreviewKind.Text)]
    [InlineData("clip.mp4", false, 20, PreviewKind.External)]
    [InlineData("REPORT.PDF", false, 20, PreviewKind.Pdf)]
    [InlineData("pack.zip", false, 20, PreviewKind.External)]
    [InlineData("scan.tif", false, 20, PreviewKind.External)]
    [InlineData("docs", true, 0, PreviewKind.Folder)]
    public void Decide_matches_what_can_be_opened_here(string name, bool folder, long size, PreviewKind expected) =>
        Assert.Equal(expected, PreviewRules.Decide(name, folder, size).Kind);

    [Theory]
    [InlineData("clip.mp4", null, PreviewFileType.Video)]
    [InlineData("track.mp3", null, PreviewFileType.Audio)]
    [InlineData("unknown.bin", "video/mp4", PreviewFileType.Video)]
    [InlineData("unknown.bin", "image/png; charset=binary", PreviewFileType.Image)]
    [InlineData("unknown.bin", "application/pdf", PreviewFileType.Pdf)]
    [InlineData("unknown.bin", "text/plain", PreviewFileType.Unknown)]
    [InlineData("notes.txt", "image/png", PreviewFileType.Text)]
    [InlineData("icon.svg", "image/svg+xml", PreviewFileType.Text)]
    public void Classify_recognizes_known_types_without_changing_existing_preview_support(
        string name, string? contentType, PreviewFileType expected) =>
        Assert.Equal(expected, PreviewRules.Classify(name, contentType));

    [Fact]
    public void Audio_and_video_are_recognized_but_remain_external_until_a_viewer_exists()
    {
        Assert.Equal(PreviewKind.External, PreviewRules.Decide("track.mp3", false, 100).Kind);
        Assert.Equal(PreviewKind.External, PreviewRules.Decide("clip.mp4", false, 100).Kind);
    }

    [Fact]
    public void Text_over_the_cap_is_not_opened_for_editing()
    {
        var decision = PreviewRules.Decide("notes.txt", false, PreviewRules.MaxTextBytes + 1);
        Assert.Equal(PreviewKind.TooLarge, decision.Kind);
    }

    [Fact]
    public void Text_at_the_cap_can_be_edited()
    {
        var decision = PreviewRules.Decide("notes.txt", false, PreviewRules.MaxTextBytes);
        Assert.Equal(PreviewKind.Text, decision.Kind);
    }

    [Fact]
    public void Image_over_the_cap_is_not_decoded()
    {
        var decision = PreviewRules.Decide("cover.png", false, PreviewRules.MaxImageBytes + 1);
        Assert.Equal(PreviewKind.TooLarge, decision.Kind);
    }

    [Fact]
    public void Pdf_over_the_cap_is_not_opened_in_the_viewer()
    {
        Assert.Equal(PreviewKind.Pdf, PreviewRules.Decide("doc.pdf", false, PreviewRules.MaxPdfBytes).Kind);
        Assert.Equal(PreviewKind.TooLarge, PreviewRules.Decide("doc.pdf", false, PreviewRules.MaxPdfBytes + 1).Kind);
    }

    [Fact]
    public void Unknown_size_is_not_loaded_into_the_editor()
    {
        Assert.Equal(PreviewKind.TooLarge, PreviewRules.Decide("notes.txt", false, -1).Kind);
    }

    [Fact]
    public void Shell_open_accepts_the_cap_and_refuses_the_next_byte()
    {
        Assert.True(PreviewRules.CanOpenInShell(0));
        Assert.True(PreviewRules.CanOpenInShell(PreviewRules.MaxShellBytes));
        Assert.False(PreviewRules.CanOpenInShell(PreviewRules.MaxShellBytes + 1));
        Assert.False(PreviewRules.CanOpenInShell(-1));
    }
}
