using System.Windows;
using System.Windows.Controls;

namespace Cavdeck.App.Features.Transfers;

public partial class TransfersView : UserControl
{
    public TransfersView() => InitializeComponent();

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The workspace may be narrow when the sidebar is expanded. Keep all
        // controls available without clipping the transfer summary.
        var compact = e.NewSize.Width < 790;
        var row = compact ? 1 : 0;
        if (Grid.GetRow(TransferSummary) == row)
            return;

        Grid.SetRow(TransferSummary, row);
        Grid.SetColumn(TransferSummary, compact ? 0 : 1);
        Grid.SetColumnSpan(TransferSummary, compact ? 3 : 1);
        TransferSummary.Margin = compact ? new Thickness(0, 8, 12, 2) : new Thickness(12, 0, 12, 0);

        Grid.SetColumn(TransferFilters, compact ? 3 : 2);
        TransferFilters.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        TransferFilters.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        Grid.SetRow(TransferConcurrency, row);
    }
}
