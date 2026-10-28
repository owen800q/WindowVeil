using WindowVeil.Geometry;
using WindowVeil.Locators;

namespace WindowVeil.Automation;

/// <summary>
/// A snapshot of a UI Automation element for editors and pickers, produced by <see cref="UiAutomationInspector"/>.
/// Rectangles are physical virtual-desktop pixels. The snapshot does not change when the element does; ask the
/// inspector again for fresh values.
/// </summary>
public sealed record AutomationElementInfo
{
    /// <summary>UI Automation runtime id in dotted form (e.g. <c>42.1234.5</c>); identifies the element while it lives.</summary>
    public required string RuntimeId { get; init; }

    /// <summary>Name property.</summary>
    public string Name { get; init; } = "";

    /// <summary>AutomationId property (WPF exposes <c>x:Name</c> here unless overridden).</summary>
    public string AutomationId { get; init; } = "";

    /// <summary>Control type programmatic name without the <c>ControlType.</c> prefix, e.g. <c>CheckBox</c>.</summary>
    public string ControlType { get; init; } = "";

    /// <summary>Class name.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>UI framework, e.g. <c>WPF</c>, <c>Win32</c>, <c>WinForm</c>, <c>XAML</c>.</summary>
    public string FrameworkId { get; init; } = "";

    /// <summary>Bounding rectangle (physical pixels; empty when the element has no geometry).</summary>
    public PixelRect Bounds { get; init; }

    /// <summary>Whether the element reports itself as off-screen.</summary>
    public bool IsOffscreen { get; init; }

    /// <summary>Whether the element is enabled.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Owning process id.</summary>
    public int ProcessId { get; init; }

    /// <summary>The element's own window handle (0 for windowless elements such as WPF controls).</summary>
    public nint NativeWindowHandle { get; init; }

    /// <summary>Handle of the top-level window containing the element (0 when unknown).</summary>
    public nint TopLevelWindowHandle { get; init; }

    /// <summary>Whether the element supports TextPattern.</summary>
    public bool SupportsTextPattern { get; init; }

    /// <summary>Whether the element has children in the inspected view; null when not known without an extra query.</summary>
    public bool? HasChildren { get; init; }

    /// <summary>
    /// Locators that would find this element, most robust first: AutomationId, exact Name, ControlType + Name,
    /// a text locator (TextPattern elements) and, as a last resort, a rectangle relative to the top-level window's
    /// client area.
    /// </summary>
    public IReadOnlyList<ElementLocator> SuggestedLocators { get; init; } = [];

    /// <summary>One-line human-readable description, e.g. <c>CheckBox "Start automatically…" #StartupCheck [10, 20, 300x24]</c>.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ControlType.Length == 0 ? "Element" : ControlType);
        if (Name.Length > 0) sb.Append(" \"").Append(Name.Length > 60 ? Name[..59] + "…" : Name).Append('"');
        if (AutomationId.Length > 0) sb.Append(" #").Append(AutomationId);
        if (ClassName.Length > 0) sb.Append(" .").Append(ClassName);
        sb.Append(' ').Append(Bounds);
        if (IsOffscreen) sb.Append(" (offscreen)");
        if (!IsEnabled) sb.Append(" (disabled)");
        return sb.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => Describe();
}
