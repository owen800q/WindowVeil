using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowVeil.Native.Interop;
using static WindowVeil.Native.Interop.NativeMethods;

namespace WindowVeil.Native;

/// <summary>
/// A dedicated thread running a Win32 message loop (<c>GetMessage</c>/<c>TranslateMessage</c>/<c>DispatchMessage</c>).
/// Work is marshaled onto it with <see cref="InvokeAsync{T}(Func{T}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Windows have thread affinity: an HWND must be created, modified and destroyed on the thread that owns it, and that
/// thread must pump messages. WinEvent out-of-context hooks likewise deliver their callbacks as messages to the
/// installing thread. This class provides such a thread for the native renderer and the WinEvent tracker.
/// </para>
/// <para>
/// The thread switches itself to per-monitor-v2 DPI awareness (falling back to per-monitor v1, or to the process
/// default on Windows versions without <c>SetThreadDpiAwarenessContext</c>), so windows it creates are PMv2-aware and
/// every coordinate used on it is in physical pixels.
/// </para>
/// <para>
/// Work items are signaled through a message-only window rather than <c>PostThreadMessage</c>, because thread messages
/// are dropped by modal loops (for example a message box or a window drag loop), while window messages are not.
/// </para>
/// </remarks>
public sealed class NativeMessageThread : IDisposable
{
    private const string MessageWindowClass = "WindowVeil.MessageWindow";
    private const uint WM_INVOKE = WM_APP + 0x157;

    [ThreadStatic]
    private static NativeMessageThread? t_current;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startupError;
    private nint _hwnd;
    private int _signaled;
    private int _disposeState;
    private volatile bool _exited;

    /// <summary>Starts a new message thread and waits until its message queue is ready.</summary>
    /// <param name="name">Thread name for debuggers.</param>
    /// <exception cref="Win32Exception">The message window could not be created.</exception>
    public NativeMessageThread(string? name = null)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name ?? "WindowVeil message thread" };
        // STA is the conventional apartment for a thread that owns windows and pumps messages.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            _thread.Join();
            throw _startupError;
        }
    }

    /// <summary>Managed thread id of the message thread.</summary>
    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>Win32 thread id of the message thread.</summary>
    public uint NativeThreadId { get; private set; }

    /// <summary>Whether the thread runs with a per-monitor DPI awareness context (physical coordinates).</summary>
    public bool IsPerMonitorDpiAware { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> was called or the loop exited.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0 || _exited;

    /// <summary>True when called on the message thread.</summary>
    public bool CheckAccess() => ReferenceEquals(t_current, this);

    /// <summary>Throws when not called on the message thread.</summary>
    /// <exception cref="InvalidOperationException">Called from another thread.</exception>
    public void VerifyAccess()
    {
        if (!CheckAccess()) throw new InvalidOperationException("This operation must run on the WindowVeil native message thread.");
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the message thread. When called on the message thread itself the function runs
    /// inline. Continuations of the returned task do not run on the message thread.
    /// </summary>
    /// <exception cref="ObjectDisposedException">(In the task) the thread was disposed before the work ran.</exception>
    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        if (CheckAccess())
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        if (IsDisposed) return Task.FromException<T>(CreateDisposedException());

        var item = new WorkItem<T>(func);
        if (cancellationToken.CanBeCanceled)
            item.Registration = cancellationToken.Register(static (s, ct) => ((WorkItem)s!).TryCancel(ct), item);
        _queue.Enqueue(item);
        // Dispose may have run concurrently: make sure nothing is left behind in the queue.
        if (IsDisposed) FailPending();
        else Signal();
        return item.Task;
    }

    /// <summary>Runs <paramref name="action"/> on the message thread (inline when already on it).</summary>
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync<object?>(() => { action(); return null; }, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the message thread and blocks until it completes. Never call this from a thread
    /// the message thread itself may wait on.
    /// </summary>
    public T Invoke<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (CheckAccess()) return func();
        return InvokeAsync(func).GetAwaiter().GetResult();
    }

    /// <summary>Blocking variant of <see cref="InvokeAsync(Action, CancellationToken)"/>.</summary>
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess()) { action(); return; }
        InvokeAsync(action).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Posts WM_QUIT, waits for the thread to exit (up to 10 seconds) and fails pending work with
    /// <see cref="ObjectDisposedException"/>. Windows still owned by the thread are destroyed by the system when it exits.
    /// Safe to call several times and from the message thread itself (then it does not wait).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        if (CheckAccess())
        {
            // Cannot join ourselves; the loop exits after the current message and fails what is left.
            PostQuitMessage(0);
            return;
        }
        if (!_exited)
        {
            if (!PostThreadMessage(NativeThreadId, WM_QUIT, 0, 0))
            {
                // The queue may be full; ask the message window instead (handled in the window procedure).
                PostMessage(_hwnd, WM_INVOKE, 1, 0);
            }
            if (!_thread.Join(TimeSpan.FromSeconds(10)))
                Trace.TraceWarning("WindowVeil: native message thread did not exit within 10 seconds.");
        }
        FailPending();
    }

    // ------------------------------------------------------------------ thread body

    private unsafe void Run()
    {
        t_current = this;
        bool classAcquired = false;
        try
        {
            IsPerMonitorDpiAware = DpiAwarenessScope.SetCurrentThreadPerMonitorV2();
            NativeThreadId = GetCurrentThreadId();
            // Force creation of the thread's message queue before anybody posts to it.
            PeekMessage(out _, 0, WM_USER, WM_USER, PM_NOREMOVE);

            delegate* unmanaged<nint, uint, nint, nint, nint> proc = &MessageWindowProc;
            WindowClassRegistry.Acquire(MessageWindowClass, (nint)proc);
            classAcquired = true;
            // HWND_MESSAGE parent: a message-only window, invisible, not enumerable, receives posted messages.
            _hwnd = CreateWindowEx(0, MessageWindowClass, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, WindowClassRegistry.ModuleHandle, 0);
            if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the WindowVeil message window.");
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _exited = true;
            if (classAcquired) WindowClassRegistry.Release(MessageWindowClass);
            t_current = null;
            _ready.Set();
            return;
        }

        _ready.Set();
        try
        {
            while (true)
            {
                int result = GetMessage(out MSG msg, 0, 0, 0);
                if (result == 0) break; // WM_QUIT
                if (result == -1)
                {
                    Trace.TraceError("WindowVeil: GetMessage failed with error {0}.", Marshal.GetLastPInvokeError());
                    break;
                }
                TranslateMessage(in msg);
                DispatchMessage(in msg);
                // Safety net in case a WM_INVOKE post was lost (for example a full message queue).
                if (!_queue.IsEmpty && Volatile.Read(ref _signaled) == 0) Drain();
            }
        }
        finally
        {
            _exited = true;
            try
            {
                if (_hwnd != 0) DestroyWindow(_hwnd);
                _hwnd = 0;
                WindowClassRegistry.Release(MessageWindowClass);
            }
            catch (Exception ex) { Trace.TraceError("WindowVeil: message thread cleanup failed: {0}", ex); }
            FailPending();
            t_current = null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint MessageWindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_INVOKE)
            {
                var current = t_current;
                if (wParam == 1) PostQuitMessage(0);
                else current?.Drain();
                return 0;
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind into user32.
            Trace.TraceError("WindowVeil: message window procedure failed: {0}", ex);
            return 0;
        }
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _signaled, 1) != 0) return;
        if (!PostMessage(_hwnd, WM_INVOKE, 0, 0))
        {
            // Allow a later Signal (or the loop's safety net) to retry.
            Volatile.Write(ref _signaled, 0);
        }
    }

    private void Drain()
    {
        Volatile.Write(ref _signaled, 0);
        while (_queue.TryDequeue(out var item))
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                item.TryFail(CreateDisposedException());
                continue;
            }
            item.Execute();
        }
    }

    private void FailPending()
    {
        while (_queue.TryDequeue(out var item)) item.TryFail(CreateDisposedException());
    }

    private static ObjectDisposedException CreateDisposedException() => new(nameof(NativeMessageThread));

    // ------------------------------------------------------------------ work items

    private abstract class WorkItem
    {
        private int _claimed;
        public CancellationTokenRegistration Registration;

        protected bool TryClaim()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) return false;
            Registration.Dispose();
            return true;
        }

        public abstract void Execute();

        public abstract void TryFail(Exception exception);

        public abstract void TryCancel(CancellationToken token);
    }

    private sealed class WorkItem<T>(Func<T> func) : WorkItem
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _tcs.Task;

        public override void Execute()
        {
            if (!TryClaim()) return;
            try { _tcs.TrySetResult(func()); }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }

        public override void TryFail(Exception exception)
        {
            if (TryClaim()) _tcs.TrySetException(exception);
        }

        public override void TryCancel(CancellationToken token)
        {
            if (TryClaim()) _tcs.TrySetCanceled(token);
        }
    }
}
