namespace Cavdeck.Infrastructure.S3;

// ToString omits keys. A record would include them.
internal sealed class S3Session
{
    public Guid ConnectionId { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    public required string Region { get; init; }
    public string? ServiceUrl { get; init; }
    public bool ForcePathStyle { get; init; }

    public override string ToString() =>
        $"S3Session region={Region} url={ServiceUrl ?? "aws"} pathStyle={ForcePathStyle}";
}
