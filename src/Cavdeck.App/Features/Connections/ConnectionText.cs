using Cavdeck.App.Localization;
using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Connections;

internal static class ConnectionText
{
    public static string ProviderName(LocalizationService texts, StorageProviderKind kind) => kind switch
    {
        StorageProviderKind.AwsS3 => texts["Provider_Aws"],
        StorageProviderKind.Minio => texts["Provider_Minio"],
        StorageProviderKind.S3Compatible => texts["Provider_Compatible"],
        _ => texts["Provider_R2"]
    };

    public static string Issue(LocalizationService texts, ConnectionIssue issue) => (issue.Field, issue.Code) switch
    {
        (ConnectionField.Name, ConnectionIssueCode.Required) => texts["Connection_Issue_NameRequired"],
        (ConnectionField.Name, ConnectionIssueCode.Duplicate) => texts["Connection_Issue_NameDuplicate"],
        (ConnectionField.Name, _) => texts["Connection_Issue_NameInvalid"],
        (ConnectionField.AccountId, ConnectionIssueCode.Required) => texts["Connection_Issue_AccountRequired"],
        (ConnectionField.AccountId, _) => texts["Connection_Issue_AccountInvalid"],
        (ConnectionField.Region, ConnectionIssueCode.Required) => texts["Connection_Issue_RegionRequired"],
        (ConnectionField.Region, _) => texts["Connection_Issue_RegionInvalid"],
        (ConnectionField.Endpoint, ConnectionIssueCode.Required) => texts["Connection_Issue_EndpointRequired"],
        (ConnectionField.Endpoint, ConnectionIssueCode.UserInfo) => texts["Connection_Issue_EndpointUserInfo"],
        (ConnectionField.Endpoint, _) => texts["Connection_Issue_EndpointInvalid"],
        (ConnectionField.AccessKeyId, ConnectionIssueCode.Required) => texts["Connection_Issue_AccessKeyRequired"],
        (ConnectionField.AccessKeyId, _) => texts["Connection_Issue_AccessKeyInvalid"],
        (ConnectionField.Secret, _) => texts["Connection_Issue_SecretRequired"],
        (ConnectionField.Bucket, _) => texts["Connection_Issue_BucketInvalid"],
        (ConnectionField.Prefix, _) => texts["Connection_Issue_PrefixInvalid"],
        _ => texts["Connection_Issue_NameInvalid"]
    };

    public static string Level(StorageProbeResult result)
    {
        if (result.Succeeded)
            return "Success";
        return result.Failure?.Kind is StorageFailureKind.AccessDenied or StorageFailureKind.BucketNotFound
            ? "Warning"
            : "Danger";
    }

    public static string Title(LocalizationService texts, StorageProbeResult result, string? prefix)
    {
        if (result.Succeeded)
        {
            if (!string.IsNullOrEmpty(result.ConfirmedBucket) && !string.IsNullOrEmpty(prefix))
                return string.Format(texts["Connection_TestSuccessBucketPrefix"], result.ConfirmedBucket);
            if (!string.IsNullOrEmpty(result.ConfirmedBucket))
                return string.Format(texts["Connection_TestSuccessBucket"], result.ConfirmedBucket);
            return result.BucketCount switch
            {
                0 => texts["Connection_TestSuccessEmpty"],
                1 => texts["Connection_TestSuccessBucketOne"],
                _ => string.Format(texts["Connection_TestSuccessBuckets"], result.BucketCount ?? 0)
            };
        }

        return texts[FailureKey(result.Failure?.Kind)];
    }

    public static string Suggestion(LocalizationService texts, StorageProbeResult result) =>
        result.Succeeded ? "" : texts[FailureKey(result.Failure?.Kind) + "Hint"];

    // Render provider prose only inside an explicitly technical panel. Ordinary
    // error toasts retain stable identifiers but never surface server prose.
    public static string Technical(LocalizationService texts, StorageFailure? failure,
        bool includeProviderDetail = false)
    {
        if (failure is null)
            return "";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(failure.ErrorCode))
            parts.Add(string.Format(texts["Connection_TechnicalCode"], failure.ErrorCode));
        if (failure.StatusCode is int status)
            parts.Add(string.Format(texts["Connection_TechnicalHttp"], status));
        if (!string.IsNullOrEmpty(failure.RequestId))
            parts.Add(string.Format(texts["Connection_TechnicalRequest"], failure.RequestId));
        if (includeProviderDetail && !string.IsNullOrWhiteSpace(failure.Detail))
            parts.Add(failure.Detail);
        return string.Join(" · ", parts);
    }

    private static string FailureKey(StorageFailureKind? kind) => kind switch
    {
        StorageFailureKind.InvalidCredentials => "Connection_Failure_InvalidCredentials",
        StorageFailureKind.AccessDenied => "Connection_Failure_AccessDenied",
        StorageFailureKind.Endpoint => "Connection_Failure_Endpoint",
        StorageFailureKind.BucketNotFound => "Connection_Failure_BucketNotFound",
        StorageFailureKind.Network => "Connection_Failure_Network",
        StorageFailureKind.CredentialMissing => "Connection_Failure_CredentialMissing",
        StorageFailureKind.InvalidConfiguration => "Connection_Failure_InvalidConfiguration",
        StorageFailureKind.Unsupported => "Connection_Failure_Unsupported",
        StorageFailureKind.ReadOnly => "Connection_Failure_ReadOnly",
        StorageFailureKind.Cancelled => "Connection_Failure_Cancelled",
        _ => "Connection_Failure_Unknown"
    };
}
