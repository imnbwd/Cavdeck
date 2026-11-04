namespace Cavdeck.Application.Shell;

public sealed class ExplorerActivationLog
{
    private readonly Dictionary<Guid, int> _activatedAt = [];
    private readonly Dictionary<Guid, Guid> _remembered = [];
    private int _tick;

    public IReadOnlyDictionary<Guid, int> ActivatedAt => _activatedAt;

    public Guid? RememberedTab(Guid connectionId) =>
        _remembered.TryGetValue(connectionId, out var tabId) ? tabId : null;

    public void NoteActive(Guid tabId, Guid? connectionId)
    {
        _tick++;
        _activatedAt[tabId] = _tick;
        if (connectionId is Guid id)
            _remembered[id] = tabId;
    }

    public void Forget(Guid tabId)
    {
        _activatedAt.Remove(tabId);
        foreach (var pair in _remembered.Where(pair => pair.Value == tabId).ToArray())
            _remembered.Remove(pair.Key);
    }
}
