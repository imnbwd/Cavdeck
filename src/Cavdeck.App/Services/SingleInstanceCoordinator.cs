using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Cavdeck.App.Features.Diagnostics;

namespace Cavdeck.App.Services;

// The mutex prevents a second engine; the event carries only a wake-up signal.
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset);
    private EventWaitHandle? _activation;
    private Task? _listener;
    private bool _owns;

    public SingleInstanceCoordinator(string? scopeOverride = null)
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var scope = scopeOverride ?? $"{identity}:{Process.GetCurrentProcess().SessionId}";
        _name = "Cavdeck-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
        _mutex = new Mutex(false, "Local\\" + _name);
        try { _owns = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _owns = true; }
    }

    public bool IsPrimary => _owns;

    public void Listen(Action activate)
    {
        if (!_owns) throw new InvalidOperationException("Only the primary instance can listen.");
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + _name + "-activate");
        _listener = Task.Run(() =>
        {
            while (WaitHandle.WaitAny([_activation, _stop]) == 0)
            {
                try { activate(); }
                catch (Exception ex) { DiagnosticLog.Current.Queue("instance-activation", ex); }
            }
        });
    }

    public async Task<bool> ActivatePrimaryAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting("Local\\" + _name + "-activate");
                return signal.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                if (timeout.IsCancellationRequested) break;
                try { await Task.Delay(150, timeout.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        return false;
    }

    public void Dispose()
    {
        _stop.Set();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException ex) { DiagnosticLog.Current.Queue("instance-activation", ex); }
        _activation?.Dispose();
        _stop.Dispose();
        if (_owns)
        {
            _mutex.ReleaseMutex();
            _owns = false;
        }
        _mutex.Dispose();
    }
}
