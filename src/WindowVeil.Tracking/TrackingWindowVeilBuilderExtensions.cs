using Microsoft.Extensions.Logging;
using WindowVeil.Native;

using WindowVeil.Tracking;

namespace WindowVeil;

/// <summary><see cref="WindowVeilBuilder"/> extensions for window tracking.</summary>
public static class TrackingWindowVeilBuilderExtensions
{
    /// <summary>
    /// Uses <see cref="WinEventWindowEventSource"/> as the event source. When a <see cref="NativeOverlayRenderer"/> is
    /// already registered (call <c>UseNativeOverlays()</c> first), its message thread is shared; otherwise the source
    /// creates its own. Also sets <see cref="Win32WindowSystem"/> when no window system is configured.
    /// </summary>
    public static WindowVeilBuilder UseWinEventTracking(this WindowVeilBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var logger = builder.LoggerFactory.CreateLogger<WinEventWindowEventSource>();
        var thread = builder.Renderers.OfType<NativeOverlayRenderer>().Select(r => r.MessageThread).FirstOrDefault(t => !t.IsDisposed);
        var source = thread is null ? new WinEventWindowEventSource(logger) : new WinEventWindowEventSource(thread, logger);
        builder.WindowSystem ??= new Win32WindowSystem();
        return builder.UseEventSource(source);
    }

    /// <summary>
    /// Uses <see cref="PollingWindowEventSource"/> (no hooks). Requires a window system; <see cref="Win32WindowSystem"/>
    /// is used when none is configured.
    /// </summary>
    public static WindowVeilBuilder UsePollingTracking(this WindowVeilBuilder builder, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WindowSystem ??= new Win32WindowSystem();
        return builder.UseEventSource(new PollingWindowEventSource(builder.WindowSystem, interval, builder.TimeProvider,
            builder.LoggerFactory.CreateLogger<PollingWindowEventSource>()));
    }
}
