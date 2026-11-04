using System.ComponentModel;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.Application.Shell;

namespace Cavdeck.App.ViewModels;

public sealed class ShellTabViewModel : ViewModelBase, IDisposable
{
    private readonly LocalizationService _texts;
    private ShellPage _page;
    private int _index;
    private bool _isActive;
    private bool _isOnlyTab;
    private ConnectionWorkspace? _connections;
    private ExplorerWorkspace? _explorer;

    public ShellTabViewModel(LocalizationService texts, Guid id)
    {
        _texts = texts;
        Id = id;
    }

    public event EventHandler<PropertyChangedEventArgs>? ActivityChanged;

    public Guid Id { get; }
    public ShellPage Page => _page;
    public int Index => _index;
    public bool IsActive => _isActive;
    public bool CanClose => !_isOnlyTab || _page != ShellPage.Connections
        || (_connections?.Editor.IsOpen ?? false);
    public bool HasConnections => _connections is not null;
    public bool HasExplorer => _explorer is not null;
    public bool HasUnsavedChanges => (_connections?.Editor.HasUnsavedChanges ?? false)
        || (_explorer?.Explorer.HasUnsavedChanges ?? false);

    public object? Workspace => _page switch
    {
        ShellPage.Connections => _connections,
        ShellPage.Explorer => _explorer,
        _ => null
    };

    public string Title
    {
        get
        {
            if (_page == ShellPage.Connections && _connections?.Editor.IsOpen == true)
                return _connections.Editor.Title;
            if (_page == ShellPage.Explorer && _explorer is { } explorer)
            {
                var place = explorer.Explorer.Place;
                if (place is { Bucket: not null } && place.Value.Prefix.Length > 0)
                {
                    var leaf = place.Value.Prefix.TrimEnd('/').Split('/').LastOrDefault();
                    if (!string.IsNullOrEmpty(leaf))
                        return leaf;
                }
                if (explorer.Explorer.Heading.Length > 0)
                    return explorer.Explorer.Heading;
            }
            return _texts[TitleKey(_page)];
        }
    }

    public string FullTitle => _page == ShellPage.Explorer && _explorer is { } explorer
        && explorer.Explorer.PathText.Length > 0 ? explorer.Explorer.PathText : Title;
    public string DuplicateLabel => _texts["Tab_Duplicate"];
    public string CloseOthersLabel => _texts["Tab_CloseOthers"];
    public string RestoreClosedLabel => _texts["Tab_RestoreClosed"];

    public void Update(ShellPage page, int index, bool isActive, bool isOnlyTab = false)
    {
        _isOnlyTab = isOnlyTab;
        var pageChanged = _page != page;
        if (pageChanged)
        {
            _page = page;
            OnPropertyChanged(nameof(Page));
            OnPropertyChanged(nameof(Workspace));
        }

        if (_index != index)
        {
            _index = index;
            OnPropertyChanged(nameof(Index));
        }

        if (_isActive != isActive)
        {
            _isActive = isActive;
            OnPropertyChanged(nameof(IsActive));
        }

        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FullTitle));
        OnPropertyChanged(nameof(DuplicateLabel));
        OnPropertyChanged(nameof(CloseOthersLabel));
        OnPropertyChanged(nameof(RestoreClosedLabel));
    }

    public void AttachConnections(ConnectionWorkspace workspace)
    {
        _connections = workspace;
        workspace.ChromeChanged += OnConnectionChrome;
        workspace.Editor.PropertyChanged += OnEditorPropertyChanged;
        if (_page == ShellPage.Connections)
            OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(CanClose));
    }

    public void AttachExplorer(ExplorerWorkspace workspace)
    {
        _explorer = workspace;
        workspace.Explorer.PropertyChanged += OnExplorerChanged;
        workspace.Explorer.Preview.PropertyChanged += OnPreviewPropertyChanged;
        if (_page == ShellPage.Explorer)
            OnPropertyChanged(nameof(Workspace));
    }

    public void RefreshTitle()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FullTitle));
        OnPropertyChanged(nameof(DuplicateLabel));
        OnPropertyChanged(nameof(CloseOthersLabel));
        OnPropertyChanged(nameof(RestoreClosedLabel));
    }

    public void CloseConnectionEditor()
    {
        if (_connections?.Editor.IsOpen == true)
            _connections.Editor.Close();
    }

    public void Dispose() => ResetWorkspaces();

    public void ResetWorkspaces()
    {
        if (_connections is not null)
        {
            _connections.ChromeChanged -= OnConnectionChrome;
            _connections.Editor.PropertyChanged -= OnEditorPropertyChanged;
            _connections.Dispose();
            _connections = null;
        }

        if (_explorer is not null)
        {
            _explorer.Explorer.PropertyChanged -= OnExplorerChanged;
            _explorer.Explorer.Preview.PropertyChanged -= OnPreviewPropertyChanged;
            _explorer.Explorer.Dispose();
            _explorer = null;
        }

        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanClose));
    }

    public static string TitleKey(ShellPage page) => page switch
    {
        ShellPage.Favorites => "Nav_Favorites",
        ShellPage.Recent => "Nav_Recent",
        ShellPage.Transfers => "Nav_Transfers",
        ShellPage.Explorer => "Nav_Explorer",
        ShellPage.Settings => "Settings_Title",
        _ => "Nav_Connections"
    };

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionEditorViewModel.HasUnsavedChanges))
            OnPropertyChanged(nameof(HasUnsavedChanges));
        if (e.PropertyName == nameof(ConnectionEditorViewModel.IsOpen))
            OnPropertyChanged(nameof(CanClose));
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Cavdeck.App.Features.Preview.PreviewPaneViewModel.IsDirty))
            OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void OnConnectionChrome(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanClose));
        ActivityChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionEditorViewModel.IsOpen)));
    }

    private void OnExplorerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExplorerViewModel.Heading) or nameof(ExplorerViewModel.PathText))
            OnPropertyChanged(nameof(Title));
        if (e.PropertyName is nameof(ExplorerViewModel.PathText) or nameof(ExplorerViewModel.Heading))
            OnPropertyChanged(nameof(FullTitle));
        if (e.PropertyName is nameof(ExplorerViewModel.Heading) or nameof(ExplorerViewModel.StatusText) or nameof(ExplorerViewModel.PathText))
            ActivityChanged?.Invoke(this, e);
    }
}
