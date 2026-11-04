using System.Windows;
using Cavdeck.App.Localization;

namespace Cavdeck.App.Controls;

public sealed class DialogService
{
    private readonly LocalizationService _texts;

    public DialogService(LocalizationService texts) => _texts = texts;

    public bool? Show(Window? owner, DialogRequest request) =>
        ShowChoice(owner, request) == DialogChoice.Primary ? true : false;

    public DialogChoice ShowChoice(Window? owner, DialogRequest request, Func<bool>? confirm = null)
    {
        var closeText = string.IsNullOrWhiteSpace(request.CloseText)
            ? _texts["Dialog_Close"]
            : request.CloseText;
        var dialog = new AppDialog(request with { CloseText = closeText })
        {
            ConfirmPrimary = confirm
        };
        var ownerWindow = owner
            ?? System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current?.MainWindow;
        if (ownerWindow is { IsLoaded: true })
            dialog.Owner = ownerWindow;
        var screens = MonitorWorkAreas.Capture();
        if (screens.Count > 0)
        {
            var screen = screens.FirstOrDefault(area => area.IsPrimary, screens[0]);
            if (ownerWindow is not null && WindowScreenCoordinates.TryRead(ownerWindow, out var bounds))
            {
                screen = screens.FirstOrDefault(area =>
                    bounds.Left >= area.Left && bounds.Left < area.Left + area.Width &&
                    bounds.Top >= area.Top && bounds.Top < area.Top + area.Height, screen);
            }
            dialog.FitToWorkArea(screen.Width / screen.DpiScale, screen.Height / screen.DpiScale);
        }
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
