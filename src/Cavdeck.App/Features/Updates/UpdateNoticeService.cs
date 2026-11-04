using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;

namespace Cavdeck.App.Features.Updates;

// Observes update state after the shell and tray have been initialized.
// Notification actions always navigate to About; installation remains opt-in.
public sealed class UpdateNoticeService : IDisposable
{
    private readonly IUpdateCoordinator _updates;
    private readonly ToastService _toasts;
    private readonly TrayIconService _tray;
    private readonly LocalizationService _texts;
    private readonly UpdateNoticePolicy _policy = new();
    private bool _started;

    public UpdateNoticeService(IUpdateCoordinator updates, ToastService toasts,
        TrayIconService tray, LocalizationService texts)
    {
        _updates = updates;
        _toasts = toasts;
        _tray = tray;
        _texts = texts;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _updates.Changed += OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        var kind = _policy.Take(_updates.Status, _updates.Version);
        if (kind is null || _tray.IsViewingUpdateDetails)
            return;

        var ready = kind == UpdateNoticeKind.Ready;
        var title = _texts[ready ? "Update_NoticeReadyTitle" : "Update_NoticeAvailableTitle"];
        var message = string.Format(_texts[ready ? "Update_Ready" : "Update_Available"],
            _updates.Version);

        if (!_tray.IsMainWindowVisible)
        {
            _tray.NotifyUpdate(title, message);
            return;
        }

        _toasts.Show(new ToastRequest
        {
            Title = title,
            Message = message,
            Duration = TimeSpan.FromSeconds(10),
            ActionLabelKey = "Update_ViewDetails",
            Action = _tray.OpenUpdateDetails
        });
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _updates.Changed -= OnChanged;
    }
}
