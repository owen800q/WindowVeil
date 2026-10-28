using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WindowVeil.Automation;

/// <summary>
/// A dedicated background MTA thread with a work queue on which all UI Automation client calls run.
/// </summary>
/// <remarks>
/// <para>
/// UI Automation clients must not run on a UI (STA) thread: a UIA call that reaches a provider in the calling
/// process (or that waits on a hung target) would block or dead-lock the message loop. Every WindowVeil UIA call is
/// therefore marshalled to this thread.
/// </para>
/// <para>
/// The thread's DPI awareness context is set to <c>PER_MONITOR_AWARE_V2</c> (falling back to <c>PER_MONITOR_AWARE</c>),
/// because UI Automation returns coordinates in the caller's DPI context: only a per-monitor aware caller receives
/// physical screen pixels for <c>BoundingRectangle</c>, text ranges and <c>FromPoint</c>.
/// </para>
/// <para>
/// Work items run one at a time in FIFO order. When a caller's token is cancelled the returned task is cancelled
/// immediately, even if the work item is still blocked inside a UIA call (the call itself cannot be interrupted;
/// the thread continues with the next item once it returns).
/// </para>
/// </remarks>
public sealed class UiAutomationThread : IDisposable
{
    private readonly BlockingCollection<IWorkItem> _queue = new();
    private readonly Thread _thread;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disposed;

    /// <summary>Creates and starts the thread.</summary>
    /// <param name="name">Thread name (for debuggers).</param>
    /// <param name="logger">Optional logger.</param>
    public UiAutomationThread(string name = "WindowVeil UI Automation", ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>
    /// The DPI awareness context that was applied to the thread (<c>-4</c> = per-monitor v2, <c>-3</c> = per-monitor),
    /// or 0 when none could be applied (UIA coordinates may then be DPI-virtualized). Valid once <see cref="Started"/> completes.
    /// </summary>
    public nint AppliedDpiAwarenessContext { get; private set; }

    /// <summary>Completes once the thread has started and applied its DPI awareness context.</summary>
    public Task Started => _started.Task;

    /// <summary>True when called from the UIA thread itself.</summary>
    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Runs <paramref name="work"/> on the UIA thread.</summary>
    /// <param name="work">The work. It receives the cancellation token and should check it between UIA calls.</param>
    /// <param name="cancellationToken">Cancels the work. The returned task is cancelled as soon as the token fires.</param>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The thread was disposed.</exception>
    public Task<T> InvokeAsync<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsCurrentThread)
        {
            // Re-entrant call from a work item: run inline to avoid dead-locking the queue.
            try { return Task.FromResult(work(cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Task.FromCanceled<T>(cancellationToken); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var item = new WorkItem<T>(work, cancellationToken);
        try
        {
            _queue.Add(item, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            item.Abandon();
            throw new ObjectDisposedException(nameof(UiAutomationThread));
        }
        return item.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the UIA thread.</summary>
    public Task InvokeAsync(Action<CancellationToken> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync<bool>(ct => { work(ct); return true; }, cancellationToken);
    }

    /// <summary>Stops accepting work, cancels queued items and lets the thread exit (waits up to 2 seconds).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (!IsCurrentThread && !_thread.Join(TimeSpan.FromSeconds(2)))
            _logger.LogWarning("The UI Automation thread did not stop within 2 seconds (a UIA call may be blocked on an unresponsive target).");
        _queue.Dispose();
    }

    private void Run()
    {
        ApplyDpiAwareness();
        _started.TrySetResult();
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (_disposed) item.Abandon();
                else item.Execute();
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue disposed while draining.
        }
    }

    private void ApplyDpiAwareness()
    {
        try
        {
            if (NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2) != 0)
            {
                AppliedDpiAwarenessContext = NativeMethods.DpiAwarenessContextPerMonitorAwareV2;
                return;
            }
            if (NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAware) != 0)
            {
                AppliedDpiAwarenessContext = NativeMethods.DpiAwarenessContextPerMonitorAware;
                _logger.LogWarning("Per-monitor v2 DPI awareness is unavailable; the UI Automation thread uses per-monitor (v1) awareness.");
                return;
            }
            _logger.LogWarning("Could not make the UI Automation thread per-monitor DPI aware; UIA coordinates may be DPI-virtualized.");
        }
        catch (EntryPointNotFoundException)
        {
            _logger.LogWarning("SetThreadDpiAwarenessContext is unavailable (Windows 10 1607+ required); UIA coordinates may be DPI-virtualized.");
        }
    }

    private interface IWorkItem
    {
        void Execute();

        void Abandon();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<CancellationToken, T> _work;
        private readonly CancellationToken _token;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public WorkItem(Func<CancellationToken, T> work, CancellationToken token)
        {
            _work = work;
            _token = token;
            if (token.CanBeCanceled)
                _registration = token.Register(static s => { var self = (WorkItem<T>)s!; self._tcs.TrySetCanceled(self._token); }, this);
        }

        public Task<T> Task => _tcs.Task;

        public void Execute()
        {
            try
            {
                if (_tcs.Task.IsCompleted) return; // cancelled while queued
                _tcs.TrySetResult(_work(_token));
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(_token);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                _registration.Dispose();
            }
        }

        public void Abandon()
        {
            _registration.Dispose();
            _tcs.TrySetException(new ObjectDisposedException(nameof(UiAutomationThread)));
        }
    }
}
