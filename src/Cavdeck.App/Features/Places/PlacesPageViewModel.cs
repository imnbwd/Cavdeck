using System.Collections.ObjectModel;
using System.Windows.Input;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Places;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Places;

public sealed class PlaceRow
{
    public PlaceRow(SavedPlace place, StorageConnection connection, bool recent, LocalizationService texts)
    {
        Place = place;
        Connection = connection;
        IsConnection = place.Bucket is null;
        IsBucket = place.Bucket is not null && place.Prefix.Length == 0;
        Title = IsConnection ? connection.Name : IsBucket ? place.Bucket! :
            place.Prefix.TrimEnd('/').Split('/').Last();
        ConnectionName = connection.Name;
        Location = place.Bucket is null ? texts["Places_AllBuckets"] :
            $"s3://{place.Bucket}/{place.Prefix}";
        VisitedText = recent ? $"{place.VisitedAt.ToLocalTime():g}" : "";
        Description = recent
            ? $"{VisitedText} · {ConnectionName} · {Location}"
            : $"{ConnectionName} · {Location}";
    }

    public SavedPlace Place { get; }
    public StorageConnection Connection { get; }
    public string Title { get; }
    public string ConnectionName { get; }
    public string Location { get; }
    public string VisitedText { get; }
    public DateTimeOffset VisitedAt => Place.VisitedAt;
    public string Description { get; }
    public bool IsConnection { get; }
    public bool IsBucket { get; }
}

public sealed class PlacesPageViewModel : ViewModelBase, IDisposable
{
    private readonly PlaceLibrary _library;
    private readonly Func<IReadOnlyList<StorageConnection>> _connections;
    private readonly Action<SavedPlace, bool> _open;
    private readonly Action _browse;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly LocalizationService _texts;
    private PlaceRow? _selected;

    public PlacesPageViewModel(bool favorites, PlaceLibrary library,
        Func<IReadOnlyList<StorageConnection>> connections,
        Action<SavedPlace, bool> open, Action browse,
        DialogService dialogs, ToastService toasts,
        LocalizationService texts)
    {
        IsFavorites = favorites;
        _library = library;
        _connections = connections;
        _open = open;
        _browse = browse;
        _dialogs = dialogs;
        _toasts = toasts;
        _texts = texts;
        OpenCommand = new RelayCommand(item => Open(item, false));
        OpenNewTabCommand = new RelayCommand(item => Open(item, true));
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, ReportUnexpectedUiError);
        ClearCommand = new AsyncRelayCommand(_ => ClearAsync(), ReportUnexpectedUiError);
        BrowseCommand = new RelayCommand(_ => _browse());
        _library.Changed += OnChanged;
        _texts.PropertyChanged += OnLanguageChanged;
        Refresh();
    }

    private void ReportUnexpectedUiError(Exception exception)
    {
        var title = _texts["Ui_ActionFailed"];
        if (_toasts.Items.Any(item => !item.IsClosing && item.Title == title))
            return;
        _toasts.Show(new ToastRequest { Title = title, Tone = ToastTone.Error });
    }

    public bool IsFavorites { get; }
    public LocalizationService Texts => _texts;
    public ObservableCollection<PlaceRow> Items { get; } = [];
    public PlaceRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }
    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Items.Count == 0;
    public bool HasItems => Items.Count > 0;
    public bool ShowClear => !IsFavorites && HasItems;
    public string Hint => _texts[IsFavorites ? "Places_FavoritesHint" : "Places_RecentHint"];
    public string ClearLabel => _texts["Places_ClearRecent"];
    public string OpenLabel => _texts["Explorer_Open"];
    public string OpenNewTabLabel => _texts["Places_OpenNewTab"];
    public string BrowseLabel => _texts["Places_Browse"];
    public string NameHeader => _texts["Explorer_Name"];
    public string ConnectionHeader => _texts["Places_Connection"];
    public string LocationHeader => _texts["Places_Location"];
    public string VisitedHeader => _texts["Places_Visited"];
    public string EmptyTitle => _texts[IsFavorites ? "Empty_FavoritesTitle" : "Empty_RecentTitle"];
    public string EmptyDescription => _texts[IsFavorites ? "Empty_FavoritesDescription" : "Empty_RecentDescription"];
    public string RemoveLabel => _texts[IsFavorites ? "Places_RemoveFavorite" : "Places_RemoveRecent"];
    public ICommand OpenCommand { get; }
    public ICommand OpenNewTabCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand BrowseCommand { get; }

    public void Refresh()
    {
        var connections = _connections().ToDictionary(item => item.Id);
        var source = IsFavorites ? _library.Favorites : _library.Recent;
        Selected = null;
        Items.Clear();
        foreach (var place in source)
            if (connections.TryGetValue(place.ConnectionId, out var connection))
                Items.Add(new PlaceRow(place, connection, !IsFavorites, _texts));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowClear));
    }

    private void Open(object? parameter, bool newTab)
    {
        if (parameter is PlaceRow row)
            _open(row.Place, newTab);
    }

    private async Task RemoveAsync(object? parameter)
    {
        if (parameter is not PlaceRow row)
            return;
        try
        {
            if (IsFavorites)
                await _library.RemoveFavoriteAsync(row.Place);
            else
                await _library.RemoveRecentAsync(row.Place);
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Places_SaveFailed"], Tone = ToastTone.Error });
        }
    }

    private async Task ClearAsync()
    {
        if (IsFavorites || Items.Count == 0)
            return;
        if (_dialogs.Show(null, new DialogRequest
        {
            Title = _texts["Places_ClearRecent"],
            Message = _texts["Places_ClearRecentConfirm"],
            PrimaryText = _texts["Places_ClearRecent"],
            SecondaryText = _texts["Dialog_Cancel"],
            Tone = DialogTone.Danger
        }) != true)
            return;
        try { await _library.ClearRecentAsync(); }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Places_SaveFailed"], Tone = ToastTone.Error });
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Refresh();
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(RemoveLabel));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(ClearLabel));
        OnPropertyChanged(nameof(OpenLabel));
        OnPropertyChanged(nameof(OpenNewTabLabel));
        OnPropertyChanged(nameof(BrowseLabel));
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(ConnectionHeader));
        OnPropertyChanged(nameof(LocationHeader));
        OnPropertyChanged(nameof(VisitedHeader));
    }

    public void Dispose()
    {
        _library.Changed -= OnChanged;
        _texts.PropertyChanged -= OnLanguageChanged;
    }
}
