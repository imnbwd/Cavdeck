using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Abstractions;

namespace Cavdeck.App.Services;

public sealed class TrayMenuViewModel : INotifyPropertyChanged
{
    private readonly LocalizationService _texts;
    private TransferTraySummary _summary;
    private UpdateStatus _updateStatus;
    private string? _updateVersion;

    public TrayMenuViewModel(LocalizationService texts, Action open, Action transfers,
        Action connections, Action favorites, Action settings, Action exit, Action? updates = null)
    {
        _texts = texts;
        OpenCommand = new RelayCommand(_ => open());
        TransfersCommand = new RelayCommand(_ => transfers());
        ConnectionsCommand = new RelayCommand(_ => connections());
        FavoritesCommand = new RelayCommand(_ => favorites());
        SettingsCommand = new RelayCommand(_ => settings());
        ExitCommand = new RelayCommand(_ => exit());
        UpdatesCommand = new RelayCommand(_ => updates?.Invoke());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand OpenCommand { get; }
    public ICommand TransfersCommand { get; }
    public ICommand ConnectionsCommand { get; }
    public ICommand FavoritesCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand UpdatesCommand { get; }
    public bool HasUpdate => _updateStatus is UpdateStatus.Available or UpdateStatus.Downloaded;
    public string UpdateText => string.Format(_texts[_updateStatus == UpdateStatus.Downloaded
        ? "Update_TrayReady" : "Update_TrayAvailable"], _updateVersion ?? "");

    public void SetUpdate(UpdateStatus status, string? version)
    {
        if (_updateStatus == status && _updateVersion == version) return;
        _updateStatus = status;
        _updateVersion = version;
        OnChanged(nameof(HasUpdate));
        OnChanged(nameof(UpdateText));
    }
    public string BrandName => _texts["Brand_Name"];
    public string OpenText => _texts["Tray_Open"];
    public string TransfersText => _texts["Tray_Transfers"];
    public string ConnectionsText => _texts["Tray_Connections"];
    public string FavoritesText => _texts["Tray_Favorites"];
    public string SettingsText => _texts["Tray_Settings"];
    public string ExitText => _texts["Tray_ExitApplication"];
    public string Status => _summary == default
        ? _texts["Tray_StatusIdle"]
        : string.Format(_texts["Tray_Status"], _summary.Running, _summary.Waiting,
            _summary.Paused, _summary.Failed);
    public int TransferCount => _summary.Unfinished;
    public bool HasTransfers => TransferCount > 0;
    public bool HasFailures => _summary.Failed > 0;
    public string Tooltip
    {
        get
        {
            var value = _summary.Failed > 0
                ? string.Format(_texts["Tray_TooltipFailed"], _summary.Failed)
                : _summary.Running + _summary.Waiting > 0
                    ? string.Format(_texts["Tray_TooltipBusy"], _summary.Running, _summary.Waiting)
                    : _texts["Tray_TooltipIdle"];
            return value.Length <= 63 ? value : value[..63];
        }
    }

    public void Refresh(TransferTraySummary summary)
    {
        _summary = summary;
        OnChanged(nameof(BrandName));
        OnChanged(nameof(OpenText));
        OnChanged(nameof(TransfersText));
        OnChanged(nameof(ConnectionsText));
        OnChanged(nameof(FavoritesText));
        OnChanged(nameof(SettingsText));
        OnChanged(nameof(ExitText));
        OnChanged(nameof(UpdateText));
        OnChanged(nameof(Status));
        OnChanged(nameof(TransferCount));
        OnChanged(nameof(HasTransfers));
        OnChanged(nameof(HasFailures));
        OnChanged(nameof(Tooltip));
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
