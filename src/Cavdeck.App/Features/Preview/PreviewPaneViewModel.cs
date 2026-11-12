using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Preview;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Preview;

public sealed class PreviewPaneViewModel : ViewModelBase, IDisposable
{
    private readonly PreviewService _preview;
    private readonly IObjectStorageProviderFactory _storage;
    private readonly LocalizationService _texts;
    private readonly ToastService _toasts;
    private readonly PreviewSession _session = new();
    private StorageConnection? _connection;
    private string? _bucket;
    private string _key = "";
    private string _name = "";
    private string _original = "";
    private string _editor = "";
    private string? _etag;
    private string? _contentType;
    private TextCharset _charset;
    private bool _bom;
    private long _size;
    private bool _isText;
    private bool _isImage;
    private bool _mediaReady;
    private bool _shellAllowed;
    private bool _downloadSuggested;
    private bool _failed;
    private bool _loading;
    private bool _saving;
    private bool _readOnly;
    private bool _saved;
    private bool _open;
    private bool _showingContent;
    private bool _editing;
    private bool _expanded;
    private bool _leavePending;
    private string _leaveTarget = "";
    private bool _leaveOpensFile;
    private bool _conflictPending;
    private bool _requiresOverwrite;
    private string? _noticeKey;
    private string? _noticeArgument;
    private StorageFailure? _failure;
    private string _notice = "";
    private string _status = "";
    private string _saveError = "";
    private ImageSource? _image;
    private PreviewFileLease? _pdfLease;
    private bool _preparingFile;
    private bool _previewRecoverable;
    private long _downloadedBytes;

    public PreviewPaneViewModel(
        PreviewService preview,
        IObjectStorageProviderFactory storage,
        LocalizationService texts,
        ToastService toasts)
    {
        _preview = preview;
        _storage = storage;
        _texts = texts;
        _toasts = toasts;
        SaveCommand = CreateAsyncCommand(_ => SaveAsync(overwrite: false));
        RevertCommand = new RelayCommand(_ => Revert(), _ => CanRevert);
        DiscardLeaveCommand = new RelayCommand(_ => DiscardLeave());
        StayCommand = new RelayCommand(_ => Stay());
        ReloadCommand = CreateAsyncCommand(_ => ReloadAsync());
        OverwriteCommand = CreateAsyncCommand(_ => SaveAsync(overwrite: true));
        ShellCommand = CreateAsyncCommand(_ => OpenWithShellAsync());
        DownloadCommand = new RelayCommand(_ => DownloadRequested?.Invoke(this, EventArgs.Empty));
        RetryCommand = CreateAsyncCommand(_ => OpenCurrentAsync());
        CancelPreviewCommand = new RelayCommand(_ => _session.CancelCurrent());
        ShowContentCommand = new RelayCommand(_ => ShowContent());
        ShowDetailsCommand = new RelayCommand(_ => ShowDetails());
        CopyDraftCommand = new RelayCommand(_ => CopyDraft());
        EditCommand = new RelayCommand(_ => BeginEdit());
        PreviewCommand = new RelayCommand(_ => EndEdit());
        ToggleExpandedCommand = new RelayCommand(_ => ToggleExpanded());
        ImageViewerCommand = new RelayCommand(_ =>
        {
            if (ShowImageViewer)
                ImageViewerRequested?.Invoke(this, EventArgs.Empty);
        });
        MediaViewerCommand = new RelayCommand(_ =>
        {
            if (ShowMediaViewer)
                MediaViewerRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private AsyncRelayCommand CreateAsyncCommand(Func<object?, Task> action) =>
        new(action, _ => _toasts.Show(new ToastRequest
        {
            Title = _texts["Ui_ActionFailed"],
            Tone = ToastTone.Error
        }));

    public event EventHandler? LeaveResolved;
    public event EventHandler? LeaveCancelled;
    public event EventHandler? DownloadRequested;
    public event EventHandler? ImageViewerRequested;
    public event EventHandler? MediaViewerRequested;
    public event EventHandler? StateChanged;
    public event EventHandler<ObjectHead>? Saved;

    public LocalizationService Texts => _texts;
    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand DiscardLeaveCommand { get; }
    public ICommand StayCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand OverwriteCommand { get; }
    public ICommand ShellCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelPreviewCommand { get; }
    public ICommand ShowContentCommand { get; }
    public ICommand ShowDetailsCommand { get; }
    public ICommand CopyDraftCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand ImageViewerCommand { get; }
    public ICommand MediaViewerCommand { get; }

    public string Key => _key;
    public long Size => _size;
    public bool IsOpen => _open;
    public bool IsDirty => _isText && _editor != _original;
    public bool ShowingContent => _showingContent;
    public bool ShowingDetails => !_open || !_showingContent;
    public bool IsLoading => _loading;
    public bool IsReadOnly => _readOnly || !_editing || _saving || _leavePending || _conflictPending;
    public bool IsEditing => _editing;
    public bool IsExpanded => _expanded;
    public string ExpandLabel => _texts[_expanded ? "Preview_RestorePane" : "Preview_ExpandPane"];
    public string FileSummary => _open && !_loading && !_failed
        ? ExplorerText.FormatBytes(_size) : "";
    public bool ShowFileSummary => FileSummary.Length > 0;
    public bool ShowFooter => _isText && !_loading && (_editing || _readOnly || _saved);
    public bool CanEdit => _isText && !_readOnly && !_loading && !_leavePending && !_conflictPending;
    public bool CanBeginEdit => CanEdit && !_editing;
    public bool CanExitEdit => _editing && !IsDirty;
    public bool CanPreviewAction => !_editing || CanExitEdit;
    public bool ShowReadOnlyDraft => _readOnly && IsDirty;
    public bool CanSave => IsDirty && _editing && !_readOnly && !_loading && !_saving && !_conflictPending;
    public bool CanRevert => IsDirty && !_loading && !_saving;
    public string Title => _name;
    public string Notice => _notice;
    public string Status => _status;
    public string LeaveMessage => _leaveOpensFile
        ? string.Format(_texts["Preview_UnsavedTarget"], _name, _leaveTarget)
        : _leaveTarget.Length > 0
            ? string.Format(_texts["Preview_UnsavedSelect"], _name, _leaveTarget)
            : string.Format(_texts["Preview_UnsavedGeneric"], _name);
    public string LeaveSaveLabel => _texts[_leaveOpensFile ? "Preview_SaveOpen" : _leaveTarget.Length > 0 ? "Preview_SaveSelect" : "Preview_SaveContinue"];
    public string LeaveDiscardLabel => _texts[_leaveOpensFile ? "Preview_DiscardOpen" : _leaveTarget.Length > 0 ? "Preview_DiscardSelect" : "Preview_DiscardContinue"];
    public string ConflictTitle => _texts[_requiresOverwrite ? "Preview_ConditionalSaveUnsupported"
        : string.IsNullOrWhiteSpace(_etag) ? "Preview_NoVersion" : "Preview_Conflict"];
    public string ConflictDetail => _texts[_requiresOverwrite ? "Preview_ConditionalSaveUnsupportedDetail"
        : string.IsNullOrWhiteSpace(_etag) ? "Preview_NoVersionDetail" : "Preview_ConflictDetail"];
    public string SaveError => _saveError;
    public string LoadingMessage => _texts["Preview_Loading"];
    public ImageSource? Image => _image;
    public string? PdfPath => _pdfLease?.FilePath;
    public bool ShowPdf => PdfPath is not null && !_loading;
    public bool ShowPreviewDownload => _preparingFile && _loading;
    public bool ShowLoadingSpinner => _loading && !_preparingFile;
    public double PreviewProgress => _size > 0 ? Math.Clamp(100.0 * _downloadedBytes / _size, 0, 100) : 0;
    public string PreviewProgressLabel => string.Format(_texts["Preview_DownloadProgress"],
        ExplorerText.FormatBytes(_downloadedBytes), ExplorerText.FormatBytes(_size),
        (int)PreviewProgress);
    public bool ShowEditor => _isText && !_loading;
    public bool ShowImage => _isImage && !_loading;
    public bool ShowImageViewer => _open && !_loading && _size >= 0
        && _size <= PreviewRules.MaxImmersiveImageBytes
        && (_isImage || PreviewRules.Classify(_name, _contentType) == PreviewFileType.Image);
    public bool ShowMediaViewer => _open && !_loading && !_failed && _mediaReady
        && _size >= 0 && _size <= PreviewRules.MaxMediaBytes;
    public PreviewFileType MediaType => PreviewRules.Classify(_name, _contentType);
    public bool ShowNotice => _notice.Length > 0 && !_isText && !_isImage;
    public bool ShowLoading => _loading;
    public bool ShowLeaveBar => _leavePending && !_conflictPending;
    public bool ShowConflictBar => _conflictPending;
    public bool ShowSaveButton => _isText && _editing && !_readOnly && !ShowLeaveBar && !ShowConflictBar;
    public bool ShowShellButton => _shellAllowed && !_loading && !ShowLeaveBar && !ShowConflictBar;
    public bool ShowDownloadButton => _downloadSuggested && !_loading && !ShowLeaveBar && !ShowConflictBar;
    public bool ShowRetry => (_failed || _previewRecoverable) && !_loading;

    public string EditorText
    {
        get => _editor;
        set
        {
            if (!SetProperty(ref _editor, value ?? ""))
                return;
            _saved = false;
            SetSaveError("");
            RaiseEditorState();
        }
    }

    private void CopyDraft()
    {
        try
        {
            System.Windows.Clipboard.SetText(_editor);
            _toasts.Show(new ToastRequest { Title = _texts["Explorer_ValueCopied"], Tone = ToastTone.Success });
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest { Title = _texts["Explorer_CopyFailed"], Tone = ToastTone.Error });
        }
    }

    public void InvalidateContext()
    {
        _mediaReady = false;
        _session.Reset();
        _loading = false;
        _preparingFile = false;
        OnPropertyChanged(nameof(IsLoading));
        _shellAllowed = false;
        _downloadSuggested = false;
        _previewRecoverable = false;
        _failed = false;
        _conflictPending = false;
        _requiresOverwrite = false;
        SetWritable(false);
        RaiseBars();
        RaiseEditorState();
    }

    public void UpdateConnection(StorageConnection connection, bool writable)
    {
        if (_connection?.Id != connection.Id)
            return;
        _connection = connection;
        SetWritable(writable);
        if (!writable)
        {
            _conflictPending = false;
            RaiseBars();
        }
    }

    public void SetWritable(bool writable)
    {
        if (!SetProperty(ref _readOnly, !writable, nameof(IsReadOnly)))
            return;
        RaiseEditorState();
    }

    private void BeginEdit()
    {
        if (!CanEdit || _editing)
            return;
        _editing = true;
        RaiseEditorState();
    }

    private void EndEdit()
    {
        if (!_editing || IsDirty)
            return;
        _editing = false;
        RaiseEditorState();
    }

    public void ShowContent()
    {
        if (_showingContent)
            return;
        _showingContent = true;
        NotifyPage();
    }

    public void ShowDetails()
    {
        if (!_showingContent)
            return;
        SetExpanded(false);
        _showingContent = false;
        NotifyPage();
    }

    private void ToggleExpanded()
    {
        if (!_open || !_showingContent)
            return;
        SetExpanded(!_expanded);
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
            return;
        _expanded = expanded;
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ExpandLabel));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkLeavePending(string? target = null, bool opensFile = false)
    {
        if (!_open || !IsDirty || _leavePending)
            return;
        _leavePending = true;
        _leaveOpensFile = opensFile;
        _leaveTarget = target ?? "";
        OnPropertyChanged(nameof(LeaveMessage));
        OnPropertyChanged(nameof(LeaveSaveLabel));
        OnPropertyChanged(nameof(LeaveDiscardLabel));
        OnPropertyChanged(nameof(ConflictTitle));
        OnPropertyChanged(nameof(ConflictDetail));
        RaiseBars();
    }

    public async Task OpenAsync(
        StorageConnection connection, string bucket, string name, string key, bool writable)
    {
        _connection = connection;
        _bucket = bucket;
        _name = name;
        _key = key;
        _readOnly = !writable;
        _open = true;
        _showingContent = true;
        _editing = false;
        SetExpanded(false);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsReadOnly));
        NotifyPage();
        await OpenCurrentAsync();
    }

    public void Close()
    {
        _session.Reset();
        SetPdfLease(null);
        _loading = false;
        _preparingFile = false;
        _previewRecoverable = false;
        SetExpanded(false);
        _open = false;
        _showingContent = false;
        _editing = false;
        _leavePending = false;
        _conflictPending = false;
        _requiresOverwrite = false;
        _isText = false;
        _isImage = false;
        _mediaReady = false;
        _shellAllowed = false;
        _downloadSuggested = false;
        _failed = false;
        _saved = false;
        _key = "";
        _name = "";
        _original = "";
        _editor = "";
        _image = null;
        SetNotice(null);
        SetSaveError("");
        UpdateStatus();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EditorText));
        OnPropertyChanged(nameof(Image));
        RaiseEditorState();
        RaiseBars();
        NotifyPage();
    }

    private void NotifyPage()
    {
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(ShowingContent));
        OnPropertyChanged(nameof(ShowingDetails));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshLanguage()
    {
        if (_failed)
            PublishFailure();
        else
            SetNotice(_noticeKey, _noticeArgument);
        UpdateStatus();
        OnPropertyChanged(nameof(LoadingMessage));
        OnPropertyChanged(nameof(ExpandLabel));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(ShowFileSummary));
        OnPropertyChanged(nameof(PreviewProgressLabel));
        OnPropertyChanged(nameof(LeaveMessage));
        OnPropertyChanged(nameof(LeaveSaveLabel));
        OnPropertyChanged(nameof(LeaveDiscardLabel));
        OnPropertyChanged(nameof(ConflictTitle));
        OnPropertyChanged(nameof(ConflictDetail));
    }

    public void Dispose()
    {
        _session.Dispose();
        SetPdfLease(null);
    }

    private async Task OpenCurrentAsync()
    {
        if (_connection is null || _bucket is null || _key.Length == 0)
            return;

        var connection = _connection;
        var bucket = _bucket;
        var key = _key;
        var name = _name;
        var operation = _session.Begin(new PreviewObjectId(connection.Id, bucket, key));
        var token = operation.Token;

        SetPdfLease(null);
        _loading = true;
        _preparingFile = false;
        _previewRecoverable = false;
        _downloadedBytes = 0;
        _failed = false;
        _failure = null;
        _conflictPending = false;
        _requiresOverwrite = false;
        _isText = false;
        _isImage = false;
        _mediaReady = false;
        SetNotice(null);
        SetSaveError("");
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(ShowFileSummary));
        RaiseEditorState();
        RaiseBars();

        PreviewFileLease? pendingLease = null;
        try
        {
            var loaded = await _preview.OpenAsync(connection, bucket, key, name, token);
            if (!_session.IsCurrent(operation))
                return;

            var image = loaded.Kind == PreviewLoadKind.Image
                ? await Task.Run(() => PreviewImageDecoder.Decode(loaded.Image), token)
                : null;

            if (loaded.Kind == PreviewLoadKind.Pdf && loaded.Head is not null)
            {
                _size = loaded.Head.SizeBytes;
                _downloadedBytes = 0;
                _preparingFile = true;
                OnPropertyChanged(nameof(PreviewProgress));
                OnPropertyChanged(nameof(PreviewProgressLabel));
                RaiseEditorState();

                var progress = new Progress<long>(bytes =>
                {
                    if (!_session.IsCurrent(operation) || !_preparingFile || token.IsCancellationRequested)
                        return;
                    _downloadedBytes = Math.Max(0, bytes);
                    OnPropertyChanged(nameof(PreviewProgress));
                    OnPropertyChanged(nameof(PreviewProgressLabel));
                });
                pendingLease = await _preview.PrepareFileAsync(
                    connection, bucket, key, name, loaded.Head.SizeBytes,
                    PreviewRules.MaxPdfBytes, progress, token);
                token.ThrowIfCancellationRequested();
            }

            if (!_session.IsCurrent(operation))
                return;

            Apply(loaded, image);
            if (loaded.Kind == PreviewLoadKind.Pdf)
            {
                SetPdfLease(pendingLease);
                pendingLease = null;
            }
        }
        catch (OperationCanceledException)
        {
            if (_session.IsCurrent(operation) && _open && _preparingFile)
            {
                _previewRecoverable = true;
                _downloadSuggested = true;
                SetNotice("Preview_DownloadCanceled");
            }
        }
        catch (StorageException exception)
        {
            if (_session.IsCurrent(operation))
            {
                var preparing = _preparingFile;
                Apply(new PreviewLoad(PreviewLoadKind.Failed, null, null,
                    TextCharset.Utf8, false, null, exception.Failure), null);
                if (preparing)
                {
                    _previewRecoverable = true;
                    _downloadSuggested = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_session.IsCurrent(operation))
            {
                _previewRecoverable = true;
                _downloadSuggested = true;
                SetNotice("Preview_DownloadFailed");
            }
        }
        finally
        {
            try
            {
                if (pendingLease is not null)
                    await pendingLease.DisposeAsync();
            }
            finally
            {
                if (_session.Complete(operation))
                {
                    _preparingFile = false;
                    _loading = false;
                    OnPropertyChanged(nameof(IsLoading));
                    OnPropertyChanged(nameof(FileSummary));
                    OnPropertyChanged(nameof(ShowFileSummary));
                    RaiseEditorState();
                    RaiseBars();
                }
            }
        }
    }

    public void PdfViewerFailed()
    {
        if (!_open || _pdfLease is null)
            return;
        SetPdfLease(null);
        _previewRecoverable = true;
        _downloadSuggested = true;
        SetNotice("Preview_PdfFailed");
        RaiseEditorState();
        RaiseBars();
    }

    private void SetPdfLease(PreviewFileLease? lease)
    {
        if (ReferenceEquals(_pdfLease, lease))
            return;

        var old = _pdfLease;
        _pdfLease = lease;
        OnPropertyChanged(nameof(PdfPath));
        OnPropertyChanged(nameof(ShowPdf));
        if (old is not null)
            _ = old.DisposeAsync();
    }

    private void Apply(PreviewLoad loaded, ImageSource? decodedImage)
    {
        var head = loaded.Head;
        _size = head?.SizeBytes ?? 0;
        _etag = head?.ETag;
        _contentType = head?.ContentType;
        _shellAllowed = head is not null && PreviewRules.CanOpenInShell(head.SizeBytes)
            && loaded.Kind is PreviewLoadKind.Text or PreviewLoadKind.Image or PreviewLoadKind.Pdf or PreviewLoadKind.External or PreviewLoadKind.TooLarge or PreviewLoadKind.NotText;
        _downloadSuggested = loaded.Kind == PreviewLoadKind.Pdf ||
            (!_shellAllowed && loaded.Kind is PreviewLoadKind.External or PreviewLoadKind.TooLarge or PreviewLoadKind.NotText);
        _failed = loaded.Kind == PreviewLoadKind.Failed;
        _isText = loaded.Kind == PreviewLoadKind.Text;
        _mediaReady = loaded.Kind == PreviewLoadKind.Media;
        _isImage = false;
        _image = null;
        _saved = false;
        _editing = false;
        _original = "";
        _editor = "";

        switch (loaded.Kind)
        {
            case PreviewLoadKind.Text:
                _charset = loaded.Charset;
                _bom = loaded.Bom;
                _original = loaded.Text ?? "";
                _editor = _original;
                SetNotice(null);
                break;
            case PreviewLoadKind.Pdf:
                SetNotice(null);
                break;
            case PreviewLoadKind.Media:
                SetNotice("Media_Ready");
                _downloadSuggested = !_shellAllowed;
                break;
            case PreviewLoadKind.Image:
                _image = decodedImage;
                _isImage = _image is not null;
                SetNotice(_isImage ? null : "Preview_ImageFailed");
                if (!_isImage)
                    _downloadSuggested = !_shellAllowed;
                break;
            case PreviewLoadKind.TooLarge:
                SetNotice("Preview_TooLarge", ExplorerText.FormatBytes(_size));
                break;
            case PreviewLoadKind.External:
                SetNotice("Preview_External");
                break;
            case PreviewLoadKind.NotText:
                SetNotice("Preview_NotText");
                break;
            default:
                _failure = loaded.Failure;
                PublishFailure();
                break;
        }

        OnPropertyChanged(nameof(EditorText));
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(ShowFileSummary));
        UpdateStatus();
        RaiseEditorState();
    }

    private async Task SaveAsync(bool overwrite)
    {
        if (_connection is null || _connection.IsReadOnly || _readOnly
            || _bucket is null || !_isText || _saving)
            return;
        if (!overwrite && !CanSave)
            return;

        if (!overwrite && string.IsNullOrWhiteSpace(_etag))
        {
            // A normal save must never turn into an unconditional PUT.
            _conflictPending = true;
            _requiresOverwrite = false;
            RaiseBars();
            return;
        }

        var textToSave = _editor;
        _saving = true;
        RaiseEditorState();
        try
        {
            var saved = await _preview.SaveTextAsync(
                _connection, _bucket, _key, textToSave, _charset, _bom, _contentType,
                overwrite ? null : _etag, CancellationToken.None);
            if (saved.Kind == PreviewSaveKind.Conflict)
            {
                _conflictPending = true;
                _requiresOverwrite = false;
                RaiseBars();
                return;
            }
            if (saved.Kind == PreviewSaveKind.RequiresOverwrite)
            {
                _conflictPending = true;
                _requiresOverwrite = true;
                RaiseBars();
                return;
            }

            if (saved.Kind != PreviewSaveKind.Saved || saved.Head is null)
            {
                SetSaveError(saved.Failure is null
                    ? _texts["Preview_SaveFailed"]
                    : ExplorerText.FailureTitle(_texts, saved.Failure));
                return;
            }

            _etag = saved.Head.ETag;
            _size = saved.Head.SizeBytes;
            OnPropertyChanged(nameof(FileSummary));
            _original = textToSave;
            _saved = true;
            _conflictPending = false;
            _requiresOverwrite = false;
            SetSaveError("");
            UpdateStatus();
            RaiseEditorState();
            RaiseBars();
            Saved?.Invoke(this, saved.Head);
            if (_leavePending && !IsDirty)
                ResolveLeave();
        }
        finally
        {
            _saving = false;
            RaiseEditorState();
        }
    }

    private async Task ReloadAsync()
    {
        var leaving = _leavePending;
        var draft = _editor;
        var original = _original;
        var etag = _etag;
        var contentType = _contentType;
        var charset = _charset;
        var bom = _bom;
        var size = _size;
        var editing = _editing;
        var requiresOverwrite = _requiresOverwrite;
        _conflictPending = false;
        _requiresOverwrite = false;
        _leavePending = false;
        RaiseBars();
        await OpenCurrentAsync();
        if (_failed)
        {
            _editor = draft;
            _original = original;
            _etag = etag;
            _contentType = contentType;
            _charset = charset;
            _bom = bom;
            _size = size;
            _isText = true;
            _editing = editing;
            _conflictPending = true;
            _requiresOverwrite = requiresOverwrite;
            _leavePending = leaving;
            SetSaveError(_texts["Preview_ReloadFailed"]);
            OnPropertyChanged(nameof(EditorText));
            RaiseEditorState();
            RaiseBars();
            return;
        }
        if (leaving)
            LeaveResolved?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenWithShellAsync()
    {
        if (_connection is null || _bucket is null || _key.Length == 0 || !_shellAllowed || _loading)
            return;

        var connection = _connection;
        var bucket = _bucket;
        var key = _key;
        var name = _name;
        var size = _size;
        var operation = _session.Begin(new PreviewObjectId(connection.Id, bucket, key));
        var token = operation.Token;
        PreviewFileLease? shellLease = null;
        _loading = true;
        OnPropertyChanged(nameof(IsLoading));
        RaiseEditorState();
        try
        {
            shellLease = await _preview.PrepareFileAsync(
                connection, bucket, key, name, size, PreviewRules.MaxShellBytes, null, token);

            token.ThrowIfCancellationRequested();
            if (!_session.IsCurrent(operation))
                return;

            var shellPath = shellLease.FilePath;
            await Task.Run(() => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(shellPath) { UseShellExecute = true }), token);

            // The external application owns the copy after launch. It is retained under
            // the existing stale-file cleanup policy rather than deleted with this lease.
            shellLease.Detach();
            shellLease = null;

            if (!_session.IsCurrent(operation))
                return;
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Preview_ShellOpened"],
                Message = _texts["Preview_ExternalCopy"],
                Tone = ToastTone.Information
            });
        }
        catch (StorageException exception)
        {
            if (_session.IsCurrent(operation))
                SetSaveError(ExplorerText.FailureTitle(_texts, exception.Failure));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (_session.IsCurrent(operation))
                SetSaveError(_texts["Preview_ShellFailed"]);
        }
        finally
        {
            try
            {
                if (shellLease is not null)
                    await shellLease.DisposeAsync();
            }
            finally
            {
                if (_session.Complete(operation))
                {
                    _loading = false;
                    OnPropertyChanged(nameof(IsLoading));
                    RaiseEditorState();
                }
            }
        }
    }

    private void Revert()
    {
        if (!CanRevert)
            return;
        EditorText = _original;
    }

    private void DiscardLeave()
    {
        _editor = _original;
        _leavePending = false;
        _conflictPending = false;
        OnPropertyChanged(nameof(EditorText));
        RaiseEditorState();
        RaiseBars();
        ResolveLeave();
    }

    private void Stay()
    {
        _leavePending = false;
        _conflictPending = false;
        RaiseBars();
        LeaveCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void ResolveLeave()
    {
        _leavePending = false;
        RaiseBars();
        LeaveResolved?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseEditorState()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ShowReadOnlyDraft));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ShowFooter));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanBeginEdit));
        OnPropertyChanged(nameof(CanExitEdit));
        OnPropertyChanged(nameof(CanPreviewAction));
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowImage));
        OnPropertyChanged(nameof(ShowImageViewer));
        OnPropertyChanged(nameof(ShowMediaViewer));
        OnPropertyChanged(nameof(ShowPdf));
        OnPropertyChanged(nameof(ShowPreviewDownload));
        OnPropertyChanged(nameof(ShowLoadingSpinner));
        OnPropertyChanged(nameof(ShowNotice));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowSaveButton));
        OnPropertyChanged(nameof(ShowShellButton));
        OnPropertyChanged(nameof(ShowDownloadButton));
        OnPropertyChanged(nameof(ShowRetry));
        UpdateStatus();
        if (RevertCommand is RelayCommand revert)
            revert.RaiseCanExecuteChanged();
    }

    private void RaiseBars()
    {
        OnPropertyChanged(nameof(ShowLeaveBar));
        OnPropertyChanged(nameof(ShowConflictBar));
        OnPropertyChanged(nameof(ShowFooter));
        OnPropertyChanged(nameof(ShowSaveButton));
        OnPropertyChanged(nameof(ShowShellButton));
        OnPropertyChanged(nameof(ShowDownloadButton));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanBeginEdit));
        OnPropertyChanged(nameof(ConflictTitle));
        OnPropertyChanged(nameof(ConflictDetail));
    }

    private void UpdateStatus()
    {
        var key = _saving
            ? "Preview_Saving"
            : _saveError.Length > 0 && IsDirty ? "Preview_SaveFailed"
            : _readOnly && _isText
            ? "Preview_ReadOnly"
            : _saved && !IsDirty ? "Preview_Saved" : null;
        var next = key is null ? "" : _texts[key];
        if (SetProperty(ref _status, next, nameof(Status)))
            return;
    }

    private void PublishFailure()
    {
        _noticeKey = null;
        _noticeArgument = null;
        var next = _failure is null
            ? _texts["Preview_OpenFailed"]
            : ExplorerText.FailureTitle(_texts, _failure) + "\n" + ExplorerText.FailureDetail(_texts, _failure);
        SetProperty(ref _notice, next, nameof(Notice));
    }

    private void SetNotice(string? key, string? argument = null)
    {
        _noticeKey = key;
        _noticeArgument = argument;
        var next = key is null
            ? ""
            : argument is null ? _texts[key] : string.Format(_texts[key], argument);
        SetProperty(ref _notice, next, nameof(Notice));
    }

    private void SetSaveError(string value)
    {
        SetProperty(ref _saveError, value, nameof(SaveError));
        UpdateStatus();
    }
}
