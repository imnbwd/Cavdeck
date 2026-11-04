using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Connections;

public sealed class ConnectionValidationException : Exception
{
    public ConnectionValidationException(IReadOnlyList<ConnectionIssue> issues)
        : base("Connection details are incomplete.")
    {
        Issues = issues;
    }

    public IReadOnlyList<ConnectionIssue> Issues { get; }
}

public sealed record ConnectionAttempt(
    bool Succeeded,
    IReadOnlyList<ConnectionIssue> Issues,
    StorageProbeResult? Probe)
{
    public static ConnectionAttempt Invalid(IReadOnlyList<ConnectionIssue> issues) =>
        new(false, issues, null);

    public static ConnectionAttempt FromProbe(StorageProbeResult probe) =>
        new(probe.Succeeded, [], probe);
}
