namespace Cavdeck.Application.Shell;

public enum ConnectionOpenKind { Activate, Create, Rejected }

public readonly record struct OpenExplorer(Guid TabId, Guid? ConnectionId);

public readonly record struct ConnectionOpenDecision(ConnectionOpenKind Kind, Guid? TabId);

public static class ConnectionOpener
{
    public static ConnectionOpenDecision Choose(
        Guid connectionId,
        IReadOnlyList<OpenExplorer> explorers,
        Guid? rememberedTabId,
        IReadOnlyDictionary<Guid, int> activatedAt,
        int openTabCount,
        int maxTabs,
        bool forceNew)
    {
        ArgumentNullException.ThrowIfNull(explorers);
        ArgumentNullException.ThrowIfNull(activatedAt);

        if (forceNew)
            return CreateOrReject(openTabCount, maxTabs);

        if (rememberedTabId is Guid remembered
            && explorers.Any(tab => tab.TabId == remembered && tab.ConnectionId == connectionId))
            return new ConnectionOpenDecision(ConnectionOpenKind.Activate, remembered);

        OpenExplorer? best = null;
        var bestOrder = int.MinValue;
        foreach (var tab in explorers)
        {
            if (tab.ConnectionId != connectionId)
                continue;
            var order = activatedAt.TryGetValue(tab.TabId, out var at) ? at : int.MinValue;
            if (best is null || order > bestOrder)
            {
                best = tab;
                bestOrder = order;
            }
        }

        if (best is { } chosen)
            return new ConnectionOpenDecision(ConnectionOpenKind.Activate, chosen.TabId);

        return CreateOrReject(openTabCount, maxTabs);
    }

    private static ConnectionOpenDecision CreateOrReject(int openTabCount, int maxTabs) =>
        openTabCount >= maxTabs
            ? new ConnectionOpenDecision(ConnectionOpenKind.Rejected, null)
            : new ConnectionOpenDecision(ConnectionOpenKind.Create, null);
}
