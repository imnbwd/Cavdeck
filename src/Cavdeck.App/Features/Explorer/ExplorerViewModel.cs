using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.App.Features.Preview;
using Cavdeck.Application.Preview;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Places;
using Cavdeck.Application.Sharing;
using Cavdeck.App.Features.Sharing;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.App.Features.Explorer;

public sealed partial class ExplorerViewModel : ViewModelBase, IDisposable
{
    public event EventHandler? SelectionSyncRequested;
    public event EventHandler? OpenFailedTransfersRequested;
    public event EventHandler<ExplorerPlace>? OpenNewTabRequested;
    private readonly ExplorerService _explorer;
    private readonly TransferEngine _transfers;
    private readonly ITransferConnections _transferConnections;
    private readonly LocalizationService _texts;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ApplicationState _state;
    private readonly ISettingsStore _settings;
    private readonly PlaceLibrary _places;
    private readonly IDownloadLinkSigner _linkSigner;
    private readonly IObjectStorageProviderFactory _storage;
    private readonly ExplorerClipboard _clipboard;
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _refreshDelay;
    private readonly HashSet<Guid> _selectOnCompletion = [];
    private readonly HashSet<Guid> _copyBatchesAwaitingResult = [];
    private readonly HashSet<string> _pendingTransferKeys = new(StringComparer.Ordinal);
    private readonly List<ObjectEntry> _loaded = [];
    private readonly List<StorageBucket> _buckets = [];
    private readonly List<ExplorerRow> _selected = [];
    private CancellationTokenSource _browse = new();
    private CancellationTokenSource? _mutation;
    private CopyProgress? _copyProgress;
    private long _lastCopyUpdate;
    private StorageConnection? _connection;
    private bool _disposed;
    private bool _reconnectPending;
    private Guid? _openingConnectionId;
    private Guid? _deletedConnectionId;
    public Guid? ConnectionId => _openingConnectionId ?? _connection?.Id ?? _deletedConnectionId;
    private string? _bucket;
    private string _prefix = "";
    private string? _continuation;
    private string? _injectedPathKey;
    private string _filter = "";
    private int _searchScopeIndex;
    private string _searchDisplayPrefix = "";
    private readonly List<ObjectEntry> _searchResults = [];
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _operation;
    private bool _searchMode;
    private bool _searchRunning;
    private bool _searchComplete;
    private int _searchScanned;
    private int _searchRequests;
    private int _searchMatches;
    private string _searchScope = "";
    private PrefixSearchCursor? _searchCursor;
    private bool _searchStopped;
    private bool _searchFailed;
    private const int MaxDisplayedSearchResults = 6000;
    private bool _preparing;
    private bool _checkingTransferConflicts;
    private int _operationScanned;
    private int _operationRequests;
    private int _operationChecks;
    private long _lastOperationUpdate;
    private string _error = "";
    private int _generation;
    private bool _busy;
    private bool _loadingMore;
    private bool _sorted = true; // Apply the initially displayed Name ascending sort.
    private bool _descending;
    private ListingSort _sort = ListingSort.Name;
    private ExplorerDetail? _detail;
    private CancellationTokenSource? _headerLoad;
    private int _headerGeneration;
    private string? _headerKey;
    private ObjectHead? _headerHead;
    private bool _headerPending;
    private bool _headerFailed;
    private bool _iconView;
    private double _toolbarWidth = 800;
    private bool _toolbarSearchOpen;
    private readonly PreviewPaneViewModel _preview;
    private readonly PreviewService _imagePreviewService;
    private ImagePreviewWindow? _imageViewer;
    private Func<Task>? _afterLeave;
    private bool _showPreviewForClose;
    private int _leaveConfirmationId;

    public ExplorerViewModel(
        ExplorerService explorer,
        TransferEngine transfers,
        LocalizationService texts,
        DialogService dialogs,
        ToastService toasts,
        ApplicationState state,
        ISettingsStore settings,
        PlaceLibrary places,
        IDownloadLinkSigner linkSigner,
        PreviewService preview,
        IObjectStorageProviderFactory storage,
        ExplorerClipboard clipboard,
        ITransferConnections transferConnections)
    {
        _explorer = explorer;
        _transfers = transfers;
        _transferConnections = transferConnections;
        _texts = texts;
        _dialogs = dialogs;
        _toasts = toasts;
        _state = state;
        _state.ExplorerDisplayChanged += OnExplorerDisplayChanged;
        _settings = settings;
        _places = places;
        _places.Changed += OnPlacesChanged;
        _linkSigner = linkSigner;
        _storage = storage;
        _clipboard = clipboard;
        _clipboard.Changed += OnClipboardChanged;
        _clipboard.StorageChanged += OnStorageChanged;
        _preview = new PreviewPaneViewModel(preview, storage, texts, toasts);
        _imagePreviewService = preview;
        _preview.ImageViewerRequested += OnImageViewerRequested;
        _preview.LeaveResolved += (_, _) => ResumeAfterLeave();
        _preview.LeaveCancelled += (_, _) => _afterLeave = null;
        _preview.Saved += (_, head) => ApplySavedObject(head);
        _preview.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowSide));
            OnPropertyChanged(nameof(SideExpanded));
        };
        _iconView = state.Preferences.ObjectBrowser == ObjectBrowserView.Icons;
        _texts.PropertyChanged += OnTextsChanged;
        transfers.Changed += OnTransferChanged;
        transfers.BatchChanged += OnCopyBatchChanged;

        RefreshCommand = CreateAsyncCommand(_ => RefreshAsync());
        GoToPathCommand = new RelayCommand(_ => RunUiOperation(() => GoToPathAsync(ShowGoToPathPrompt)),
            _ => CanGoToPath);
        LoadMoreCommand = CreateAsyncCommand(_ => LoadMoreAsync());
        RunSearchCommand = CreateAsyncCommand(_ => RunSearchAsync());
        ContinueSearchCommand = CreateAsyncCommand(_ => RunSearchAsync(continueExisting: true));
        CancelSearchCommand = new RelayCommand(_ => StopSearch());
        ClearSearchCommand = new RelayCommand(_ => { Filter = string.Empty; CancelSearch(); PublishRows(false); NotifyChrome(); });
        CancelOperationCommand = new RelayCommand(_ => _operation?.Cancel());
        CancelMutationCommand = new RelayCommand(_ => _mutation?.Cancel());
        UpCommand = CreateAsyncCommand(_ => UpAsync());
        OpenRowCommand = CreateAsyncCommand(OpenRowAsync);
        OpenRowNewTabCommand = new RelayCommand(OpenRowInNewTab);
        OpenCrumbCommand = CreateAsyncCommand(OpenCrumbAsync);
        NewFolderCommand = CreateAsyncCommand(_ => NewFolderAsync());
        NewTextFileCommand = CreateAsyncCommand(_ => NewTextFileAsync());
        DeleteCommand = CreateAsyncCommand(_ => DeleteAsync());
        RenameCommand = CreateAsyncCommand(_ => RenameAsync());
        CopyCommand = new RelayCommand(_ => Remember(cut: false), _ => CanCopy);
        CopyToCommand = CreateAsyncCommand(_ => CopyToAsync());
        CutCommand = new RelayCommand(_ => Remember(cut: true), _ => CanCut);
        PasteCommand = CreateAsyncCommand(_ => PasteAsync());
        CopyKeyCommand = new RelayCommand(_ => CopyText(keys: true), _ => CanCopyKey);
        CopyNameCommand = new RelayCommand(_ => CopyName(), _ => _selected.Count == 1);
        CopyUriCommand = new RelayCommand(_ => CopyText(keys: false), _ => CanCopyKey);
        CopyDetailValueCommand = new RelayCommand(value => CopyDetailValue(value as string));
        ToggleFavoriteCommand = CreateAsyncCommand(_ => ToggleFavoriteAsync());
        ToggleBucketFavoriteCommand = CreateAsyncCommand(ToggleBucketFavoriteAsync);
        ToggleFolderFavoriteCommand = CreateAsyncCommand(_ => ToggleFolderFavoriteAsync());
        ShareLinkCommand = CreateAsyncCommand(_ => ShareLinkAsync());
        UseListViewCommand = new RelayCommand(_ => SetBrowserView(icons: false));
        UseIconViewCommand = new RelayCommand(_ => SetBrowserView(icons: true));
        ToggleGridDensityCommand = new RelayCommand(_ => ToggleGridDensity());
        ToggleDetailsPaneCommand = new RelayCommand(_ => ToggleDetailsPane());
    }

    private AsyncRelayCommand CreateAsyncCommand(Func<object?, Task> action) =>
        new(action, ReportUnexpectedUiError);

    // WPF input events also start operations with parameters gathered from file dialogs.
    // Keep their error handling with the owning Explorer workspace.
    public void RunUiOperation(Func<Task> operation) =>
        _ = UiAsyncTask.RunAsync(operation, ReportUnexpectedUiError);

    private void ReportUnexpectedUiError(Exception exception)
    {
        if (_disposed)
            return;
        var title = exception is StorageException storage
            ? ExplorerText.FailureTitle(_texts, storage.Failure)
            : _texts["Ui_ActionFailed"];
        // A resize or repeated click can generate several failures at once.
        if (_toasts.Items.Any(item => !item.IsClosing && item.Title == title))
            return;
        _toasts.Show(new ToastRequest { Title = title, Tone = ToastTone.Error });
    }

    public LocalizationService Texts => _texts;
    public ResettableCollection<ExplorerRow> Rows { get; } = [];
    public ResettableCollection<BucketRow> BucketRows { get; } = [];
    public ObservableCollection<CrumbViewModel> Crumbs { get; } = [];
    public string PathText { get; private set; } = "";

    public ICommand RefreshCommand { get; }
    public RelayCommand GoToPathCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand RunSearchCommand { get; }
    public ICommand ContinueSearchCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand CancelOperationCommand { get; }
    public ICommand CancelMutationCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand OpenRowCommand { get; }
    public ICommand OpenRowNewTabCommand { get; }
    public ICommand OpenCrumbCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand NewTextFileCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CopyToCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand CopyKeyCommand { get; }
    public ICommand CopyNameCommand { get; }
    public ICommand CopyUriCommand { get; }
    public ICommand CopyDetailValueCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleBucketFavoriteCommand { get; }
    public ICommand ToggleFolderFavoriteCommand { get; }
    public ICommand ShareLinkCommand { get; }
    public ICommand UseListViewCommand { get; }
    public ICommand UseIconViewCommand { get; }
    public ICommand ToggleGridDensityCommand { get; }
    public ICommand ToggleDetailsPaneCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? ""))
                return;
            if (_searchMode)
                ApplySelection([]);
            CancelSearch();
            OnPropertyChanged(nameof(HasFilter));
            if (ShowBuckets)
                PublishBuckets();
            else
                PublishRows(appendOnlyNewPage: false);
            NotifyChrome();
        }
    }

    // 0: loaded names, 1: provider key prefix, 2: bucket-wide name scan, 3: recursive current folder.
    public int SearchScopeIndex
    {
        get => _searchScopeIndex;
        set
        {
            if (value is < 0 or > 3)
                return;
            if (!SetProperty(ref _searchScopeIndex, value))
                return;
            ApplySelection([]);
            CancelSearch();
            PublishRows(false);
            NotifyChrome();
            OnPropertyChanged(nameof(SearchScopeHint));
            OnPropertyChanged(nameof(ShowSearchAction));
        }
    }

    public bool ShowSearchAction => ShowObjects && _searchScopeIndex != 0;
    public string SearchScopeLabel => _texts[ShowBuckets ? "Explorer_ScopeLoaded" : _searchScopeIndex switch
    {
        1 => "Explorer_ScopePrefix",
        2 => "Explorer_ScopeBucket",
        3 => "Explorer_ScopeFolder",
        _ => "Explorer_ScopeLoaded"
    }];
    public string SearchScopeHint => _texts[ShowBuckets ? "Explorer_BucketFilterHint" : _searchScopeIndex switch
    {
        1 => "Explorer_KeyPrefixHint",
        2 => "Explorer_BucketScanHint",
        3 => "Explorer_FolderScanHint",
        _ => "Explorer_LoadedFilterHint"
    }];

    public bool HasConnection => _connection is not null;
    public bool ShowBuckets => !_reconnectPending && _connection is not null && _bucket is null;
    public bool ShowBucketRegion => ShowBuckets && _connection?.Provider == StorageProviderKind.AwsS3
        && _buckets.Any(bucket => !string.IsNullOrWhiteSpace(bucket.Region));
    public bool ShowObjects => !_reconnectPending && _connection is not null && _bucket is not null;
    public bool ShowEmptyConnection => _connection is null && !_busy;
    public bool IsBusy => _busy;
    public bool IsLoadingMore => _loadingMore;
    public bool IsReadOnly => _connection?.IsReadOnly == true;
    public bool HasFilter => _filter.Trim().Length > 0;
    public bool CanRunSearch => ShowSearchAction && HasFilter && !_busy && !_searchRunning;
    public bool ShowSearchStatus => _searchMode;
    public bool ShowSearchLoading => _searchRunning && Rows.Count == 0;
    public string SearchLoadingText => _texts[_searchScopeIndex == 1
        ? "Explorer_KeyPrefixLoading" : "Explorer_SearchLoading"];
    public bool CanCancelSearch => _searchRunning;
    public bool CanContinueSearch => _searchMode && !_searchRunning && _searchCursor is not null
        && _searchResults.Count < MaxDisplayedSearchResults;
    public bool ShowSearchPrompt => ShowObjects && HasFilter && _searchScopeIndex != 0
        && !_searchMode && !_busy;
    public bool ShowSearchPartialEmpty => ShowObjects && _searchMode && !_searchRunning
        && !_searchComplete && !ShowError && Rows.Count == 0;
    public bool ShowOperationStatus => _preparing;
    public string SearchStatusText => string.Format(_texts[
        _searchRunning ? (_searchScopeIndex == 1 ? "Explorer_KeyPrefixProgress" : "Explorer_SearchProgress")
        : _searchFailed ? "Explorer_SearchFailed"
        : _searchStopped ? "Explorer_SearchStopped"
        : _searchComplete ? (_searchScopeIndex == 1 ? "Explorer_KeyPrefixComplete" : "Explorer_SearchComplete")
        : _searchResults.Count >= MaxDisplayedSearchResults ? "Explorer_SearchDisplayLimit"
        : "Explorer_SearchIncomplete"],
        _searchScope, _searchScanned, _searchRequests, _searchMatches);
    public string OperationStatusText => _checkingTransferConflicts
        ? _texts["Transfer_CheckingConflicts"]
        : string.Format(_texts["Explorer_PreparingOperation"],
            _operationScanned, _operationRequests, _operationChecks);
    public string NoMatchesTitle => _texts[ShowBuckets ? "Explorer_BucketsNoMatches"
        : _searchMode ? "Explorer_SearchNoMatches" : "Explorer_NoMatches"];
    public string NoMatchesHint => _texts[ShowBuckets ? "Explorer_BucketsNoMatchesHint"
        : _searchMode
            ? (_searchScopeIndex == 1 ? "Explorer_KeyPrefixNoMatchesHint" : "Explorer_SearchNoMatchesHint")
            : "Explorer_NoMatchesHint"];
    public string Error => _error;
    public bool ShowError => _error.Length > 0 && !_busy && Rows.Count == 0 && BucketRows.Count == 0;
    public bool ShowLoading => _busy && !_loadingMore && Rows.Count == 0 && BucketRows.Count == 0 && _error.Length == 0;
    public bool ShowBucketList => ShowBuckets && !ShowLoading && !ShowError && BucketRows.Count > 0;
    public bool ShowObjectList => ShowObjects && !ShowLoading && !ShowError && Rows.Count > 0;
    public bool IsIconView => _iconView;
    public string? PreferredDownloadDirectory => _state.Preferences.AvailableDownloadDirectory;
    public string? AutomaticDownloadDirectory =>
        _state.Preferences.DownloadDestination == DownloadDestinationPreference.DefaultFolder
            ? PreferredDownloadDirectory : null;
    public bool IsCompactGrid => _state.Preferences.GridDensity == ObjectGridDensity.Compact;
    public string GridDensityTip => _texts[IsCompactGrid ? "Explorer_GridComfortable" : "Explorer_GridCompact"];
    public double TileHeight => IsCompactGrid ? 106 : 136;
    public double TileIconSize => IsCompactGrid ? 30 : 38;
    public double TileIconStrokeThickness => IsCompactGrid ? 2.8 : 3.5;
    public double TileNameSize => IsCompactGrid ? 12 : 13;
    public double TileNameLineHeight => IsCompactGrid ? 17 : 18;
    public double TileNameMaxHeight => TileNameLineHeight * 2;
    public bool ShowObjectTable => ShowObjectList && !_iconView;
    public bool ShowObjectIcons => ShowObjectList && _iconView;
    public IReadOnlyList<ExplorerRow> SelectedRows => _selected;
    public bool HasSelection => _selected.Count > 0;
    public bool CanOpenFolderInNewTab => ShowObjects && !_busy
        && _selected.Count == 1 && _selected[0].IsFolder;
    // Measure the Explorer pane, not the entire shell window.
    public bool ToolbarCompactSearch => _toolbarWidth < 820;
    public bool ToolbarShowSearchInput => !ToolbarCompactSearch || _toolbarSearchOpen;
    public bool ToolbarShowSearchButton => ToolbarCompactSearch && !_toolbarSearchOpen;
    public bool ToolbarShowCloseSearchButton => ToolbarCompactSearch && _toolbarSearchOpen;
    public bool ToolbarShowActions => !_toolbarSearchOpen;
    public bool ToolbarShowExtendedActions => _toolbarWidth >= 1450;
    public bool ToolbarShowDefaultExtras => _toolbarWidth >= 1000;
    public bool ToolbarShowDefaultCommands => ToolbarShowDefaultExtras && ShowPaste;

    public void SetToolbarWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0 || Math.Abs(_toolbarWidth - width) < 1)
            return;
        _toolbarWidth = width;
        if (!ToolbarCompactSearch)
            _toolbarSearchOpen = false;
        else if (HasFilter)
            _toolbarSearchOpen = true; // Never hide an active search when resizing.
        NotifyToolbarLayout();
    }

    public void ToggleToolbarSearch()
    {
        if (!ToolbarCompactSearch)
            return;
        if (_toolbarSearchOpen)
        {
            CloseToolbarSearch();
            return;
        }
        _toolbarSearchOpen = true;
        NotifyToolbarLayout();
    }

    public void CloseToolbarSearch()
    {
        if (!_toolbarSearchOpen)
            return;
        Filter = string.Empty; // Closing search also restores the unfiltered listing.
        _toolbarSearchOpen = false;
        NotifyToolbarLayout();
    }

    private void NotifyToolbarLayout()
    {
        OnPropertyChanged(nameof(ToolbarCompactSearch));
        OnPropertyChanged(nameof(ToolbarShowSearchInput));
        OnPropertyChanged(nameof(ToolbarShowSearchButton));
        OnPropertyChanged(nameof(ToolbarShowCloseSearchButton));
        OnPropertyChanged(nameof(ToolbarShowActions));
        OnPropertyChanged(nameof(ToolbarShowExtendedActions));
        OnPropertyChanged(nameof(ToolbarShowDefaultExtras));
        OnPropertyChanged(nameof(ToolbarShowDefaultCommands));
    }
    public string SelectionLabel => string.Format(_texts[_continuation is not null || _searchMode && !_searchComplete
        ? "Explorer_StatusSelectedLoaded" : "Explorer_StatusSelected"], _selected.Count);
    public bool CanSelectAll => ShowObjects && Rows.Count > 0 && !_busy;
    public bool ShowEmptyObjects => ShowObjects && !_busy && !ShowError && Rows.Count == 0 &&
        !HasFilter && !_searchMode;
    public bool ShowEmptyBuckets => ShowBuckets && !_busy && !ShowError && BucketRows.Count == 0 && !HasFilter;
    public bool ShowNoMatches => !_busy && !_searchRunning && !ShowError && HasFilter &&
        (ShowBuckets || _searchScopeIndex == 0 || _searchMode && _searchComplete) &&
        Rows.Count == 0 && BucketRows.Count == 0 && HasConnection;
    public bool ShowLoadMore => ShowObjects && !_searchMode && _continuation is not null && !ShowError;
    public PreviewPaneViewModel Preview => _preview;
    public bool HasUnsavedChanges => _preview.IsDirty;

    // Reuse the preview editor\'s existing save/discard/stay flow for closing tabs and exiting.
    // The tab remains alive until the editor resolves the pending leave.
    public Task<bool> ConfirmLeaveAsync()
    {
        CancelGoToPath();
        return ConfirmLeaveAsync(CancellationToken.None);
    }

    private async Task<bool> ConfirmLeaveAsync(CancellationToken cancellationToken)
    {
        if (!_preview.IsDirty)
            return true;

        _afterLeave = null; // Supersede any pending location change with the close request.
        // The user may have hidden the details pane while editing. A close
        // confirmation must never be trapped inside an invisible pane.
        _showPreviewForClose = true;
        OnPropertyChanged(nameof(ShowSide));
        _preview.ShowContent();
        var confirmationId = ++_leaveConfirmationId;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? resolved = null;
        EventHandler? cancelled = null;
        void Cleanup()
        {
            _preview.LeaveResolved -= resolved;
            _preview.LeaveCancelled -= cancelled;
            if (confirmationId == _leaveConfirmationId && !_reconnectPending && !_disposed)
            {
                if (cancellationToken.IsCancellationRequested && _afterLeave is null && _preview.ShowLeaveBar)
                    _preview.StayCommand.Execute(null);
                _showPreviewForClose = false;
                OnPropertyChanged(nameof(ShowSide));
            }
        }
        resolved = (_, _) => completion.TrySetResult(true);
        cancelled = (_, _) => completion.TrySetResult(false);
        _preview.LeaveResolved += resolved;
        _preview.LeaveCancelled += cancelled;
        _preview.MarkLeavePending();
        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Cleanup();
        }
    }
    public bool ShowSide => (_state.Preferences.ShowDetailsPane || _showPreviewForClose)
        && (_detail is not null || _preview.IsOpen);
    public bool DetailsPaneEnabled => _state.Preferences.ShowDetailsPane;
    public string DetailsPaneTip => _texts[DetailsPaneEnabled ? "Explorer_HideDetails" : "Explorer_ShowDetails"];
    public double DetailsPaneWidth => _state.Preferences.DetailsPaneWidth;
    public bool SideExpanded => _preview.IsOpen && _preview.ShowingContent;
    public ExplorerDetail? Detail => _detail;
    public string LoadMoreText => _texts[_loadingMore ? "Explorer_Loading" : "Explorer_LoadMore"];
    public bool CanGoUp => ShowObjects && !_busy && (_prefix != Root || CanListBuckets);
    public bool CanRefresh => HasConnection && !_busy;
    // Keep primary actions in place while browsing; only their enabled state follows loading.
    public bool ShowWriteActions => ShowObjects && _connection?.IsReadOnly == false;
    public bool CanWrite => ShowWriteActions && !_busy;
    // Search results can span prefixes. A blank area there is not a destination.
    public bool CanWriteToCurrentDirectory => CanWrite && !_searchMode && _bucket is not null;
    public bool CanOpen => !_reconnectPending && !_busy && _selected.Count == 1;
    public bool CanDelete => CanWrite && _selected.Count > 0;
    public bool CanRename => CanWrite && _selected.Count == 1;
    public bool CanCopy => ShowObjects && !_busy && _selected.Count > 0;
    public bool CanCopyTo => CanCopy;
    public bool CanCut => CanCopy && CanWrite && ServerCopy;
    public bool CanPaste => CanPasteSnapshot(WindowsFileClipboard.Read());
    public bool CanPasteToCurrentDirectory => CanWriteToCurrentDirectory && CanPaste;
    public bool ShowPaste => ShowWriteActions && CanPaste;
    public string PasteLabel => _clipboard.Resolve(WindowsFileClipboard.Read()) == PasteSource.Internal
        ? string.Format(_texts[_clipboard.Current?.Cut == true
            ? "Explorer_PasteMoveCount" : "Explorer_PasteCount"],
            _clipboard.Current?.Entries.Count ?? 0)
        : _texts["Explorer_Paste"];

    private bool CanPasteSnapshot(FileClipboardSnapshot snapshot) => CanWrite
        && _connection is not null && _bucket is not null
        && (_clipboard.Resolve(snapshot) switch
        {
            PasteSource.Internal => _clipboard.Current is { } clip
                && (clip.ConnectionId != _connection.Id || ServerCopy) && clip.Entries.Count > 0,
            PasteSource.LocalFiles => snapshot.Paths.Count > 0,
            _ => false
        });

    public void RefreshPasteAvailability()
    {
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanPasteToCurrentDirectory));
        OnPropertyChanged(nameof(ShowPaste));
        OnPropertyChanged(nameof(PasteLabel));
        OnPropertyChanged(nameof(ToolbarShowDefaultCommands));
    }
    public bool ShowMutationStatus => _mutation is not null;
    public string MutationStatusText => _copyProgress is null
        ? _texts["Explorer_CopyPlanning"]
        : string.Format(_texts[_copyProgress.Stage switch
        {
            CopyStage.Checking => "Explorer_CopyChecking",
            CopyStage.Copying => "Explorer_CopyProgress",
            CopyStage.Removing => "Explorer_MoveProgress",
            _ => "Explorer_DeleteProgress"
        }], _copyProgress.Completed, _copyProgress.Total);
    public bool CanCopyKey => _selected.Count > 0 && !_busy;
    public bool CanFavorite => _connection is not null && !_busy;
    public bool CanFavoriteFolder => CanFavorite && _bucket is not null
        && _selected.Count == 1 && _selected[0].IsFolder;
    public string FolderFavoriteTip => _texts[CanFavoriteFolder && _places.IsFavorite(
        _connection!.Id, _bucket, _selected[0].Entry.Key)
        ? "Places_RemoveFavorite" : "Places_AddFavorite"];
    public bool IsCurrentFavorite => _connection is not null &&
        _places.IsFavorite(_connection.Id, _bucket, _prefix);
    public string FavoriteTip => _texts[IsCurrentFavorite ? "Places_RemoveFavorite" : "Places_AddFavorite"];
    public bool CanShareLink => ShowObjects && !_busy && _selected.Count == 1 && !_selected[0].IsFolder;
    public bool CanDownload => ShowObjects && !_busy && _selected.Count > 0;
    public bool IsDropTarget { get; private set; }
    public string DropDestination => _bucket is null ? "" : string.Format(
        _texts["Transfer_DropDestination"], (_bucket + "/" + (_dropPrefix ?? _prefix)).TrimEnd('/'));
    public bool CanLoadMore => ShowLoadMore && !_busy;

    public string Heading
    {
        get
        {
            if (_connection is null)
                return "";
            return _bucket is null ? _connection.Name : $"{_connection.Name} / {_bucket}";
        }
    }

    public ExplorerPlace? Place => _connection is null
        ? null
        : new ExplorerPlace(_connection, _bucket, _prefix, _bucket is null);

    public string StatusText
    {
        get
        {
            if (_connection is null)
                return _texts["Status_NoItems"];
            if (_selected.Count > 0)
                return string.Format(_texts["Explorer_StatusSelected"], _selected.Count);
            if (ShowBuckets)
                return string.Format(_texts["Explorer_StatusBuckets"], _buckets.Count);
            if (_continuation is not null)
                return string.Format(_texts["Explorer_StatusMore"], _loaded.Count);
            return string.Format(_texts["Explorer_StatusItems"], _loaded.Count);
        }
    }

    public void Dispose()
    {
        SetDropTarget(false);
        _disposed = true;
        GoToPathCommand.RaiseCanExecuteChanged();
        _generation++;
        CancelGoToPath();
        _state.ExplorerDisplayChanged -= OnExplorerDisplayChanged;
        _clipboard.Changed -= OnClipboardChanged;
        _clipboard.StorageChanged -= OnStorageChanged;
        _texts.PropertyChanged -= OnTextsChanged;
        _places.Changed -= OnPlacesChanged;
        _transfers.Changed -= OnTransferChanged;
        _transfers.BatchChanged -= OnCopyBatchChanged;
        _copyBatchesAwaitingResult.Clear();
        _refreshDelay?.Cancel();
        CancelSearch();
        _operation?.Cancel();
        _mutation?.Cancel();
        _browse.Cancel();
        CancelHeaderLoad();
        _imageViewer?.Close();
        _imageViewer = null;
        _preview.ImageViewerRequested -= OnImageViewerRequested;
        _preview.Dispose();
    }

    public void MarkConnectionUnavailable()
    {
        if (_disposed || _connection is not { } connection)
            return;

        _deletedConnectionId = connection.Id;
        _connection = null;
        _reconnectPending = true;
        CancelGoToPath();
        _generation++;
        _operation?.Cancel();
        _mutation?.Cancel();
        _browse.Cancel();
        CancelSearch();
        CancelHeaderLoad();
        _selected.Clear();
        _loaded.Clear();
        _buckets.Clear();
        Rows.Clear();
        BucketRows.Clear();
        Crumbs.Clear();
        SetBusy(false, more: false);
        _preview.InvalidateContext();
        _afterLeave = null;

        if (_preview.IsDirty)
        {
            _showPreviewForClose = true;
            OnPropertyChanged(nameof(ShowSide));
            _preview.ShowContent();
            _preview.MarkLeavePending();
        }

        SetError(_texts["Connection_DeletedExternally"]);
        OnPropertyChanged(nameof(ConnectionId));
        NotifyChrome();
        RaiseCommands();
    }

    public async Task ApplyConnectionChangeAsync(Cavdeck.Application.Connections.ConnectionChange change)
    {
        if (_disposed || _connection?.Id != change.Current.Id)
            return;
        CancelGoToPath();
        var previous = _connection;
        var reconnect = _reconnectPending || change.RequiresReconnect
            || new Cavdeck.Application.Connections.ConnectionChange(previous, change.Current).RequiresReconnect;
        _connection = change.Current;
        _deletedConnectionId = null;
        if ((reconnect || change.Current.IsReadOnly) && _clipboard.Current is { Cut: true } cut
            && cut.ConnectionId == change.Current.Id)
            _clipboard.ClearIfCurrent(cut);
        // Stop prepared and running mutations; no stale confirmation may submit another write.
        if (reconnect || change.Current.IsReadOnly)
        {
            _operation?.Cancel();
            _mutation?.Cancel();
        }
        _reconnectPending = reconnect;
        if (reconnect)
        {
            // The old listing cannot be used against the new backend, even after Stay.
            _generation++;
            _browse.Cancel();
            CancelSearch();
            CancelHeaderLoad();
            _selected.Clear();
            _loaded.Clear();
            _buckets.Clear();
            Rows.Clear();
            BucketRows.Clear();
            Crumbs.Clear();
            SetBusy(false, more: false);
            _preview.InvalidateContext();
            SetError(_texts["Connection_ReconnectPending"]);
        }
        else
            _preview.UpdateConnection(change.Current, !change.Current.IsReadOnly);
        if (!reconnect)
            RebuildCrumbs();
        RefreshContextDetail();
        NotifyChrome();
        RaiseCommands();
        if (!reconnect)
            return;
        // A draft created while the save was in flight must never be discarded.
        if (_preview.IsDirty)
        {
            _afterLeave = null;
            _showPreviewForClose = true;
            OnPropertyChanged(nameof(ShowSide));
            _preview.ShowContent();
            _preview.MarkLeavePending();
            return;
        }
        _showPreviewForClose = false;
        OnPropertyChanged(nameof(ShowSide));
        _preview.Close();
        ClearFilter();
        var keep = Cavdeck.Application.Connections.ConnectionChange.ContainsLocation(
            change.Current, _bucket, _prefix);
        var bucket = keep ? _bucket : change.Current.RestrictedBucket;
        var prefix = keep ? _prefix : ObjectLayout.RootPrefix(change.Current);
        _reconnectPending = false;
        await GoToAsync(bucket, prefix, buckets: bucket is null);
    }

    public async Task OpenPlaceAsync(ExplorerPlace place)
    {
        CancelGoToPath();
        if (_reconnectPending)
        {
            await RetryReconnectAsync();
            return;
        }
        _openingConnectionId = place.Connection.Id;
        StorageConnection? current;
        try
        {
            current = await _transferConnections.FindAsync(place.Connection.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (!_disposed)
                Surface(exception, keepList: true);
            return;
        }
        if (_disposed || current is null)
            return;
        place = place with { Connection = current };
        if (!Cavdeck.Application.Connections.ConnectionChange.ContainsLocation(current, place.Bucket, place.Prefix))
            place = new ExplorerPlace(current, current.RestrictedBucket, ObjectLayout.RootPrefix(current),
                string.IsNullOrEmpty(current.RestrictedBucket));
        if (_preview.IsDirty)
        {
            _afterLeave = () => OpenPlaceAsync(place);
            _preview.MarkLeavePending();
            return;
        }
        _connection = place.Connection;
        _deletedConnectionId = null;
        _openingConnectionId = null;
        ClearFilter();
        await GoToAsync(place.Bucket, place.Prefix, place.ListingBuckets);
    }

    public async Task OpenAsync(StorageConnection connection)
    {
        CancelGoToPath();
        if (_reconnectPending)
        {
            await RetryReconnectAsync();
            return;
        }
        if (_disposed)
            return;
        if (_preview.IsDirty)
        {
            _afterLeave = () => OpenAsync(connection);
            _preview.MarkLeavePending();
            return;
        }
        _connection = connection;
        _deletedConnectionId = null;
        ClearFilter();
        await GoToAsync(
            string.IsNullOrEmpty(connection.RestrictedBucket) ? null : connection.RestrictedBucket,
            ObjectLayout.RootPrefix(connection),
            buckets: string.IsNullOrEmpty(connection.RestrictedBucket));
    }

    public void SetBrowserView(bool icons)
    {
        if (_iconView == icons)
            return;
        _iconView = icons;
        OnPropertyChanged(nameof(IsIconView));
        OnPropertyChanged(nameof(ShowObjectTable));
        OnPropertyChanged(nameof(ShowObjectIcons));
        var next = icons ? ObjectBrowserView.Icons : ObjectBrowserView.List;
        if (_state.Preferences.ObjectBrowser == next)
            return;
        _state.Preferences.ObjectBrowser = next;
        _state.NotifyExplorerDisplayChanged();
        RunUiOperation(() => _settings.SaveAsync(_state.Preferences));
    }

    private void ToggleGridDensity()
    {
        _state.Preferences.GridDensity = IsCompactGrid
            ? ObjectGridDensity.Comfortable : ObjectGridDensity.Compact;
        _state.NotifyExplorerDisplayChanged();
        RunUiOperation(() => _settings.SaveAsync(_state.Preferences));
    }

    private void ToggleDetailsPane()
    {
        // Closing a temporary expanded reader must not restore it unexpectedly later.
        if (_state.Preferences.ShowDetailsPane && _preview.IsExpanded)
            _preview.ToggleExpandedCommand.Execute(null);
        _state.Preferences.ShowDetailsPane = !_state.Preferences.ShowDetailsPane;
        _state.NotifyExplorerDisplayChanged();
        RunUiOperation(() => _settings.SaveAsync(_state.Preferences));
    }

    public void SetDetailsPaneWidth(double width)
    {
        var value = Math.Clamp(width, 280, 720);
        if (Math.Abs(_state.Preferences.DetailsPaneWidth - value) < 1)
            return;
        _state.Preferences.DetailsPaneWidth = value;
        _state.NotifyExplorerDisplayChanged();
        RunUiOperation(() => _settings.SaveAsync(_state.Preferences));
    }

    private void OnExplorerDisplayChanged(object? sender, EventArgs e)
    {
        var icons = _state.Preferences.ObjectBrowser == ObjectBrowserView.Icons;
        if (_iconView != icons)
        {
            _iconView = icons;
            OnPropertyChanged(nameof(IsIconView));
            OnPropertyChanged(nameof(ShowObjectTable));
            OnPropertyChanged(nameof(ShowObjectIcons));
        }
        OnPropertyChanged(nameof(IsCompactGrid));
        OnPropertyChanged(nameof(GridDensityTip));
        OnPropertyChanged(nameof(TileHeight));
        OnPropertyChanged(nameof(TileIconSize));
        OnPropertyChanged(nameof(TileIconStrokeThickness));
        OnPropertyChanged(nameof(TileNameSize));
        OnPropertyChanged(nameof(TileNameLineHeight));
        OnPropertyChanged(nameof(TileNameMaxHeight));
        OnPropertyChanged(nameof(DetailsPaneEnabled));
        OnPropertyChanged(nameof(DetailsPaneTip));
        OnPropertyChanged(nameof(DetailsPaneWidth));
        OnPropertyChanged(nameof(ShowSide));
    }

    public void SetSelection(IReadOnlyList<ExplorerRow> rows) => ApplySelection(rows);

    private async Task ToggleFavoriteAsync()
    {
        if (!CanFavorite || _connection is null)
            return;
        try
        {
            await _places.ToggleFavoriteAsync(_connection.Id, _bucket, _prefix);
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Places_SaveFailed"], Tone = ToastTone.Error });
        }
    }

    private void OnPlacesChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsCurrentFavorite));
        OnPropertyChanged(nameof(FavoriteTip));
        OnPropertyChanged(nameof(FolderFavoriteTip));
        if (_connection is not null)
            foreach (var row in BucketRows)
                row.SetFavorite(_places.IsFavorite(_connection.Id, row.Name, Root));
    }

    private async Task ToggleBucketFavoriteAsync(object? parameter)
    {
        if (parameter is not BucketRow row || _connection is null || _busy)
            return;
        try { await _places.ToggleFavoriteAsync(_connection.Id, row.Name, Root); }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Places_SaveFailed"], Tone = ToastTone.Error });
        }
    }

    private async Task ToggleFolderFavoriteAsync()
    {
        if (!CanFavoriteFolder || _connection is null)
            return;
        try { await _places.ToggleFavoriteAsync(_connection.Id, _bucket, _selected[0].Entry.Key); }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Places_SaveFailed"], Tone = ToastTone.Error });
        }
    }

    public bool RequestSelection(IReadOnlyList<ExplorerRow> rows)
    {
        if (_reconnectPending)
            return false;
        if (_preview.IsDirty && !SameKeys(rows))
        {
            var copy = rows.ToList();
            _afterLeave = () =>
            {
                ApplySelection(copy);
                if (_preview.IsOpen && (copy.Count != 1 || copy[0].Entry.Key != _preview.Key))
                    _preview.Close();
                SelectionSyncRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            };
            _preview.MarkLeavePending(rows.Count == 1 ? rows[0].Name : null);
            return false;
        }

        ApplySelection(rows);
        if (_preview.IsOpen && (rows.Count != 1 || rows[0].Entry.Key != _preview.Key))
            _preview.Close();
        return true;
    }

    public Task OpenEntryAsync(ExplorerRow row)
    {
        if (_busy || _reconnectPending || _disposed)
            return Task.CompletedTask;
        if (row.IsFolder)
        {
            if (_bucket is null)
                return Task.CompletedTask;
            ClearFilter();
            return GoToAsync(_bucket, row.Entry.Key, buckets: false);
        }

        if (_connection is null || _bucket is null)
            return Task.CompletedTask;
        if (!_state.Preferences.ShowDetailsPane)
            ToggleDetailsPane();
        if (_preview.IsOpen && _preview.Key == row.Entry.Key)
        {
            _preview.ShowContent();
            return Task.CompletedTask;
        }

        if (_preview.IsDirty)
        {
            _afterLeave = async () =>
            {
                ApplySelection([row]);
                if (_connection is not null && _bucket is not null)
                    await _preview.OpenAsync(_connection, _bucket, row.Name, row.Entry.Key, CanWrite);
            };
            _preview.MarkLeavePending(row.Name, opensFile: true);
            return Task.CompletedTask;
        }

        return _preview.OpenAsync(_connection, _bucket, row.Name, row.Entry.Key, CanWrite);
    }

    private void OnImageViewerRequested(object? sender, EventArgs e)
    {
        if (_disposed || _connection is null || _bucket is null || !_preview.ShowImageViewer)
            return;

        var selectedKey = _preview.Key;
        // Snapshot the displayed order, including any active sort or search result order.
        // The gallery never mutates Explorer selection while it is being browsed.
        var items = Rows
            .Where(row => !row.IsFolder && PreviewRules.Classify(row.Name) == PreviewFileType.Image)
            .Select(row => new ImagePreviewItem(row.Entry.Key, row.Name))
            .ToList();

        if (!items.Any(item => string.Equals(item.Key, selectedKey, StringComparison.Ordinal)))
            items.Insert(0, new ImagePreviewItem(selectedKey, _preview.Title));

        var gallery = new ImagePreviewGallery(items, selectedKey);
        var initial = _preview.Image as BitmapSource;

        _imageViewer?.Close();
        var model = new ImagePreviewViewModel(
            _imagePreviewService, _connection, _bucket, gallery, _texts, initial);
        var window = new ImagePreviewWindow(model, _texts)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_imageViewer, window))
                _imageViewer = null;
        };

        _imageViewer = window;
        try
        {
            window.Show();
            window.Activate();
        }
        catch (Exception exception)
        {
            _imageViewer = null;
            model.Dispose();
            ReportUnexpectedUiError(exception);
        }
    }

    public ListingSort? ActiveSort => _sorted ? _sort : null;
    public bool SortDescending => _descending;

    public void ApplySort(ListingSort sort, bool descending)
    {
        _sorted = true;
        _sort = sort;
        _descending = descending;
        OnPropertyChanged(nameof(ActiveSort));
        OnPropertyChanged(nameof(SortDescending));
        PublishRows(appendOnlyNewPage: false);
    }

    private void ApplySelection(IReadOnlyList<ExplorerRow> rows, ObjectHead? knownHead = null)
    {
        _selected.Clear();
        _selected.AddRange(rows);
        if (knownHead is not null)
        {
            CancelHeaderLoad();
            _headerKey = knownHead.Key;
            _headerHead = knownHead;
        }
        else if (rows.Count == 1)
            BeginHeaderLoad(rows[0]);
        else
            CancelHeaderLoad();

        RefreshContextDetail();
        NotifyChrome();
        RaiseCommands();
    }

    private static bool SameKeys(IReadOnlyList<ExplorerRow> rows, IReadOnlyList<ExplorerRow> current) =>
        rows.Count == current.Count && rows.Select(row => row.Entry.Key).SequenceEqual(current.Select(row => row.Entry.Key));

    private bool SameKeys(IReadOnlyList<ExplorerRow> rows) => SameKeys(rows, _selected);

    private Task RetryReconnectAsync() => _connection is { } connection
        ? ApplyConnectionChangeAsync(new Cavdeck.Application.Connections.ConnectionChange(null, connection))
        : Task.CompletedTask;

    private void ResumeAfterLeave()
    {
        if (_reconnectPending)
        {
            _afterLeave = null;
            RunUiOperation(RetryReconnectAsync);
            return;
        }
        var next = _afterLeave;
        _afterLeave = null;
        if (next is not null)
        {
            CancelGoToPath();
            RunUiOperation(next);
        }
    }

    private void ApplySavedObject(ObjectHead head)
    {
        foreach (var row in Rows)
        {
            if (row.Entry.Key != head.Key)
                continue;
            row.Replace(row.Entry with
            {
                SizeBytes = head.SizeBytes,
                ETag = head.ETag,
                LastModified = DateTimeOffset.UtcNow
            });
        }

        if (_selected.Count == 1 && _selected[0].Entry.Key == head.Key)
        {
            BeginHeaderLoad(_selected[0]);
            _detail = BuildDetail(_selected[0]);
            OnPropertyChanged(nameof(Detail));
        }
    }

    private void NotifyChrome()
    {
        if (IsDropTarget && !CanWriteToCurrentDirectory)
            SetDropTarget(false);
        OnPropertyChanged(nameof(CanRunSearch));
        OnPropertyChanged(nameof(ShowSearchAction));
        OnPropertyChanged(nameof(SearchScopeHint));
        OnPropertyChanged(nameof(SearchScopeLabel));
        OnPropertyChanged(nameof(SearchLoadingText));
        OnPropertyChanged(nameof(ShowSearchStatus));
        OnPropertyChanged(nameof(ShowSearchLoading));
        OnPropertyChanged(nameof(ShowSearchPrompt));
        OnPropertyChanged(nameof(ShowSearchPartialEmpty));
        OnPropertyChanged(nameof(CanContinueSearch));
        OnPropertyChanged(nameof(ShowOperationStatus));
        OnPropertyChanged(nameof(HasConnection));
        OnPropertyChanged(nameof(ShowBuckets));
        OnPropertyChanged(nameof(ShowBucketRegion));
        OnPropertyChanged(nameof(ShowObjects));
        OnPropertyChanged(nameof(ShowEmptyConnection));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowBucketList));
        OnPropertyChanged(nameof(ShowObjectList));
        OnPropertyChanged(nameof(ShowObjectTable));
        OnPropertyChanged(nameof(ShowObjectIcons));
        OnPropertyChanged(nameof(ShowEmptyObjects));
        OnPropertyChanged(nameof(ShowEmptyBuckets));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowLoadMore));
        OnPropertyChanged(nameof(ShowSide));
        OnPropertyChanged(nameof(SideExpanded));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ShowWriteActions));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanWriteToCurrentDirectory));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanOpenFolderInNewTab));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(ToolbarShowDefaultCommands));
        OnPropertyChanged(nameof(PasteLabel));
        OnPropertyChanged(nameof(MutationStatusText));
        OnPropertyChanged(nameof(GridDensityTip));
        OnPropertyChanged(nameof(DetailsPaneTip));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanCopyTo));
        OnPropertyChanged(nameof(CanCut));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanPasteToCurrentDirectory));
        OnPropertyChanged(nameof(ShowPaste));
        OnPropertyChanged(nameof(CanCopyKey));
        OnPropertyChanged(nameof(CanFavorite));
        OnPropertyChanged(nameof(CanFavoriteFolder));
        OnPropertyChanged(nameof(IsCurrentFavorite));
        OnPropertyChanged(nameof(FavoriteTip));
        OnPropertyChanged(nameof(FolderFavoriteTip));
        OnPropertyChanged(nameof(CanShareLink));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Detail));
    }

    private void RaiseCommands()
    {
        GoToPathCommand.RaiseCanExecuteChanged();
        if (CopyCommand is RelayCommand copy)
            copy.RaiseCanExecuteChanged();
        if (CutCommand is RelayCommand cut)
            cut.RaiseCanExecuteChanged();
        if (CopyKeyCommand is RelayCommand key)
            key.RaiseCanExecuteChanged();
        if (CopyNameCommand is RelayCommand name)
            name.RaiseCanExecuteChanged();
        if (CopyUriCommand is RelayCommand uri)
            uri.RaiseCanExecuteChanged();
    }

    private void OnTextsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RefreshLanguage();

    private void RefreshLanguage()
    {
        OnPropertyChanged(nameof(DropDestination));
        foreach (var row in Rows)
            row.Refresh();
        foreach (var bucket in BucketRows)
            bucket.RefreshLanguage();
        RefreshContextDetail();
        _preview.RefreshLanguage();
        OnPropertyChanged(nameof(LoadMoreText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SearchStatusText));
        OnPropertyChanged(nameof(SearchScopeHint));
        OnPropertyChanged(nameof(SearchScopeLabel));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(PasteLabel));
        OnPropertyChanged(nameof(MutationStatusText));
        OnPropertyChanged(nameof(GridDensityTip));
        OnPropertyChanged(nameof(DetailsPaneTip));
        OnPropertyChanged(nameof(NoMatchesTitle));
        OnPropertyChanged(nameof(NoMatchesHint));
        OnPropertyChanged(nameof(OperationStatusText));
        RebuildCrumbs();
    }

    public readonly record struct ExplorerPlace(
        StorageConnection Connection, string? Bucket, string Prefix, bool ListingBuckets);

}
