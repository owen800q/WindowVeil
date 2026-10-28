using System.Windows.Threading;

namespace WindowVeil.Wpf.Hosting;

/// <summary>Marshals work to a dispatcher with predictable completion semantics.</summary>
internal static class DispatcherInvoke
{
    /// <summary>
    /// Runs <paramref name="func"/> on the dispatcher (inline when already on its thread). The returned task completes
    /// <b>asynchronously</b> — never inline on the dispatcher thread — so callers such as the session loop never continue
    /// on, and block, the UI thread. A dispatcher that shuts down before the work runs fails the task with
    /// <see cref="WindowVeilErrorCode.WpfDispatcherFailure"/> instead of leaving it pending.
    /// </summary>
    public static Task<T> RunAsync<T>(Dispatcher dispatcher, Func<T> func, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        if (dispatcher.HasShutdownStarted) return Task.FromException<T>(Unavailable());
        if (dispatcher.CheckAccess())
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(() =>
        {
            // Exceptions go to the caller, never to Dispatcher.UnhandledException.
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, DispatcherPriority.Normal, cancellationToken);

        // Cancellation aborts a queued operation; so does a dispatcher shutdown.
        void OnAborted()
        {
            if (cancellationToken.IsCancellationRequested) tcs.TrySetCanceled(cancellationToken);
            else tcs.TrySetException(Unavailable());
        }
        operation.Aborted += (_, _) => OnAborted();
        if (operation.Status == DispatcherOperationStatus.Aborted) OnAborted();
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="action"/> on the dispatcher; see <see cref="RunAsync{T}"/>.</summary>
    public static Task RunAsync(Dispatcher dispatcher, Action action, CancellationToken cancellationToken) =>
        RunAsync(dispatcher, () => { action(); return true; }, cancellationToken);

    public static WindowVeilException Unavailable() =>
        new(WindowVeilErrorCode.WpfDispatcherFailure, "The WPF overlay dispatcher has shut down.");
}
