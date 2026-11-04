using System.Text;

namespace Cavdeck.Domain.Preview;

public enum TextCharset
{
    Utf8,
    Utf16Le,
    Utf16Be
}

public sealed record DecodedText(string Text, TextCharset Charset, bool Bom);

public static class TextCodec
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out DecodedText decoded)
    {
        if (bytes.IsEmpty)
        {
            decoded = new DecodedText("", TextCharset.Utf8, false);
            return true;
        }

        if (HasPrefix(bytes, 0xEF, 0xBB, 0xBF))
            return Finish(bytes[3..], TextCharset.Utf8, bom: true, out decoded);
        if (HasPrefix(bytes, 0xFF, 0xFE))
            return Finish(bytes[2..], TextCharset.Utf16Le, bom: true, out decoded);
        if (HasPrefix(bytes, 0xFE, 0xFF))
            return Finish(bytes[2..], TextCharset.Utf16Be, bom: true, out decoded);
        return Finish(bytes, TextCharset.Utf8, bom: false, out decoded);
    }

    public static byte[] Encode(string? text, TextCharset charset, bool bom)
    {
        Encoding encoding = charset switch
        {
            TextCharset.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: bom, throwOnInvalidBytes: true),
            TextCharset.Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: bom, throwOnInvalidBytes: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom, throwOnInvalidBytes: true)
        };
        var body = encoding.GetBytes(text ?? "");
        if (!bom)
            return body;
        var preamble = encoding.GetPreamble();
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private static bool Finish(ReadOnlySpan<byte> bytes, TextCharset charset, bool bom, out DecodedText decoded)
    {
        try
        {
            Encoding encoding = charset switch
            {
                TextCharset.Utf16Le => new UnicodeEncoding(false, false, true),
                TextCharset.Utf16Be => new UnicodeEncoding(true, false, true),
                _ => new UTF8Encoding(false, true)
            };
            decoded = new DecodedText(encoding.GetString(bytes), charset, bom);
            return true;
        }
        catch (DecoderFallbackException)
        {
            decoded = new DecodedText("", TextCharset.Utf8, false);
            return false;
        }
    }

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, byte first, byte second) =>
        bytes.Length >= 2 && bytes[0] == first && bytes[1] == second;

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, byte first, byte second, byte third) =>
        bytes.Length >= 3 && bytes[0] == first && bytes[1] == second && bytes[2] == third;
}
