using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Localization;
using Cavdeck.Application.Explorer;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public partial class FolderPropertiesView : UserControl, IDisposable
{
    private readonly Func<Action<FolderProperties>, CancellationToken, Task<FolderProperties>> _scan;
    private readonly Action<string> _copy;
    private CancellationTokenSource? _operation;
    private FolderProperties? _result;
    private bool _disposed;
    private bool _started;

    public LocalizationService Texts { get; }
    public string ScopeName { get; }
    public string ConnectionName { get; }
    public string Location { get; }

    public FolderPropertiesView(LocalizationService texts, string name, string connection, string location,
        Func<Action<FolderProperties>, CancellationToken, Task<FolderProperties>> scan, Action<string> copy)
    {
        Texts = texts;
        ScopeName = name;
        ConnectionName = connection;
        Location = location;
        _scan = scan;
        _copy = copy;
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) =>
        {
            if (_started || _disposed) return;
            _started = true;
            await ScanAsync();
        };
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _operation?.Cancel();
    }

    private async Task ScanAsync()
    {
        if (_disposed || _operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _result = null;
        CopyButton.IsEnabled = false;
        ScanButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        ScanProgress.Visibility = Visibility.Visible;
        var latest = new FolderProperties(0, 0, 0, 0);
        ShowStats(latest);
        StatusText.Text = Texts["FolderProperties_Scanning"];
        // Await on the UI thread, but scan and aggregate off it. Progress is coalesced by
        // the dispatcher so large buckets cannot flood the UI with page updates.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (_disposed || operation.IsCancellationRequested) return;
            var snapshot = Volatile.Read(ref latest);
            ShowStats(snapshot);
            StatusText.Text = string.Format(Texts["FolderProperties_Progress"], snapshot.ListRequests);
        };
        timer.Start();
        try
        {
            var result = await Task.Run(() => _scan(value => Volatile.Write(ref latest, value), operation.Token),
                operation.Token);
            if (_disposed) return;
            operation.Token.ThrowIfCancellationRequested();
            _result = result;
            ShowStats(result);
            StatusText.Text = string.Format(Texts["FolderProperties_Complete"], result.ListRequests);
            CopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_disposed)
            {
                ShowStats(Volatile.Read(ref latest));
                StatusText.Text = Texts["FolderProperties_Cancelled"];
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue("folder-properties", exception);
            if (!_disposed)
            {
                ShowStats(Volatile.Read(ref latest));
                StatusText.Text = Texts["FolderProperties_Failed"] +
                    (exception is StorageException storage ? "\n" + ExplorerText.FailureTitle(Texts, storage.Failure) : "");
            }
        }
        finally
        {
            timer.Stop();
            _operation = null;
            if (!_disposed)
            {
                ScanProgress.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                ScanButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void ShowStats(FolderProperties value)
    {
        SizeText.Text = ExplorerText.FormatBytes(value.SizeBytes);
        ExactSizeText.Text = string.Format(Texts["FolderProperties_Bytes"], value.SizeBytes);
        FilesText.Text = value.Files.ToString("N0", CultureInfo.CurrentCulture);
        FoldersText.Text = value.Subfolders.ToString("N0", CultureInfo.CurrentCulture);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not { } value) return;
        _copy(string.Join(Environment.NewLine, ScopeName, ConnectionName, Location,
            Texts["FolderProperties_TotalSize"] + ": " + ExplorerText.FormatBytes(value.SizeBytes)
                + " (" + string.Format(Texts["FolderProperties_Bytes"], value.SizeBytes) + ")",
            Texts["FolderProperties_Files"] + ": " + FilesText.Text,
            Texts["FolderProperties_Subfolders"] + ": " + FoldersText.Text,
            Texts["FolderProperties_Hint"]));
    }

    public void Dispose()
    {
        _disposed = true;
        _operation?.Cancel();
    }
}
