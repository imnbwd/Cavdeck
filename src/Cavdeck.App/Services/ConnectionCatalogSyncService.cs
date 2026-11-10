using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.Application.Connections;
using Cavdeck.Infrastructure.Connections;

namespace Cavdeck.App.Services;

/// <summary>
/// Keeps the Desktop connection list in sync with the shared catalog.
/// File events are only wake-up signals; the catalog remains the source of truth.
/// </summary>
public sealed class ConnectionCatalogSyncService : IDisposable
{
    private readonly JsonConnectionCatalog _catalog;
    private readonly ConnectionService _connections;
    private readonly ConnectionsViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _pendingRefresh;
    private bool _disposed;

    public ConnectionCatalogSyncService(
        JsonConnectionCatalog catalog,
        ConnectionService connections,
        ConnectionsViewModel viewModel)
    {
        _catalog = catalog;
        _connections = connections;
        _viewModel = viewModel;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
                return;

            try
            {
                var directory = Path.GetDirectoryName(_catalog.FilePath)!;
                Directory.CreateDirectory(directory);

                var watcher = new FileSystemWatcher(directory, Path.GetFileName(_catalog.FilePath))
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = false
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                _watcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception)
            {
                DiagnosticLog.Current.Queue("connection-catalog-watch", exception);
                return;
            }
        }

        // Cover changes that happened between the initial load and watcher startup.
        ScheduleRefresh();
    }

    public void Refresh() => ScheduleRefresh();

    private void OnChanged(object sender, FileSystemEventArgs args) => ScheduleRefresh();

    private void OnRenamed(object sender, RenamedEventArgs args) => ScheduleRefresh();

    private void OnError(object sender, ErrorEventArgs args)
    {
        DiagnosticLog.Current.Queue("connection-catalog-watch", args.GetException());
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        CancellationTokenSource pending;
        lock (_gate)
        {
            if (_disposed)
                return;

            _pendingRefresh?.Cancel();
            pending = new CancellationTokenSource();
            _pendingRefresh = pending;
        }

        _ = RefreshAfterDelayAsync(pending);
    }

    private async Task RefreshAfterDelayAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), pending.Token).ConfigureAwait(false);
            await _refreshGate.WaitAsync(pending.Token).ConfigureAwait(false);
            try
            {
                // The catalog is atomically replaced, so a normal read sees either
                // the previous complete document or the latest complete document.
                var connections = await _connections.ListAsync(pending.Token).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(
                    () => _viewModel.ReconcileExternal(connections),
                    DispatcherPriority.DataBind,
                    pending.Token).Task.ConfigureAwait(false);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            // A newer file event superseded this refresh.
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue("connection-catalog-sync", exception);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingRefresh, pending))
                    _pendingRefresh = null;
            }
            pending.Dispose();
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _pendingRefresh?.Cancel();
            _pendingRefresh = null;
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
    }
}
