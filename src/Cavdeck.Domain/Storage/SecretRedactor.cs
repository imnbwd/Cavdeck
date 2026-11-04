using System.Text.RegularExpressions;

namespace Cavdeck.Domain.Storage;

public static partial class SecretRedactor
{
    public static string? Redact(string? value, IEnumerable<string?>? secrets = null)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var result = value;
        if (secrets is not null)
        {
            foreach (var secret in secrets)
            {
                if (string.IsNullOrEmpty(secret) || secret.Length < 8)
                    continue;
                result = result.Replace(secret, "••••", StringComparison.Ordinal);
            }
        }

        result = SignedQuery().Replace(result, "$1••••");
        return result;
    }

    [GeneratedRegex(
        "(?i)((?:X-Amz-)?Credential=|X-Amz-Signature=|X-Amz-Security-Token=|AWSAccessKeyId=|Signature=)[^&\\s\"'<>]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SignedQuery();
}
