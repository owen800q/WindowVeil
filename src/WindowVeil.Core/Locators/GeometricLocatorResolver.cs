using WindowVeil.Geometry;

namespace WindowVeil.Locators;

/// <summary>Resolves rectangle, window-relative and normalized locators. Pure geometry; always available.</summary>
public sealed class GeometricLocatorResolver : ILocatorResolver
{
    /// <inheritdoc />
    public bool CanResolve(ElementLocator locator) => locator is ScreenRectangleLocator or WindowRelativeLocator or NormalizedLocator;

    /// <inheritdoc />
    public ValueTask<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var w = context.Window;
        PixelRect? rect = locator switch
        {
            ScreenRectangleLocator s => s.Bounds,
            WindowRelativeLocator r => CoordinateConverter.RelativeToScreen(r.Bounds, Anchor(w, r.Anchor).Location),
            NormalizedLocator n => CoordinateConverter.NormalizedToScreen(n.Bounds, Anchor(w, n.Anchor)),
            _ => null,
        };
        if (rect is null)
            return ValueTask.FromResult(LocatorResolution.Failed(locator, LocatorStatus.Unsupported, $"Unsupported locator {locator.Kind}."));
        if (rect.Value.IsEmpty)
            return ValueTask.FromResult(LocatorResolution.Failed(locator, LocatorStatus.Rejected, $"Rectangle {rect} is empty."));
        return ValueTask.FromResult(LocatorResolution.Found(locator, rect.Value) with { WindowBounds = w.Bounds });
    }

    private static PixelRect Anchor(Platform.TargetWindowInfo w, WindowAnchor anchor) =>
        anchor == WindowAnchor.Client ? w.ClientBounds : w.Bounds;
}
