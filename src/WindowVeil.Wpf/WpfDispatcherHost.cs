using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Wpf.Interop;

namespace WindowVeil.Wpf;

/// <summary>
/// A dedicated STA thread running a WPF <see cref="System.Windows.Threading.Dispatcher"/> message loop. Used by
/// <see cref="WpfOverlayRenderer"/> when no application dispatcher is supplied, and usable on its own (for example to host
/// WPF content from a console or service process).
/// </summary>
/// <remarks>
/// <para>
/// Before the dispatcher (and therefore any HWND) is created, the thread switches itself to per-monitor-v2 DPI awareness
/// with <c>SetThreadDpiAwarenessContext</c>, so windows created on it receive physical coordinates and <c>WM_DPICHANGED</c>.
/// WPF only rescales its content per monitor when the <b>process</b> is per-monitor-v2 aware, so processes hosting
/// WindowVeil overlays should declare <c>&lt;dpiAwareness&gt;PerMonitorV2&lt;/dpiAwareness&gt;</c> in their
/// <c>app.manifest</c> (the WindowVeil samples do). Without it, overlays are still positioned in exact physical pixels, but
/// WPF may render their content at the system DPI.
/// </para>
/// <para>
/// Exceptions that escape overlay content on this dispatcher (event handlers, bindings, layout, animations) are logged and
/// marked handled, so a faulty overlay control can never terminate the host process.
/// </para>
/// </remarks>
public sealed class WpfDispatcherHost : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private int _disposed;

    /// <summary>Starts the thread and waits until its dispatcher is ready.</summary>
    /// <param name="threadName">Thread name for diagnostics.</param>
    /// <param name="logger">Receives unhandled dispatcher exceptions.</param>
    /// <exception cref="WindowVeilException">The thread could not be started (<see cref="WindowVeilErrorCode.WpfDispatcherFailure"/>).</exception>
    public WpfDispatcherHost(string? threadName = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        Dispatcher? dispatcher = null;
        Exception? startupError = null;
        using var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            try
            {
                // Must happen before the Dispatcher creates its first (message-only) HWND: a window's DPI awareness is
                // fixed at creation from the creating thread's context.
                IsPerMonitorV2 = NativeMethods.TrySetThreadPerMonitorV2();
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.UnhandledException += OnUnhandledException;
            }
            catch (Exception ex)
            {
                startupError = ex;
                return;
            }
            finally
            {
                ready.Set();
            }
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = threadName ?? "WindowVeil WPF dispatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        try
        {
            _thread.Start();
        }
        catch (Exception ex) when (ex is ThreadStartException or OutOfMemoryException)
        {
            throw new WindowVeilException(WindowVeilErrorCode.WpfDispatcherFailure, "Failed to start the WPF dispatcher thread.", ex);
        }
        ready.Wait();
        if (startupError is not null || dispatcher is null)
            throw new WindowVeilException(WindowVeilErrorCode.WpfDispatcherFailure, "Failed to initialize the WPF dispatcher thread.", startupError);
        _dispatcher = dispatcher;
    }

    /// <summary>The dispatcher running on the dedicated thread.</summary>
    public Dispatcher Dispatcher => _dispatcher;

    /// <summary>The dedicated STA thread.</summary>
    public Thread Thread => _thread;

    /// <summary>Whether the thread was switched to per-monitor-v2 DPI awareness (false before Windows 10 1607).</summary>
    public bool IsPerMonitorV2 { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has been called or the dispatcher shut down for another reason.</summary>
    public bool IsShutDown => Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted;

    /// <summary>
    /// Runs <paramref name="action"/> on the dispatcher thread (inline when called on it). The task completes on the thread
    /// pool, never inline on the dispatcher, and fails with <see cref="WindowVeilErrorCode.WpfDispatcherFailure"/> if the
    /// dispatcher shuts down first.
    /// </summary>
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Hosting.DispatcherInvoke.RunAsync(_dispatcher, action, cancellationToken);
    }

    /// <summary>Runs <paramref name="func"/> on the dispatcher thread and returns its result (same semantics as <see cref="InvokeAsync(Action, CancellationToken)"/>).</summary>
    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Hosting.DispatcherInvoke.RunAsync(_dispatcher, func, cancellationToken);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Overlay content is third-party UI code running inside someone else's process: never let it crash the host.
        _logger.LogError(e.Exception, "Unhandled exception on the WPF overlay dispatcher; the exception was handled to keep the process alive");
        e.Handled = true;
    }

    /// <summary>
    /// Shuts the dispatcher down (destroying every window it owns) and waits for the thread to exit. Idempotent. When called
    /// on the dispatcher thread itself, the shutdown is queued and the method returns without waiting.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            return;
        }
        try
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.InvokeShutdown();
        }
        catch (InvalidOperationException)
        {
            // The dispatcher already shut down between the check and the call.
        }
        if (!_thread.Join(JoinTimeout))
            _logger.LogWarning("The WPF overlay dispatcher thread did not exit within {Timeout}", JoinTimeout);
    }

    /// <summary>Asynchronous form of <see cref="Dispose"/>; waits for the thread on the thread pool.</summary>
    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return ValueTask.CompletedTask;
        if (_dispatcher.CheckAccess()) { Dispose(); return ValueTask.CompletedTask; }
        return new ValueTask(Task.Run(Dispose));
    }
}
