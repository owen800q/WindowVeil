using Microsoft.Extensions.Logging;

using WindowVeil.Native;

namespace WindowVeil;

/// <summary><see cref="WindowVeilBuilder"/> extensions for the Win32 backend.</summary>
public static class NativeWindowVeilBuilderExtensions
{
    /// <summary>
    /// Registers the native overlay renderer (masks, highlights, spotlights) and, when no window system is configured
    /// yet, <see cref="Win32WindowSystem"/>. Unless <see cref="NativeOverlayOptions.MessageThread"/> is supplied, a
    /// <see cref="NativeMessageThread"/> is created and registered as an engine-owned component, so other Win32
    /// components (for example WinEvent tracking) can share it; the engine disposes it last.
    /// </summary>
    public static WindowVeilBuilder UseNativeOverlays(this WindowVeilBuilder builder, NativeOverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new NativeOverlayOptions();
        if (options.MessageThread is null)
        {
            var thread = new NativeMessageThread("WindowVeil native overlays");
            builder.AddOwnedComponent(thread);
            options = new NativeOverlayOptions { ExcludeFromCapture = options.ExcludeFromCapture, MessageThread = thread };
        }
        builder.WindowSystem ??= new Win32WindowSystem();
        builder.AddRenderer(new NativeOverlayRenderer(options, builder.LoggerFactory.CreateLogger<NativeOverlayRenderer>()));
        return builder;
    }
}
