namespace WindowVeil.Editor.Model;

/// <summary>
/// Runs an asynchronous handler for the most recent posted value only: while a call is in flight, newer values replace
/// each other and only the last one runs afterwards. Used to throttle hit testing and highlight updates (at most one
/// pending query; intermediate mouse moves are dropped). Thread-safe; the handler never runs concurrently with itself.
/// </summary>
public sealed class LatestOnlyWorker<T>
{
    private readonly Func<T, CancellationToken, Task> _handler;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private bool _hasPending;
    private T _pending = default!;
    private Task _running = Task.CompletedTask;
    private bool _active;
    private CancellationTokenSource _cts = new();

    /// <summary>Creates a worker.</summary>
    public LatestOnlyWorker(Func<T, CancellationToken, Task> handler, Action<Exception>? onError = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _onError = onError;
    }

    /// <summary>Posts a value. Returns immediately.</summary>
    public void Post(T value)
    {
        lock (_gate)
        {
            _pending = value;
            _hasPending = true;
            if (_active) return;
            _active = true;
            _running = RunAsync(_cts.Token);
        }
    }

    /// <summary>Drops any pending value, cancels the running call and waits for it to finish.</summary>
    public async Task CancelAsync()
    {
        Task running;
        CancellationTokenSource old;
        lock (_gate)
        {
            _hasPending = false;
            _pending = default!;
            running = _running;
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        await old.CancelAsync().ConfigureAwait(false);
        try { await running.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        old.Dispose();
    }

    /// <summary>Waits until every posted value has been processed.</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task running;
            lock (_gate)
            {
                if (!_active) return;
                running = _running;
            }
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (!_hasPending) { _active = false; return; }
                // A value posted after CancelAsync runs with the new token.
                if (ct.IsCancellationRequested) ct = _cts.Token;
                value = _pending;
                _pending = default!;
                _hasPending = false;
            }
            try
            {
                await _handler(value, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }
    }
}
