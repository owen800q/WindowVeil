namespace WindowVeil.Native.Interop;

/// <summary>
/// Temporarily switches the calling thread to per-monitor-v2 DPI awareness so every coordinate returned or accepted by
/// USER32 on this thread is in physical pixels, then restores the previous context on dispose.
/// </summary>
/// <remarks>
/// <c>SetThreadDpiAwarenessContext</c> exists since Windows 10 1607 and PMv2 since 1703. On older systems the scope
/// falls back to per-monitor v1, and on systems without the API it is a no-op (the process awareness applies).
/// </remarks>
internal readonly struct DpiAwarenessScope : IDisposable
{
    private static volatile bool s_unavailable;
    private readonly nint _previous;

    private DpiAwarenessScope(nint previous) => _previous = previous;

    /// <summary>Enters a PMv2 (or best available per-monitor) context on the current thread.</summary>
    public static DpiAwarenessScope EnterPerMonitorV2()
    {
        if (s_unavailable) return default;
        try
        {
            nint previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previous == 0)
                previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
            return new DpiAwarenessScope(previous);
        }
        catch (EntryPointNotFoundException)
        {
            s_unavailable = true;
            return default;
        }
    }

    /// <summary>Permanently sets the current thread to PMv2 (used by dedicated threads). Returns false when unsupported.</summary>
    public static bool SetCurrentThreadPerMonitorV2()
    {
        if (s_unavailable) return false;
        try
        {
            if (NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) != 0) return true;
            return NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            s_unavailable = true;
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_previous != 0) NativeMethods.SetThreadDpiAwarenessContext(_previous);
    }
}
