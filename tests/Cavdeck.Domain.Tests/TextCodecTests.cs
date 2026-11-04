using System.Text;
using Cavdeck.Domain.Preview;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class TextCodecTests
{
    [Fact]
    public void Utf8_roundtrip_keeps_newlines()
    {
        var original = "line\r\nline\n"u8.ToArray();
        Assert.True(TextCodec.TryDecode(original, out var decoded));
        Assert.Equal("line\r\nline\n", decoded.Text);
        Assert.Equal(TextCharset.Utf8, decoded.Charset);
        Assert.False(decoded.Bom);
        Assert.Equal(original, TextCodec.Encode(decoded.Text, decoded.Charset, decoded.Bom));
    }

    [Fact]
    public void Utf8_bom_is_preserved()
    {
        var original = Encoding.UTF8.GetPreamble().Concat("你好"u8.ToArray()).ToArray();
        Assert.True(TextCodec.TryDecode(original, out var decoded));
        Assert.Equal("你好", decoded.Text);
        Assert.True(decoded.Bom);
        Assert.Equal(original, TextCodec.Encode(decoded.Text, decoded.Charset, decoded.Bom));
    }

    [Fact]
    public void Utf16_bom_roundtrips()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var original = encoding.GetPreamble().Concat(encoding.GetBytes("ab")).ToArray();
        Assert.True(TextCodec.TryDecode(original, out var decoded));
        Assert.Equal(TextCharset.Utf16Le, decoded.Charset);
        Assert.True(decoded.Bom);
        Assert.Equal(original, TextCodec.Encode(decoded.Text, decoded.Charset, decoded.Bom));
    }

    [Fact]
    public void Empty_file_is_editable_utf8()
    {
        Assert.True(TextCodec.TryDecode([], out var decoded));
        Assert.Equal("", decoded.Text);
        Assert.Empty(TextCodec.Encode(decoded.Text, decoded.Charset, decoded.Bom));
    }

    [Fact]
    public void Invalid_utf8_is_refused()
    {
        Assert.False(TextCodec.TryDecode([0xFF], out _));
    }
}
