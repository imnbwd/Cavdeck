namespace Cavdeck.Application.Shell;

// Tab list for the shell. The last tab returns to Connections when closed.
public sealed class ShellSession
{
    public const int MaxTabs = 12;

    private readonly List<Tab> _tabs = [];
    private Guid? _previousTabId;

    public int Count => _tabs.Count;
    public int ActiveIndex { get; private set; }
    public ShellPage Active => _tabs[ActiveIndex].Page;

    // Settings should return to the last active work tab on the next launch.
    public int RestorableActiveIndex
    {
        get
        {
            if (Active != ShellPage.Settings)
                return ActiveIndex;
            var previous = _tabs.FindIndex(tab => tab.Id == _previousTabId);
            return previous >= 0 && _tabs[previous].Page != ShellPage.Settings
                ? previous : ActiveIndex;
        }
    }

    public static ShellSession CreateDefault() => From(null, 0);

    public static ShellSession ForStartup(WindowLayout layout, bool restorePreviousTabs) =>
        restorePreviousTabs ? From(layout.Tabs, layout.ActiveTab) : CreateDefault();

    public static ShellSession From(IEnumerable<string>? ids, int activeIndex)
    {
        var session = new ShellSession();
        var restoredActive = -1;
        if (ids != null)
        {
            var savedIndex = 0;
            foreach (var id in ids)
            {
                if (session._tabs.Count >= MaxTabs)
                    break;
                if (ShellPages.TryParse(id, out var page))
                {
                    var existing = page is ShellPage.Connections or ShellPage.Explorer
                        ? -1 : session._tabs.FindIndex(tab => tab.Page == page);
                    if (existing < 0)
                    {
                        session._tabs.Add(new Tab(page));
                        existing = session._tabs.Count - 1;
                    }
                    if (savedIndex == activeIndex)
                        restoredActive = existing;
                }
                savedIndex++;
            }
        }

        if (session._tabs.Count == 0)
            session._tabs.Add(new Tab(ShellPage.Connections));

        session.ActiveIndex = restoredActive >= 0
            ? restoredActive : Math.Clamp(activeIndex, 0, session._tabs.Count - 1);
        return session;
    }

    public ShellPage PageAt(int index) => _tabs[index].Page;

    public Guid IdAt(int index) => _tabs[index].Id;

    public IReadOnlyList<string> TabIds => _tabs.Select(tab => ShellPages.ToId(tab.Page)).ToArray();

    public bool Navigate(ShellPage page)
    {
        if (_tabs[ActiveIndex].Page == page)
            return false;
        _tabs[ActiveIndex].Page = page;
        return true;
    }

    public bool TryOpen(ShellPage page)
    {
        if (_tabs.Count >= MaxTabs)
            return false;
        _tabs.Add(new Tab(page));
        ActiveIndex = _tabs.Count - 1;
        return true;
    }

    public bool ActivateOrOpen(ShellPage page)
    {
        var existing = _tabs.FindIndex(tab => tab.Page == page);
        if (existing >= 0)
            return Activate(existing);
        if (_tabs.Count >= MaxTabs)
            return false;
        RememberActive();
        return TryOpen(page);
    }

    public bool CloseAt(int index)
    {
        if (index < 0 || index >= _tabs.Count)
            return false;

        if (_tabs.Count == 1)
            return Navigate(ShellPage.Connections);

        var closingActive = index == ActiveIndex;
        var closingSettings = closingActive && _tabs[index].Page == ShellPage.Settings;
        _tabs.RemoveAt(index);
        if (closingActive)
        {
            var previous = closingSettings ? _tabs.FindIndex(tab => tab.Id == _previousTabId) : -1;
            ActiveIndex = previous >= 0 ? previous : Math.Min(index, _tabs.Count - 1);
        }
        else if (index < ActiveIndex)
            ActiveIndex--;
        if (closingSettings)
            _previousTabId = null;
        return true;
    }

    public bool Activate(int index)
    {
        if (index < 0 || index >= _tabs.Count || index == ActiveIndex)
            return false;
        RememberActive();
        ActiveIndex = index;
        return true;
    }

    public bool Cycle(int delta)
    {
        if (_tabs.Count <= 1 || delta == 0)
            return false;
        RememberActive();
        var count = _tabs.Count;
        ActiveIndex = ((ActiveIndex + delta) % count + count) % count;
        return true;
    }

    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count)
            return false;
        to = Math.Clamp(to, 0, _tabs.Count - 1);
        if (from == to)
            return false;

        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);

        if (ActiveIndex == from)
            ActiveIndex = to;
        else if (from < ActiveIndex && to >= ActiveIndex)
            ActiveIndex--;
        else if (from > ActiveIndex && to <= ActiveIndex)
            ActiveIndex++;
        return true;
    }

    private sealed class Tab
    {
        public Tab(ShellPage page) => Page = page;

        public Guid Id { get; } = Guid.NewGuid();
        public ShellPage Page { get; set; }
    }

    private void RememberActive()
    {
        if (Active != ShellPage.Settings)
            _previousTabId = _tabs[ActiveIndex].Id;
    }
}
