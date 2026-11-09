using System.Buffers.Text;
using System.IO.Compression;
using System.Text.Json;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Configuration;

namespace Cavdeck.Cli;

internal sealed record SearchIdentity(Guid ConnectionId, string Bucket, string Prefix, string Query, string MatchMode);

internal sealed record SearchToken(int SchemaVersion, SearchIdentity Identity, string? ContinuationToken,
    int Offset, string[] PreviousKeys)
{
    private const int MaxEncodedLength = 24_000;
    private const int MaxDecodedLength = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Checkpoints => Path.Combine(CavdeckDataPaths.LocalDirectory, "cli", "search");

    [System.Text.Json.Serialization.JsonIgnore]
    public PrefixSearchCursor Cursor => new(ContinuationToken, Offset);

    public static SearchToken? Decode(string? encoded, SearchIdentity identity, string? directory = null)
    {
        if (encoded is null)
            return null;
        try
        {
            using var input = OpenCheckpoint(encoded, directory ?? Checkpoints);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                if (payload.Length + read > MaxDecodedLength)
                    throw Invalid();
                payload.Write(buffer, 0, read);
            }
            var token = JsonSerializer.Deserialize<SearchToken>(payload.GetBuffer().AsSpan(0, (int)payload.Length), Json);
            if (token is not { SchemaVersion: 1, Offset: >= 0 and < 1000, PreviousKeys: not null } ||
                token.Identity != identity || token.PreviousKeys.Any(key => key is null))
                throw Invalid();
            return token;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw Invalid();
        }
    }

    public static string Encode(SearchIdentity identity, PrefixSearchCursor cursor, IEnumerable<string> keys,
        string? directory = null)
    {
        var value = new SearchToken(1, identity, cursor.ContinuationToken, cursor.Offset, keys.ToArray());
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MaxDecodedLength)
            throw new CliException(new(2, "SEARCH_TOKEN_TOO_LARGE", CliText.Get("SearchTokenTooLarge")));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(payload);
        if (output.Length <= MaxEncodedLength / 4 * 3)
            return Base64Url.EncodeToString(output.GetBuffer().AsSpan(0, (int)output.Length));

        // A complete checkpoint can exceed the Windows command-line limit.
        // Keep the same token format for small searches and spill large ones locally.
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory ?? Checkpoints, id + ".search");
        using var pins = ExactLocalPaths.Pin(path, includeLeaf: false, createDirectories: true);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.WriteTo(file);
        return "local." + id;
    }

    private static Stream OpenCheckpoint(string encoded, string directory)
    {
        if (encoded.StartsWith("local.", StringComparison.Ordinal))
        {
            if (!Guid.TryParseExact(encoded[6..], "N", out var id))
                throw Invalid();
            var path = Path.Combine(directory, id.ToString("N") + ".search");
            using var pins = ExactLocalPaths.Pin(path, includeLeaf: true);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length <= MaxDecodedLength)
                return file;
            file.Dispose();
            throw Invalid();
        }
        if (encoded.Length is 0 or > MaxEncodedLength ||
            encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw Invalid();
        return new MemoryStream(Base64Url.DecodeFromChars(encoded));
    }

    private static CliException Invalid() =>
        new(new(2, "INVALID_SEARCH_TOKEN", CliText.Get("InvalidSearchToken")));
}
