using System.Text;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Cli;

internal sealed record StorageUri(string Bucket, string Key, bool HasTrailingSlash)
{
    public static StorageUri Parse(string value)
    {
        if (!value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
            throw new CliException(CliError.Arguments);
        var address = value[5..];
        var slash = address.IndexOf('/');
        var bucket = slash < 0 ? address : address[..slash];
        var key = slash < 0 ? "" : address[(slash + 1)..];
        if (bucket.Length == 0 || bucket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new CliException(CliError.Arguments);
        ValidateKey(key);
        return new(bucket, key, value.EndsWith('/'));
    }

    public static void ValidateKey(string key)
    {
        if (Encoding.UTF8.GetByteCount(key) > ObjectLayout.MaxKeyBytes || key.Any(char.IsControl))
            throw new CliException(CliError.Arguments);
        // The SDK can drop a leading slash, and HTTP URI construction can collapse dot segments.
        if (key.StartsWith('/') || key.Split('/').Any(part => part is "." or "..") ||
            key.Any(char.IsSurrogate) && !IsWellFormed(key))
            throw new CliException(new(9, "UNSUPPORTED_KEY", CliText.Get("InvalidKey")));
    }

    private static bool IsWellFormed(string key)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(key); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    public void RequireObject()
    {
        if (string.IsNullOrWhiteSpace(Key))
            throw new CliException(CliError.Arguments);
    }
}
