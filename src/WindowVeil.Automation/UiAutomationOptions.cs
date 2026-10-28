namespace WindowVeil.Automation;

/// <summary>Options for <see cref="UiAutomationLocatorResolver"/> and <see cref="UiAutomationInspector"/>.</summary>
public sealed record UiAutomationOptions
{
    /// <summary>
    /// Search the raw UI Automation view instead of the control view. The raw view also contains layout-only
    /// elements, which makes searches slower and deeper. Default false.
    /// </summary>
    public bool UseRawView { get; init; }

    /// <summary>
    /// Cache the element found for a <see cref="Locators.UiAutomationLocator"/> in
    /// <see cref="Locators.LocatorResolution.State"/> and re-validate it on the next resolution instead of searching
    /// the tree again. Default true.
    /// </summary>
    public bool CacheElements { get; init; } = true;

    /// <summary>Maximum number of characters read from one TextPattern document. Default 100 000.</summary>
    public int MaxTextLength { get; init; } = 100_000;

    /// <summary>Maximum tree depth searched by text locators (which have no per-locator bound). Default 32.</summary>
    public int TextSearchMaxDepth { get; init; } = 32;

    /// <summary>Maximum number of elements visited by one text-locator search. Default 5000.</summary>
    public int TextSearchMaxNodes { get; init; } = 5000;

    /// <summary>Maximum number of text matches turned into geometry per resolution. Default 256.</summary>
    public int MaxTextMatches { get; init; } = 256;

    /// <summary>Capacity of the inspector's RuntimeId → element map (least recently used entries are evicted). Default 4096.</summary>
    public int InspectorCacheCapacity { get; init; } = 4096;

    internal void Validate()
    {
        if (MaxTextLength <= 0) throw new ArgumentOutOfRangeException(nameof(MaxTextLength), MaxTextLength, "Must be positive.");
        if (TextSearchMaxDepth < 0) throw new ArgumentOutOfRangeException(nameof(TextSearchMaxDepth), TextSearchMaxDepth, "Must not be negative.");
        if (TextSearchMaxNodes <= 0) throw new ArgumentOutOfRangeException(nameof(TextSearchMaxNodes), TextSearchMaxNodes, "Must be positive.");
        if (MaxTextMatches <= 0) throw new ArgumentOutOfRangeException(nameof(MaxTextMatches), MaxTextMatches, "Must be positive.");
        if (InspectorCacheCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(InspectorCacheCapacity), InspectorCacheCapacity, "Must be positive.");
    }
}
