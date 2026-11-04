using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Settings;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.App.Features.Explorer;

public partial class CopyDestinationPicker : UserControl, IDialogSubmitState, IDisposable
{
    private readonly ExplorerService _explorer;
    private readonly LocalizationService _texts;
    private readonly CancellationTokenSource _stop = new();
    private int _generation;
    private string? _continuation;
    private bool _busy;
    private bool _browseError;
    private string _prefix = "";

    public CopyDestinationPicker(ExplorerService explorer, LocalizationService texts,
        IReadOnlyList<StorageConnection> connections, int fileCount, int folderCount,
        TransferConflictPreference defaultConflict)
    {
        _explorer = explorer;
        _texts = texts;
        InitializeComponent();
        SummaryText.Text = string.Format(texts["Explorer_CopyToSummary"], fileCount, folderCount);
        ConnectionLabel.Text = texts["Explorer_CopyToConnection"];
        BucketLabel.Text = texts["Explorer_CopyToBucket"];
        FolderLabel.Text = texts["Explorer_CopyToFolder"];
        ConflictLabel.Text = texts["Explorer_CopyToConflict"];
        UpButton.Content = texts["Explorer_CopyToUp"];
        MoreButton.Content = texts["Explorer_LoadMore"];
        RetryButton.Content = texts["Explorer_Retry"];
        Conflicts.Items.Add(texts["Transfer_Skip"]);
        Conflicts.Items.Add(texts["Transfer_Rename"]);
        Conflicts.Items.Add(texts["Transfer_Overwrite"]);
        Conflicts.SelectedIndex = defaultConflict == TransferConflictPreference.Skip ? 0 : 1;
        RelayHint.Text = texts["Explorer_CopyToRelayHint"];
        var choices = connections.Select(connection => new DestinationOption(connection,
            connection.Name, connection.Provider.ToString(), connection.IsReadOnly,
            connection.IsReadOnly ? texts["Transfer_ReadOnly"] : "")).ToList();
        Connections.ItemsSource = choices;
        AutomationProperties.SetName(Connections, ConnectionLabel.Text);
        AutomationProperties.SetName(Buckets, BucketLabel.Text);
        AutomationProperties.SetName(Folders, FolderLabel.Text);
        AutomationProperties.SetName(Conflicts, ConflictLabel.Text);
        AutomationProperties.SetName(UpButton, UpButton.Content.ToString()!);
        AutomationProperties.SetName(MoreButton, MoreButton.Content.ToString()!);
        AutomationProperties.SetName(RetryButton, RetryButton.Content.ToString()!);
        StatusText.Text = Connections.Items.Count == 0 ? texts["Explorer_CopyToNoWritable"] : "";
        Connections.SelectedItem = choices.FirstOrDefault(choice => !choice.IsReadOnly)
            ?? choices.FirstOrDefault();
        UpdateState();
    }

    public event EventHandler? CanSubmitChanged;
    public StorageConnection? Destination => (Connections.SelectedItem as DestinationOption)?.Connection;
    public string? Bucket => (Buckets.SelectedItem as StorageBucket)?.Name;
    public string Prefix => _prefix;
    public TransferConflictPolicy Conflict => Conflicts.SelectedIndex switch
    {
        1 => TransferConflictPolicy.Rename,
        2 => TransferConflictPolicy.Overwrite,
        _ => TransferConflictPolicy.Skip
    };
    public bool CanSubmit => !_busy && !_browseError && Destination is { IsReadOnly: false } && Bucket is not null;

    public void Dispose() => _stop.Cancel();

    public sealed record DestinationOption(StorageConnection Connection, string Name,
        string Provider, bool IsReadOnly, string ReadOnlyText);

    private async void Connections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var generation = ++_generation;
        Buckets.ItemsSource = null;
        Folders.Items.Clear();
        _prefix = "";
        _continuation = null;
        _browseError = false;
        UpdateState();
        if (Destination is not { } connection)
            return;
        Connections.ToolTip = connection.Name;
        if (connection.IsReadOnly)
        {
            StatusText.Text = _texts["Transfer_ReadOnly"];
            UpdateState();
            return;
        }
        await LoadBucketsAsync(connection, generation);
    }

    private async Task LoadBucketsAsync(StorageConnection connection, int generation)
    {
        SetBusy(true, _texts["Explorer_Loading"]);
        try
        {
            var buckets = connection.RestrictedBucket is { Length: > 0 } restricted
                ? [new StorageBucket(restricted, null)]
                : await _explorer.ListBucketsAsync(connection, _stop.Token);
            if (generation != _generation || _stop.IsCancellationRequested)
                return;
            Buckets.ItemsSource = buckets;
            Buckets.SelectedIndex = buckets.Count > 0 ? 0 : -1;
            StatusText.Text = buckets.Count == 0 ? _texts["Explorer_CopyToNoBuckets"] : "";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _generation)
            {
                _browseError = true;
                StatusText.Text = error is StorageException storage
                    ? ExplorerText.FailureTitle(_texts, storage.Failure)
                    : _texts["Transfer_FailureUnknown"];
            }
        }
        finally
        {
            if (generation == _generation)
                SetBusy(false, StatusText.Text);
        }
    }

    private async void Buckets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Buckets.ToolTip = Bucket;
        _prefix = Destination is { } destination ? ObjectLayout.RootPrefix(destination) : "";
        _continuation = null;
        _browseError = false;
        Folders.Items.Clear();
        UpdateState();
        if (Destination is not null && Bucket is not null)
            await BrowseAsync(false);
    }

    private async Task BrowseAsync(bool append)
    {
        if (Destination is not { } connection || Bucket is not { } bucket)
            return;
        var generation = ++_generation;
        var prefix = _prefix;
        _browseError = false;
        SetBusy(true, _texts["Explorer_Loading"]);
        try
        {
            var page = await _explorer.BrowseAsync(connection, bucket, prefix,
                append ? _continuation : null, _stop.Token);
            if (generation != _generation || _stop.IsCancellationRequested)
                return;
            if (!append)
                Folders.Items.Clear();
            foreach (var folder in page.Entries.Where(entry => entry.Kind == ObjectEntryKind.Folder))
                Folders.Items.Add(folder);
            Folders.DisplayMemberPath = nameof(ObjectEntry.Name);
            _continuation = page.ContinuationToken;
            StatusText.Text = Folders.Items.Count == 0 ? _texts["Explorer_CopyToEmpty"] : "";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _generation)
            {
                _browseError = true;
                StatusText.Text = error is StorageException storage
                    ? ExplorerText.FailureTitle(_texts, storage.Failure)
                    : _texts["Transfer_FailureUnknown"];
            }
        }
        finally
        {
            if (generation == _generation)
                SetBusy(false, StatusText.Text);
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        var root = Destination is { } destination ? ObjectLayout.RootPrefix(destination) : "";
        var parent = ObjectLayout.ParentPrefix(_prefix);
        if (parent is null || !parent.StartsWith(root, StringComparison.Ordinal))
            return;
        _prefix = parent;
        _continuation = null;
        await BrowseAsync(false);
    }

    private async void Folders_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            ItemsControl.ContainerFromElement(Folders, e.OriginalSource as DependencyObject) is not ListBoxItem
                { DataContext: ObjectEntry folder })
            return;
        e.Handled = true;
        await OpenFolderAsync(folder);
    }
    private async void Folders_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await OpenFolderAsync();
    }

    private async Task OpenFolderAsync(ObjectEntry? chosen = null)
    {
        if ((chosen ?? Folders.SelectedItem) is not ObjectEntry folder)
            return;
        _prefix = ObjectLayout.NormalizePrefix(folder.Key);
        _continuation = null;
        await BrowseAsync(false);
    }

    private async void More_Click(object sender, RoutedEventArgs e) => await BrowseAsync(true);

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_browseError || Destination is not { IsReadOnly: false } destination)
            return;
        _browseError = false;
        if (Bucket is null)
            await LoadBucketsAsync(destination, ++_generation);
        else
            await BrowseAsync(false);
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        StatusText.Text = status;
        UpdateState();
    }

    private void UpdateState()
    {
        PrefixText.Text = _prefix.Length == 0 ? "/" : "/" + _prefix;
        PrefixText.ToolTip = PrefixText.Text;
        UpButton.IsEnabled = !_busy && ObjectLayout.ParentPrefix(_prefix) is { } parent
            && Destination is { } destination && parent.StartsWith(ObjectLayout.RootPrefix(destination), StringComparison.Ordinal);
        MoreButton.Visibility = _continuation is null ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.IsEnabled = !_busy;
        RetryButton.Visibility = _browseError ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !_busy;
        CanSubmitChanged?.Invoke(this, EventArgs.Empty);
    }
}
