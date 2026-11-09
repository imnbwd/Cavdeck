using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Connections;

public sealed class ConnectionEditorViewModel : ViewModelBase
{
    private readonly ConnectionService _connections;
    private readonly LocalizationService _texts;
    private readonly ToastService _toasts;
    private bool _loading;
    private StorageProbeResult? _lastProbe;
    private string? _lastPrefix;
    private Guid? _id;
    private StorageConnection? _originalConnection;
    private string? _originalAccessKey;
    private string _name = "";
    private StorageProviderKind _provider = StorageProviderKind.CloudflareR2;
    private string _accountId = "";
    private string _region = "";
    private string _endpoint = "";
    private string _accessKeyId = "";
    private string _secret = "";
    private bool _forcePathStyle = true;
    private bool _isReadOnly;
    private string _bucket = "";
    private string _prefix = "";
    private bool _isNew = true;
    private bool _isOpen;
    private bool _isDirty;
    private bool _showSecret;
    private bool _showAdvanced;
    private CancellationTokenSource? _operation;
    private bool _isBusy;
    private string? _busyKind;
    private bool _hasProbe;
    private string _probeLevel = "Danger";
    private string _probeTitle = "";
    private string _probeSuggestion = "";
    private string _probeTechnical = "";
    private string _nameError = "";
    private string _accountError = "";
    private string _regionError = "";
    private string _endpointError = "";
    private string _accessKeyError = "";
    private string _secretError = "";
    private string _bucketError = "";
    private string _prefixError = "";
    private string _preview = "";
    private string _providerHint = "";
    private bool _showAccount = true;
    private bool _showRegion;
    private bool _showEndpoint;
    private bool _showPathStyle;
    private bool _showHttpWarning;
    private bool _showSecretHint;

    public ConnectionEditorViewModel(
        ConnectionService connections, LocalizationService texts, ToastService toasts)
    {
        _connections = connections;
        _texts = texts;
        _toasts = toasts;
        Providers =
        [
            new ProviderChoice(StorageProviderKind.CloudflareR2),
            new ProviderChoice(StorageProviderKind.AwsS3),
            new ProviderChoice(StorageProviderKind.Minio),
            new ProviderChoice(StorageProviderKind.S3Compatible)
        ];
        SelectProviderCommand = new RelayCommand(parameter =>
        {
            if (parameter is StorageProviderKind kind)
                Provider = kind;
        });
        ToggleSecretCommand = new RelayCommand(_ => ShowSecret = !ShowSecret);
        ToggleAdvancedCommand = new RelayCommand(_ => ShowAdvanced = !ShowAdvanced);
        TestCommand = new AsyncRelayCommand(_ => TestAsync(), ReportUnexpectedUiError);
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), ReportUnexpectedUiError);
        CancelCommand = new RelayCommand(_ => Close());
        CopyDiagnosticCommand = new RelayCommand(_ => CopyDiagnostic());
        RefreshTexts();
    }

    private void ReportUnexpectedUiError(Exception exception)
    {
        var title = _texts["Ui_ActionFailed"];
        if (_toasts.Items.Any(item => !item.IsClosing && item.Title == title))
            return;
        _toasts.Show(new ToastRequest { Title = title, Tone = ToastTone.Error });
    }

    public event EventHandler<StorageConnection>? Saved;
    public Func<ConnectionChange, Task<bool>>? BeforeSaveAsync { get; set; }
    public ConnectionChange? SavedChange { get; private set; }
    public Guid? ConnectionId => _isOpen ? _id : null;

    public IReadOnlyList<ProviderChoice> Providers { get; }
    public ICommand SelectProviderCommand { get; }
    public ICommand ToggleSecretCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyDiagnosticCommand { get; }

    public bool IsOpen => _isOpen;
    public bool HasUnsavedChanges => IsOpen && _isDirty;
    public bool IsNew => _isNew;
    public bool IsBusy => _isBusy;
    public bool IsIdle => !_isBusy;
    public bool ShowSecret
    {
        get => _showSecret;
        set
        {
            if (SetProperty(ref _showSecret, value))
                OnPropertyChanged(nameof(SecretToggleLabel));
        }
    }

    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (!SetProperty(ref _showAdvanced, value))
                return;
            OnPropertyChanged(nameof(AdvancedLabel));
            if (!_loading)
                Refresh();
        }
    }
    public bool HasProbe => _hasProbe;
    public string ProbeLevel => _probeLevel;
    public string ProbeTitle => _probeTitle;
    public string ProbeSuggestion => _probeSuggestion;
    public string ProbeTechnical => _probeTechnical;
    public string NameError => _nameError;
    public string AccountError => _accountError;
    public string RegionError => _regionError;
    public string EndpointError => _endpointError;
    public string AccessKeyError => _accessKeyError;
    public string SecretError => _secretError;
    public string BucketError => _bucketError;
    public string PrefixError => _prefixError;
    public bool HasNameError => _nameError.Length > 0;
    public bool HasAccountError => _accountError.Length > 0;
    public bool HasRegionError => _regionError.Length > 0;
    public bool HasEndpointError => _endpointError.Length > 0;
    public bool HasAccessKeyError => _accessKeyError.Length > 0;
    public bool HasSecretError => _secretError.Length > 0;
    public bool HasBucketError => _bucketError.Length > 0;
    public bool HasPrefixError => _prefixError.Length > 0;
    public string Preview => _preview;
    public string ProviderHint => _providerHint;
    public bool ShowAccount => _showAccount;
    public bool ShowRegion => _showRegion;
    public bool ShowEndpoint => _showEndpoint;
    public bool ShowPathStyle => _showPathStyle;
    public bool ShowHttpWarning => _showHttpWarning;
    public bool ShowSecretHint => _showSecretHint;
    public string Title => _texts[_isNew ? "Connection_NewTitle" : "Connection_EditTitle"];
    public string SecretToggleLabel => _texts[_showSecret ? "Connection_HideSecret" : "Connection_ShowSecret"];
    public string AdvancedLabel => _texts[_showAdvanced ? "Connection_AdvancedHide" : "Connection_Advanced"];
    public string TestLabel => _texts[_busyKind == "test" ? "Connection_Testing" : "Connection_Test"];
    public string SaveLabel => _texts[_busyKind == "save" ? "Connection_Saving" : "Connection_Save"];
    public string BusyMessage => _isBusy
        ? _texts[_busyKind == "save" ? "Connection_Saving" : "Connection_Testing"]
        : "";

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnUserEdit(); }
    }

    public string AccountId
    {
        get => _accountId;
        set { if (SetProperty(ref _accountId, value)) OnUserEdit(); }
    }

    public string Region
    {
        get => _region;
        set { if (SetProperty(ref _region, value)) OnUserEdit(); }
    }

    public string Endpoint
    {
        get => _endpoint;
        set { if (SetProperty(ref _endpoint, value)) OnUserEdit(); }
    }

    public string AccessKeyId
    {
        get => _accessKeyId;
        set { if (SetProperty(ref _accessKeyId, value)) OnUserEdit(); }
    }

    public string Secret
    {
        get => _secret;
        set { if (SetProperty(ref _secret, value)) OnUserEdit(); }
    }

    public bool ForcePathStyle
    {
        get => _forcePathStyle;
        set { if (SetProperty(ref _forcePathStyle, value)) OnUserEdit(); }
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set { if (SetProperty(ref _isReadOnly, value)) OnUserEdit(); }
    }

    public string Bucket
    {
        get => _bucket;
        set { if (SetProperty(ref _bucket, value)) OnUserEdit(); }
    }

    public string Prefix
    {
        get => _prefix;
        set { if (SetProperty(ref _prefix, value)) OnUserEdit(); }
    }

    public StorageProviderKind Provider
    {
        get => _provider;
        set
        {
            if (!SetProperty(ref _provider, value))
                return;
            if (!_loading)
                ApplyProviderDefaults();
            OnUserEdit();
        }
    }

    public void RefreshTexts()
    {
        foreach (var choice in Providers)
            choice.Label = ConnectionText.ProviderName(_texts, choice.Kind);
        Refresh();
        if (_lastProbe is not null)
            ApplyProbe(_lastProbe, _lastPrefix);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SecretToggleLabel));
        OnPropertyChanged(nameof(AdvancedLabel));
        OnPropertyChanged(nameof(TestLabel));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(BusyMessage));
    }

    public void BeginCreate()
    {
        _operation?.Cancel();
        SetBusy(false, null);
        _loading = true;
        _id = null;
        _originalConnection = null;
        _originalAccessKey = null;
        _isNew = true;
        _name = "";
        _provider = StorageProviderKind.CloudflareR2;
        _accountId = "";
        _region = "";
        _endpoint = "";
        _accessKeyId = "";
        _secret = "";
        _forcePathStyle = true;
        _isReadOnly = false;
        _bucket = "";
        _prefix = "";
        _showSecret = false;
        _showAdvanced = false;
        _hasProbe = false;
        _lastProbe = null;
        ClearErrors();
        _loading = false;
        NotifyFields();
        Refresh();
        _isDirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SetOpen(true);
    }

    public void BeginEdit(StorageConnection connection, string? accessKeyId)
    {
        _operation?.Cancel();
        SetBusy(false, null);
        _loading = true;
        _id = connection.Id;
        _originalConnection = connection;
        _originalAccessKey = accessKeyId;
        _isNew = false;
        _name = connection.Name;
        _provider = connection.Provider;
        _accountId = connection.AccountId ?? "";
        _region = connection.Region;
        _endpoint = connection.Endpoint;
        _accessKeyId = accessKeyId ?? "";
        _secret = "";
        _forcePathStyle = connection.ForcePathStyle;
        _isReadOnly = connection.IsReadOnly;
        _bucket = connection.RestrictedBucket ?? "";
        _prefix = connection.KeyPrefix ?? "";
        _showSecret = false;
        _showAdvanced = connection.IsReadOnly
            || !string.IsNullOrEmpty(connection.RestrictedBucket)
            || !string.IsNullOrEmpty(connection.KeyPrefix)
            || !string.IsNullOrEmpty(connection.Endpoint);
        _hasProbe = false;
        _lastProbe = null;
        ClearErrors();
        _loading = false;
        NotifyFields();
        Refresh();
        _isDirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SetOpen(true);
    }

    public void Close()
    {
        _operation?.Cancel();
        SetBusy(false, null);
        _isDirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        _secret = "";
        _accessKeyId = "";
        _showSecret = false;
        _hasProbe = false;
        _lastProbe = null;
        OnPropertyChanged(nameof(Secret));
        OnPropertyChanged(nameof(AccessKeyId));
        OnPropertyChanged(nameof(ShowSecret));
        OnPropertyChanged(nameof(SecretToggleLabel));
        SetOpen(false);
    }

    private async Task TestAsync()
    {
        if (_isBusy)
            return;
        await RunAsync("test", async token =>
        {
            var draft = CreateDraft();
            var attempt = await _connections.TestAsync(draft, token);
            if (token.IsCancellationRequested)
                return;
            if (attempt.Issues.Count > 0)
            {
                ApplyIssues(attempt.Issues);
                return;
            }

            ApplyProbe(attempt.Probe!, draft.KeyPrefix);
        });
    }

    private async Task SaveAsync()
    {
        if (_isBusy)
            return;
        await RunAsync("save", async token =>
        {
            try
            {
                var draft = CreateDraft();
                var credentialsChanged = _originalConnection is not null
                    && (!string.IsNullOrEmpty(draft.SecretAccessKey)
                        || (draft.AccessKeyId?.Trim() ?? "") != _originalAccessKey);
                if (_originalConnection is { } original
                    && ConnectionPlanner.TryResolve(draft, out var target, out _)
                    && target is not null && BeforeSaveAsync is { } beforeSave)
                {
                    var candidate = new StorageConnection(original.Id, draft.Name.Trim(), draft.Provider,
                        target.Endpoint, target.Region, original.CredentialReference, draft.IsReadOnly,
                        target.RestrictedBucket, target.KeyPrefix, target.ForcePathStyle, target.AccountId);
                    if (!await beforeSave(new ConnectionChange(original, candidate, credentialsChanged)))
                        return;
                }
                token.ThrowIfCancellationRequested();
                var saved = await _connections.SaveAsync(draft, token, _originalConnection);
                SavedChange = new ConnectionChange(_originalConnection, saved, credentialsChanged);
                // A successful commit must reach every workspace even if the editor was closed.
                Saved?.Invoke(this, saved);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ConnectionValidationException exception)
            {
                if (!token.IsCancellationRequested)
                    ApplyIssues(exception.Issues);
            }
            catch (StorageException exception)
            {
                if (!token.IsCancellationRequested)
                    ApplyProbe(StorageProbeResult.Failed(exception.Failure), _prefix);
            }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested)
                    return;
                ApplyProbe(StorageProbeResult.Failed(new StorageFailure(
                    StorageFailureKind.Unknown, null, null, null,
                    SecretRedactor.Redact(exception.Message, [_accessKeyId, _secret]))), _prefix);
            }
        });
    }

    private async Task RunAsync(string kind, Func<CancellationToken, Task> action)
    {
        _operation?.Cancel();
        _operation = new CancellationTokenSource();
        var token = _operation.Token;
        SetBusy(true, kind);
        try
        {
            await action(token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
                SetBusy(false, null);
        }
    }

    private ConnectionDraft CreateDraft() => new()
    {
        Id = _isNew ? null : _id,
        Name = _name,
        Provider = _provider,
        AccountId = _accountId,
        Region = _region,
        Endpoint = _endpoint,
        ForcePathStyle = _forcePathStyle,
        AccessKeyId = _accessKeyId,
        SecretAccessKey = _secret,
        KeepExistingSecret = !_isNew && string.IsNullOrEmpty(_secret),
        IsReadOnly = _isReadOnly,
        RestrictedBucket = _bucket,
        KeyPrefix = _prefix
    };

    private void ApplyProviderDefaults()
    {
        _loading = true;
        if (_provider == StorageProviderKind.AwsS3
            && (string.IsNullOrWhiteSpace(_region) || _region == ConnectionPlanner.R2Region))
            _region = "us-east-1";
        else if (_provider is StorageProviderKind.Minio or StorageProviderKind.S3Compatible
            && string.IsNullOrWhiteSpace(_region))
            _region = "us-east-1";

        _forcePathStyle = _provider != StorageProviderKind.AwsS3;
        _loading = false;
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(ForcePathStyle));
    }

    private void OnUserEdit()
    {
        if (_loading)
            return;
        if (!_isDirty)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        ClearErrors();
        _hasProbe = false;
        _lastProbe = null;
        OnPropertyChanged(nameof(HasProbe));
        Refresh();
    }

    private void Refresh()
    {
        var draft = CreateDraft();
        var resolved = ConnectionPlanner.TryResolve(draft, out var target, out _);
        SetFlag(ref _showAccount, _provider == StorageProviderKind.CloudflareR2, nameof(ShowAccount));
        SetFlag(ref _showRegion, _provider != StorageProviderKind.CloudflareR2, nameof(ShowRegion));
        SetFlag(ref _showEndpoint, _provider is StorageProviderKind.Minio or StorageProviderKind.S3Compatible
            || (_provider == StorageProviderKind.AwsS3 && _showAdvanced), nameof(ShowEndpoint));
        SetFlag(ref _showPathStyle, _showAdvanced && _provider != StorageProviderKind.CloudflareR2
            && (_provider != StorageProviderKind.AwsS3 || !string.IsNullOrWhiteSpace(_endpoint)), nameof(ShowPathStyle));
        SetFlag(ref _showSecretHint, !_isNew, nameof(ShowSecretHint));
        SetFlag(ref _showHttpWarning, resolved && target!.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            nameof(ShowHttpWarning));

        var preview = !resolved || target is null
            ? _texts["Connection_PreviewEmpty"]
            : string.IsNullOrEmpty(target.Endpoint)
                ? string.Format(_texts["Connection_PreviewAws"], target.Region)
                : string.Format(_texts["Connection_Preview"], target.Endpoint, target.Region);
        if (SetProperty(ref _preview, preview))
            OnPropertyChanged(nameof(Preview));

        var hintKey = _provider switch
        {
            StorageProviderKind.AwsS3 => "Connection_ProviderHint_Aws",
            StorageProviderKind.Minio => "Connection_ProviderHint_Minio",
            StorageProviderKind.S3Compatible => "Connection_ProviderHint_Compatible",
            _ => "Connection_ProviderHint_R2"
        };
        if (SetProperty(ref _providerHint, _texts[hintKey]))
            OnPropertyChanged(nameof(ProviderHint));

        foreach (var choice in Providers)
            choice.IsSelected = choice.Kind == _provider;
    }

    private void ApplyIssues(IReadOnlyList<ConnectionIssue> issues)
    {
        ClearErrors();
        foreach (var issue in issues)
        {
            var message = ConnectionText.Issue(_texts, issue);
            switch (issue.Field)
            {
                case ConnectionField.Name: SetError(ref _nameError, message, nameof(NameError), nameof(HasNameError)); break;
                case ConnectionField.AccountId: SetError(ref _accountError, message, nameof(AccountError), nameof(HasAccountError)); break;
                case ConnectionField.Region: SetError(ref _regionError, message, nameof(RegionError), nameof(HasRegionError)); break;
                case ConnectionField.Endpoint: SetError(ref _endpointError, message, nameof(EndpointError), nameof(HasEndpointError)); break;
                case ConnectionField.AccessKeyId: SetError(ref _accessKeyError, message, nameof(AccessKeyError), nameof(HasAccessKeyError)); break;
                case ConnectionField.Secret: SetError(ref _secretError, message, nameof(SecretError), nameof(HasSecretError)); break;
                case ConnectionField.Bucket: SetError(ref _bucketError, message, nameof(BucketError), nameof(HasBucketError)); break;
                case ConnectionField.Prefix: SetError(ref _prefixError, message, nameof(PrefixError), nameof(HasPrefixError)); break;
            }
        }

        if (issues.Any(issue => issue.Field is ConnectionField.Bucket or ConnectionField.Prefix))
            ShowAdvanced = true;
    }

    private void ApplyProbe(StorageProbeResult result, string? prefix)
    {
        _lastProbe = result;
        _lastPrefix = prefix;
        _hasProbe = true;
        _probeLevel = ConnectionText.Level(result);
        _probeTitle = ConnectionText.Title(_texts, result, prefix);
        _probeSuggestion = ConnectionText.Suggestion(_texts, result);
        _probeTechnical = ConnectionText.Technical(_texts, result.Failure, includeProviderDetail: true);
        OnPropertyChanged(nameof(HasProbe));
        OnPropertyChanged(nameof(ProbeLevel));
        OnPropertyChanged(nameof(ProbeTitle));
        OnPropertyChanged(nameof(ProbeSuggestion));
        OnPropertyChanged(nameof(ProbeTechnical));
    }

    private void CopyDiagnostic()
    {
        if (!_hasProbe)
            return;
        var failure = _lastProbe?.Failure;
        // The visible probe detail can contain server supplied text. Build clipboard data
        // from typed fields only; never copy the endpoint, bucket, prefix, or raw detail.
        var report = string.Join(Environment.NewLine, new[]
        {
            "Cavdeck connection diagnostic",
            $"Provider: {_provider}",
            $"Result: {(_lastProbe?.Succeeded == true ? "Success" : "Failed")}",
            failure is null ? null : $"Category: {failure.Kind}",
            failure?.StatusCode is int status ? $"HTTP status: {status}" : null,
            SafeIdentifier(failure?.ErrorCode) is string code ? $"S3 error code: {code}" : null,
            SafeIdentifier(failure?.RequestId) is string requestId ? $"Request ID: {requestId}" : null
        }.Where(line => line is not null));
        try
        {
            Clipboard.SetText(report);
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Connection_Copied"],
                Tone = ToastTone.Success
            });
        }
        catch (Exception)
        {
            _toasts.Show(new ToastRequest
            {
                Title = _texts["Connection_CopyFailed"],
                Tone = ToastTone.Warning
            });
        }
    }

    private static string? SafeIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        Regex.IsMatch(value, "\\A[A-Za-z0-9_-]+\\z", RegexOptions.CultureInvariant)
            ? value : null;

    private void ClearErrors()
    {
        SetError(ref _nameError, "", nameof(NameError), nameof(HasNameError));
        SetError(ref _accountError, "", nameof(AccountError), nameof(HasAccountError));
        SetError(ref _regionError, "", nameof(RegionError), nameof(HasRegionError));
        SetError(ref _endpointError, "", nameof(EndpointError), nameof(HasEndpointError));
        SetError(ref _accessKeyError, "", nameof(AccessKeyError), nameof(HasAccessKeyError));
        SetError(ref _secretError, "", nameof(SecretError), nameof(HasSecretError));
        SetError(ref _bucketError, "", nameof(BucketError), nameof(HasBucketError));
        SetError(ref _prefixError, "", nameof(PrefixError), nameof(HasPrefixError));
    }

    private void SetError(ref string field, string value, string name, string hasName)
    {
        if (!SetProperty(ref field, value, name))
            return;
        OnPropertyChanged(hasName);
    }

    private void SetBusy(bool busy, string? kind)
    {
        _busyKind = busy ? kind : null;
        if (!SetProperty(ref _isBusy, busy, nameof(IsBusy)))
        {
            OnPropertyChanged(nameof(TestLabel));
            OnPropertyChanged(nameof(SaveLabel));
            OnPropertyChanged(nameof(BusyMessage));
            return;
        }
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(TestLabel));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(BusyMessage));
    }

    private void SetOpen(bool open)
    {
        if (SetProperty(ref _isOpen, open, nameof(IsOpen)))
            OnPropertyChanged(nameof(Title));
    }

    private void NotifyFields()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccountId));
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(AccessKeyId));
        OnPropertyChanged(nameof(Secret));
        OnPropertyChanged(nameof(ForcePathStyle));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(Bucket));
        OnPropertyChanged(nameof(Prefix));
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(ShowSecret));
        OnPropertyChanged(nameof(ShowAdvanced));
        OnPropertyChanged(nameof(SecretToggleLabel));
        OnPropertyChanged(nameof(AdvancedLabel));
        OnPropertyChanged(nameof(HasProbe));
    }

    private bool SetFlag(ref bool field, bool value, string name) => SetProperty(ref field, value, name);
}

public sealed class ProviderChoice : ViewModelBase
{
    private string _label = "";
    private bool _isSelected;

    public ProviderChoice(StorageProviderKind kind) => Kind = kind;

    public StorageProviderKind Kind { get; }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
