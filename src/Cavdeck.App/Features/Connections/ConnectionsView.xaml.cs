using System.Windows.Controls;

namespace Cavdeck.App.Features.Connections;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView()
    {
        InitializeComponent();
    }

    private void ConnectionMore_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
