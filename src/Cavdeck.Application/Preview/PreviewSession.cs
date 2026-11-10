namespace Cavdeck.Application.Preview;

public readonly record struct PreviewObjectId(Guid ConnectionId, string Bucket, string Key);

/// <summary>Coordinates the active preview request for one Explorer workspace.</summary>
public sealed class PreviewSession : IDisposable
{
    private readonly object _gate = new();
    private Operation? _current;
    private long _generation;
    private bool _disposed;

    public Operation Begin(PreviewObjectId target)
    {
        if (target.ConnectionId == Guid.Empty)
            throw new ArgumentException("A connection is required.", nameof(target));
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Key);

        Operation? previous;
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            operation = new Operation(target, ++_generation);
            _current = operation;
        }

        // The previous operation owns its token source until its async work completes.
        previous?.Cancel();
        return operation;
    }

    public bool IsCurrent(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
            return !_disposed && ReferenceEquals(_current, operation);
    }

    /// <summary>Releases the operation token source and reports whether it was current.</summary>
    public bool Complete(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = ReferenceEquals(_current, operation);
            if (wasCurrent)
                _current = null;
        }

        operation.Dispose();
        return wasCurrent;
    }

    /// <summary>Requests cancellation without invalidating the current operation.</summary>
    public void CancelCurrent()
    {
        Operation? current;
        lock (_gate)
            current = _current;
        current?.Cancel();
    }

    /// <summary>Invalidates and cancels the current operation.</summary>
    public void Reset()
    {
        Operation? current;
        lock (_gate)
        {
            if (_disposed)
                return;
            _generation++;
            current = _current;
            _current = null;
        }
        current?.Cancel();
    }

    public void Dispose()
    {
        Operation? current;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
            current = _current;
            _current = null;
        }
        current?.Cancel();
    }

    public sealed class Operation
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source;

        internal Operation(PreviewObjectId target, long generation)
        {
            Target = target;
            Generation = generation;
            _source = new CancellationTokenSource();
            Token = _source.Token;
        }

        public PreviewObjectId Target { get; }
        public long Generation { get; }
        public CancellationToken Token { get; }
        public bool IsCancellationRequested => Token.IsCancellationRequested;

        internal void Cancel()
        {
            lock (_gate)
                _source?.Cancel();
        }

        internal void Dispose()
        {
            lock (_gate)
            {
                _source?.Dispose();
                _source = null;
            }
        }
    }
}
