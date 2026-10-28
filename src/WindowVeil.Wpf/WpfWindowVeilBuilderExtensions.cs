using Microsoft.Extensions.Logging;

using WindowVeil.Wpf;

namespace WindowVeil;

/// <summary>Registers the WPF renderer with a <see cref="WindowVeilBuilder"/>.</summary>
public static class WpfWindowVeilBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="WpfOverlayRenderer"/> so rules can use <see cref="WpfAppearance"/>. The engine owns and disposes the
    /// renderer (which shuts down its dedicated dispatcher thread, if it started one).
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="options">Renderer options; by default overlays run on a dedicated dispatcher thread, one host window each.</param>
    /// <returns>The builder.</returns>
    public static WindowVeilBuilder UseWpfOverlays(this WindowVeilBuilder builder, WpfOverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new WpfOverlayOptions();
        if (options.Logger is null) options = options with { Logger = builder.LoggerFactory.CreateLogger<WpfOverlayRenderer>() };
        var renderer = new WpfOverlayRenderer(options);
        builder.AddRenderer(renderer);
        builder.AddOwnedComponent(renderer);
        return builder;
    }
}
