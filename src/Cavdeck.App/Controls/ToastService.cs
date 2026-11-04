using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Localization;

namespace Cavdeck.App.Controls;

public enum ToastTone
{
    Information,
    Success,
    Warning,
    Error
}

public enum ToastPriority
{
    Normal,
    High
}

public sealed record ToastRequest
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public ToastTone Tone { get; init; } = ToastTone.Information;
    // Errors are high priority by default; callers may override this when appropriate.
    public ToastPriority? Priority { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ActionLabel { get; init; }
    // Optional resource key for actions that must refresh when the language changes.
    public string? ActionLabelKey { get; init; }
    public Action? Action { get; init; }
}

public sealed class ToastItem : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer? _timer;
    private readonly LocalizationService _texts;
    private readonly string? _actionLabelKey;
    private readonly string? _literalActionLabel;
    private DispatcherTimer? _closeTimer;
    private TimeSpan _remaining;
    private long _startedAt;
    private bool _paused;
    private bool _closing;
    private bool _closed;
    private bool _disposed;
    private bool _actionInvoked;
    private bool _announced;
    private ToastCopyState _copyState;

    internal ToastItem(string title, string? message, ToastTone tone, ToastPriority priority,
        LocalizationService texts, TimeSpan? duration, string? actionLabel = null,
        Action? action = null, string? actionLabelKey = null,
        Action<ToastItem>? copyDiagnostics = null)
    {
        Title = title;
        Message = message;
        Tone = tone;
        Priority = priority;
        _texts = texts;
        _literalActionLabel = actionLabel;
        _actionLabelKey = actionLabelKey;
        DismissCommand = new ToastCommand(Dismiss);
        if (action is not null &&
            (!string.IsNullOrWhiteSpace(actionLabelKey) || !string.IsNullOrWhiteSpace(actionLabel)))
            ActionCommand = new ToastCommand(() => ExecuteAction(action));

        if (tone == ToastTone.Error && copyDiagnostics is not null)
            CopyCommand = new ToastCommand(() => copyDiagnostics(this));

        RefreshLocalizedLabels();

        if (duration is null || duration == Timeout.InfiniteTimeSpan || duration == TimeSpan.MaxValue)
            return;

        _remaining = duration.Value > TimeSpan.Zero ? duration.Value : TimeSpan.FromMilliseconds(1);
        _timer = new DispatcherTimer();
        _timer.Tick += OnTimeout;
        StartTimeout();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Closed;

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string? Message { get; }
    public ToastTone Tone { get; }
    public ToastPriority Priority { get; }
    public ICommand DismissCommand { get; }
    public ICommand? ActionCommand { get; }
    public ICommand? CopyCommand { get; }
    public bool HasAction => ActionCommand is not null;
    public bool HasCopy => CopyCommand is not null;
    public string? ActionLabel { get; private set; }
    public string CloseLabel { get; private set; } = "";
    public string CloseTooltip { get; private set; } = "";
    public string AccessibleAnnouncement { get; private set; } = "";
    public string CopyLabel { get; private set; } = "";
    public string CopyHelpText { get; private set; } = "";
    public string CopyFeedback { get; private set; } = "";
    public bool IsClosing => _closing;

    internal bool IsDisposed => _disposed;
    internal bool IsTimeoutActive => _timer?.IsEnabled == true;

    // The loaded event can fire again after layout or template changes.
    internal bool TryMarkAnnounced()
    {
        if (_announced || _closing || _disposed)
            return false;
        _announced = true;
        return true;
    }

    public void Pause()
    {
        if (_timer is null || _disposed || _closing || _paused || !_timer.IsEnabled)
            return;

        _timer.Stop();
        _remaining -= Stopwatch.GetElapsedTime(_startedAt);
        if (_remaining < TimeSpan.Zero)
            _remaining = TimeSpan.Zero;
        _paused = true;
    }

    public void Resume()
    {
        if (_timer is null || _disposed || _closing || !_paused)
            return;

        if (_remaining <= TimeSpan.Zero)
        {
            Dismiss();
            return;
        }

        StartTimeout();
    }

    public void RefreshLocalizedLabels()
    {
        if (_disposed)
            return;

        CloseTooltip = _texts["Toast_Dismiss"];
        CloseLabel = string.Format(_texts["Toast_DismissNamed"], Title);
        ActionLabel = !string.IsNullOrWhiteSpace(_actionLabelKey)
            ? _texts[_actionLabelKey]
            : _literalActionLabel;

        var severity = _texts[Tone switch
        {
            ToastTone.Success => "Toast_ToneSuccess",
            ToastTone.Warning => "Toast_ToneWarning",
            ToastTone.Error => "Toast_ToneError",
            _ => "Toast_ToneInformation"
        }];
        AccessibleAnnouncement = string.IsNullOrWhiteSpace(Message)
            ? string.Join(" — ", severity, Title)
            : string.Join(" — ", severity, Title, Message);

        CopyHelpText = _texts["Toast_CopyHelpText"];
        CopyLabel = _texts["Toast_CopyDiagnostics"];
        CopyFeedback = _copyState switch
        {
            ToastCopyState.Success => _texts["Toast_Copied"],
            ToastCopyState.Failed => _texts["Diagnostics_CopyFailed"],
            _ => ""
        };

        OnPropertyChanged(nameof(CloseTooltip));
        OnPropertyChanged(nameof(CloseLabel));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(AccessibleAnnouncement));
        OnPropertyChanged(nameof(CopyHelpText));
        OnPropertyChanged(nameof(CopyLabel));
        OnPropertyChanged(nameof(CopyFeedback));
    }

    internal void ReportCopyResult(bool copied)
    {
        if (_disposed || _closing)
            return;

        _copyState = copied ? ToastCopyState.Success : ToastCopyState.Failed;
        RefreshLocalizedLabels();
    }

    public void Dismiss()
    {
        if (_closing || _disposed)
            return;

        _closing = true;
        StopTimeout();
        OnPropertyChanged(nameof(IsClosing));

        var delay = Motion.Duration(TimeSpan.FromMilliseconds(120));
        if (delay == TimeSpan.Zero)
        {
            NotifyClosed();
            return;
        }

        _closeTimer = new DispatcherTimer { Interval = delay };
        _closeTimer.Tick += OnCloseTimerTick;
        _closeTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopTimeout();
        if (_timer is not null)
            _timer.Tick -= OnTimeout;

        if (_closeTimer is not null)
        {
            _closeTimer.Stop();
            _closeTimer.Tick -= OnCloseTimerTick;
            _closeTimer = null;
        }

        Closed = null;
        PropertyChanged = null;
    }

    private void StartTimeout()
    {
        if (_timer is null)
            return;

        _paused = false;
        _startedAt = Stopwatch.GetTimestamp();
        _timer.Interval = _remaining;
        _timer.Start();
    }

    private void StopTimeout() => _timer?.Stop();

    private void OnTimeout(object? sender, EventArgs e) => Dismiss();

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        if (_closeTimer is not null)
        {
            _closeTimer.Stop();
            _closeTimer.Tick -= OnCloseTimerTick;
            _closeTimer = null;
        }
        NotifyClosed();
    }

    private void NotifyClosed()
    {
        if (_closed || _disposed)
            return;

        _closed = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteAction(Action action)
    {
        if (_disposed || _closing || _actionInvoked)
            return;

        _actionInvoked = true;
        Dismiss();
        try
        {
            action();
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Queue("ui", error);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private enum ToastCopyState
    {
        None,
        Success,
        Failed
    }

    private sealed class ToastCommand : ICommand
    {
        private readonly Action _execute;
        public ToastCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}

public sealed class ToastService : IDisposable
{
    private const int MaxVisible = 4;
    private readonly LocalizationService _texts;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _copyText;
    private readonly Func<string> _createDiagnosticReport;
    private volatile bool _disposed;

    public ToastService(LocalizationService texts)
        : this(texts, Clipboard.SetText, () => DiagnosticLog.Current.CreateReport()) { }

    // Allow unit tests to exercise copy success/failure without modifying the OS clipboard.
    internal ToastService(LocalizationService texts, Action<string> copyText,
        Func<string> createDiagnosticReport)
    {
        _texts = texts;
        _copyText = copyText;
        _createDiagnosticReport = createDiagnosticReport;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _texts.PropertyChanged += OnTextsChanged;
    }

    public ObservableCollection<ToastItem> Items { get; } = new();

    public void Show(ToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RunOnUi(() => ShowCore(request));
    }

    public void Dismiss(Guid id) =>
        RunOnUi(() => Items.FirstOrDefault(item => item.Id == id)?.Dismiss());

    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            RunOnUi(Dispose);
            return;
        }

        if (_disposed)
            return;

        _disposed = true;
        _texts.PropertyChanged -= OnTextsChanged;
        foreach (var item in Items.ToArray())
            RemoveItem(item);
    }

    private void ShowCore(ToastRequest request)
    {
        var priority = request.Priority ??
            (request.Tone == ToastTone.Error ? ToastPriority.High : ToastPriority.Normal);

        if (Items.Count >= MaxVisible)
        {
            var victim = Items.FirstOrDefault(item => item.IsClosing)
                ?? Items.FirstOrDefault(item => item.Priority == ToastPriority.Normal);
            if (victim is null)
            {
                if (priority == ToastPriority.Normal)
                    return;
                victim = Items[0];
            }

            RemoveItem(victim);
        }

        var item = new ToastItem(
            request.Title, request.Message, request.Tone, priority, _texts,
            request.Duration ?? DefaultDuration(request.Tone),
            request.ActionLabel, request.Action, request.ActionLabelKey,
            request.Tone == ToastTone.Error ? CopyDiagnostics : null);
        item.Closed += OnItemClosed;
        Items.Add(item);
    }

    private void CopyDiagnostics(ToastItem item)
    {
        if (_disposed || item.IsDisposed || item.IsClosing)
            return;

        try
        {
            // Deliberately copy the existing sanitized report, never Toast text
            // or exception messages (which can contain file names or signed URLs).
            _copyText(_createDiagnosticReport());
            item.ReportCopyResult(copied: true);
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Queue("copy-diagnostic", error);
            item.ReportCopyResult(copied: false);
        }
    }

    private void OnItemClosed(object? sender, EventArgs e)
    {
        if (sender is ToastItem item)
            RemoveItem(item);
    }

    private void RemoveItem(ToastItem item)
    {
        item.Closed -= OnItemClosed;
        item.Dispose();
        Items.Remove(item);
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e) =>
        RunOnUi(() =>
        {
            foreach (var item in Items)
                item.RefreshLocalizedLabels();
        });

    private void RunOnUi(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return;

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted ||
                                                _dispatcher.HasShutdownFinished)
        {
            // The application is exiting; no Toast can be displayed.
        }
    }

    internal static TimeSpan DefaultDuration(ToastTone tone) => tone switch
    {
        ToastTone.Error => TimeSpan.FromSeconds(7),
        ToastTone.Warning => TimeSpan.FromSeconds(5.5),
        _ => TimeSpan.FromSeconds(4)
    };
}
