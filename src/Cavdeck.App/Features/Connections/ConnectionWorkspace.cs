using System.Windows.Input;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Connections;

// One connection tab. The catalog is shared; the editor draft belongs to this tab.
public sealed class ConnectionWorkspace : ViewModelBase, IDisposable
{
    private readonly ToastService _toasts;
    private readonly LocalizationService _texts;

    public ConnectionWorkspace(
        ConnectionsViewModel connections,
        ConnectionEditorViewModel editor,
        LocalizationService texts,
        ToastService toasts)
    {
        Connections = connections;
        Editor = editor;
        _texts = texts;
        _toasts = toasts;
        AddCommand = new RelayCommand(_ => Editor.BeginCreate(), _ => Connections.IsIdle && Editor.IsIdle);
        EditCommand = new AsyncRelayCommand(EditAsync);
        editor.Saved += OnSaved;
        editor.PropertyChanged += OnEditorChanged;
        connections.PropertyChanged += OnCatalogChanged;
        texts.PropertyChanged += OnTextsChanged;
    }

    public event EventHandler? ChromeChanged;

    public ConnectionsViewModel Connections { get; }
    public ConnectionEditorViewModel Editor { get; }
    public LocalizationService Texts => _texts;
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }

    public bool ShowList => !Editor.IsOpen;
    public bool ShowEmpty => ShowList && !Connections.IsLoading && !Connections.HasConnections && !Connections.HasLoadError;
    public string ListSummary => string.Format(_texts["Connection_ListSummary"], Connections.Count);
    public bool ShowLoading => ShowList && Connections.IsLoading && !Connections.HasConnections;
    public bool ShowRows => ShowList && Connections.HasConnections;

    public void Dispose()
    {
        Editor.Saved -= OnSaved;
        Editor.PropertyChanged -= OnEditorChanged;
        Connections.PropertyChanged -= OnCatalogChanged;
        _texts.PropertyChanged -= OnTextsChanged;
        ChromeChanged = null;
        Editor.Close();
    }

    private void OnTextsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Editor.RefreshTexts();
        OnPropertyChanged(nameof(ListSummary));
    }

    private async Task EditAsync(object? parameter)
    {
        if (parameter is not ConnectionItemViewModel item)
            return;
        await Connections.EditIntoAsync(item, Editor);
    }

    private void OnSaved(object? sender, StorageConnection connection)
    {
        try
        {
            Connections.PublishSaved(Editor.SavedChange
                ?? new Cavdeck.Application.Connections.ConnectionChange(null, connection));
            Connections.Select(connection.Id);
            Editor.Close();
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Connection_SavedTitle"],
                Message = string.Format(_texts["Connection_SavedMessage"], connection.Name),
                Tone = ToastTone.Success
            });
        }
        catch (Exception)
        {
            Connections.ReportLoadFailure();
        }
    }

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionEditorViewModel.IsIdle) && AddCommand is RelayCommand add)
            add.RaiseCanExecuteChanged();
        if (e.PropertyName != nameof(ConnectionEditorViewModel.IsOpen))
            return;
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowRows));
        ChromeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCatalogChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(ConnectionsViewModel.HasConnections)
            or nameof(ConnectionsViewModel.HasLoadError)
            or nameof(ConnectionsViewModel.Count)
            or nameof(ConnectionsViewModel.IsIdle)
            or nameof(ConnectionsViewModel.IsBusy)
            or nameof(ConnectionsViewModel.IsLoading)))
            return;
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowRows));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ListSummary));
        if (AddCommand is RelayCommand add)
            add.RaiseCanExecuteChanged();
    }
}
