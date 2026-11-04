using System.Windows.Input;
using Cavdeck.App.Localization;

namespace Cavdeck.App.Features.Explorer;

// One explorer tab. Listing, selection, and location stay on this instance.
public sealed class ExplorerWorkspace
{
    public ExplorerWorkspace(ExplorerViewModel explorer, LocalizationService texts, ICommand showConnections)
    {
        Explorer = explorer;
        Texts = texts;
        ShowConnectionsCommand = showConnections;
    }

    public ExplorerViewModel Explorer { get; }
    public LocalizationService Texts { get; }
    public ICommand ShowConnectionsCommand { get; }
}
