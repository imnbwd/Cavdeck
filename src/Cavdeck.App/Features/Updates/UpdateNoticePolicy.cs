using Cavdeck.Application.Abstractions;

namespace Cavdeck.App.Features.Updates;

public enum UpdateNoticeKind { Available, Ready }

// One notice per version and stage per application session. Status progress events
// and repeated checks never create a notification storm.
public sealed class UpdateNoticePolicy
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

    public UpdateNoticeKind? Take(UpdateStatus status, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var kind = status switch
        {
            UpdateStatus.Available => UpdateNoticeKind.Available,
            UpdateStatus.Downloaded => UpdateNoticeKind.Ready,
            _ => (UpdateNoticeKind?)null
        };
        return kind is not null && _shown.Add($"{kind}:{version}") ? kind : null;
    }
}
