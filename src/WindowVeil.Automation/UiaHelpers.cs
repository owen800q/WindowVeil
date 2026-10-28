using System.Runtime.InteropServices;
using System.Windows.Automation;
using UiaAutomation = System.Windows.Automation.Automation;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WpfRect = System.Windows.Rect;

namespace WindowVeil.Automation;

/// <summary>Shared UI Automation helpers. Every member must be called on the UIA thread.</summary>
internal static class UiaHelpers
{
    private const string ControlTypePrefix = "ControlType.";

    /// <summary>Properties fetched in bulk while searching.</summary>
    private static readonly AutomationProperty[] SearchProperties =
    [
        AutomationElement.AutomationIdProperty,
        AutomationElement.NameProperty,
        AutomationElement.ControlTypeProperty,
        AutomationElement.ClassNameProperty,
        AutomationElement.NativeWindowHandleProperty,
        AutomationElement.BoundingRectangleProperty,
        AutomationElement.IsOffscreenProperty,
        AutomationElement.IsTextPatternAvailableProperty,
    ];

    /// <summary>Additional properties fetched for the inspector.</summary>
    private static readonly AutomationProperty[] InspectorProperties =
    [
        AutomationElement.FrameworkIdProperty,
        AutomationElement.IsEnabledProperty,
        AutomationElement.ProcessIdProperty,
        AutomationElement.RuntimeIdProperty,
    ];

    /// <summary>Creates the cache request used for searches (full element mode, so patterns stay usable).</summary>
    public static CacheRequest CreateSearchCacheRequest(bool rawView, bool forInspector = false)
    {
        var request = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = rawView ? UiaAutomation.RawViewCondition : UiaAutomation.ControlViewCondition,
        };
        foreach (var p in SearchProperties) request.Add(p);
        if (forInspector) foreach (var p in InspectorProperties) request.Add(p);
        return request;
    }

    /// <summary>The tree walker for the configured view.</summary>
    public static TreeWalker Walker(bool rawView) => rawView ? TreeWalker.RawViewWalker : TreeWalker.ControlViewWalker;

    /// <summary>
    /// Children of <paramref name="element"/> in the cache request's view with cached properties (one cross-process call),
    /// or an empty array when the element went away.
    /// </summary>
    public static AutomationElement[] GetChildren(AutomationElement element, CacheRequest request)
    {
        try
        {
            using (request.Activate())
            {
                var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                var result = new AutomationElement[children.Count];
                children.CopyTo(result, 0);
                return result;
            }
        }
        catch (Exception ex) when (IsStale(ex))
        {
            return [];
        }
    }

    /// <summary>True for exceptions that mean "the element (or its provider) is gone or not responding".</summary>
    public static bool IsStale(Exception ex) =>
        ex is ElementNotAvailableException or COMException or TimeoutException
        || ex is InvalidOperationException and not ObjectDisposedException;

    /// <summary>Converts a UIA rectangle (physical pixels on a per-monitor aware thread) to a pixel rectangle, rounded outward.</summary>
    public static bool TryToPixelRect(WpfRect rect, out PixelRect result)
    {
        result = PixelRect.Empty;
        if (rect.IsEmpty) return false;
        if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)) return false;
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        result = PixelRect.FromRectDOutward(new RectD(rect.X, rect.Y, rect.Width, rect.Height));
        return !result.IsEmpty;
    }

    /// <summary>The programmatic name of a control type without the <c>ControlType.</c> prefix (e.g. <c>CheckBox</c>).</summary>
    public static string ControlTypeName(ControlType? controlType)
    {
        var name = controlType?.ProgrammaticName ?? "";
        return StripPrefix(name);
    }

    private static string StripPrefix(string name) =>
        name.StartsWith(ControlTypePrefix, StringComparison.OrdinalIgnoreCase) ? name[ControlTypePrefix.Length..] : name;

    /// <summary>Whether <paramref name="actual"/> matches the selector's control type (case-insensitive, prefix optional).</summary>
    public static bool ControlTypeMatches(ControlType? actual, string expected) =>
        string.Equals(ControlTypeName(actual), StripPrefix(expected.Trim()), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the element's cached properties satisfy every condition of the selector (Index and Ancestor are not checked).</summary>
    public static bool MatchesCached(AutomationElement element, UiAutomationLocator selector)
    {
        var c = element.Cached;
        if (selector.AutomationId is not null && !string.Equals(c.AutomationId, selector.AutomationId, StringComparison.Ordinal)) return false;
        if (selector.ClassName is not null && !string.Equals(c.ClassName, selector.ClassName, StringComparison.Ordinal)) return false;
        if (selector.ControlType is not null && !ControlTypeMatches(c.ControlType, selector.ControlType)) return false;
        if (selector.Name is not null && !selector.Name.IsMatch(c.Name)) return false;
        return true;
    }

    /// <summary>Whether the element is (part of) a WindowVeil overlay window.</summary>
    public static bool IsOverlay(AutomationElement element, IReadOnlySet<nint>? overlays)
    {
        nint handle = element.Cached.NativeWindowHandle;
        if (handle == 0) return false;
        if (overlays is not null && overlays.Contains(handle)) return true;
        if (OverlayWindowRegistry.IsOverlay(handle)) return true;
        nint root = NativeMethods.GetRoot(handle);
        return root != 0 && root != handle && ((overlays is not null && overlays.Contains(root)) || OverlayWindowRegistry.IsOverlay(root));
    }

    /// <summary>Cached TextPattern availability (false when not supported or not cached).</summary>
    public static bool CachedSupportsTextPattern(AutomationElement element)
    {
        try
        {
            return element.GetCachedPropertyValue(AutomationElement.IsTextPatternAvailableProperty, ignoreDefaultValue: false) is true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Formats a runtime id as a dotted string (e.g. <c>42.1234.5</c>).</summary>
    public static string RuntimeIdKey(int[]? runtimeId) => runtimeId is null || runtimeId.Length == 0 ? "" : string.Join('.', runtimeId);

    /// <summary>Collapses whitespace runs to single spaces and trims.</summary>
    public static string CollapseWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool pending = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch)) { pending = sb.Length > 0; continue; }
            if (pending) { sb.Append(' '); pending = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
