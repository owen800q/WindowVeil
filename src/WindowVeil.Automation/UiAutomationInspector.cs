using System.Windows.Automation;
using UiaAutomation = System.Windows.Automation.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Rendering;

namespace WindowVeil.Automation;

/// <summary>
/// Inspects the UI Automation tree of external windows for visual editors and element pickers: hit testing,
/// tree navigation and locator suggestions.
/// </summary>
/// <remarks>
/// <para>All UIA calls run on a private <see cref="UiAutomationThread"/> (MTA, per-monitor v2 DPI aware); the methods
/// can be awaited from any thread, including a WPF UI thread. Coordinates are physical virtual-desktop pixels.</para>
/// <para>The inspector remembers the elements it returned (keyed by <see cref="AutomationElementInfo.RuntimeId"/>,
/// least recently used entries are evicted after <see cref="UiAutomationOptions.InspectorCacheCapacity"/> entries) so
/// that <see cref="GetChildrenAsync"/>, <see cref="GetParentAsync"/> and <see cref="RefreshAsync"/> can navigate from a
/// snapshot. When the element is no longer remembered or no longer exists, those methods return null or an empty list
/// instead of throwing.</para>
/// </remarks>
public sealed class UiAutomationInspector : IDisposable
{
    private const int MaxHitTestDepth = 64;
    private const int MaxWindowsBelow = 1024;

    /// <summary>Control types that commonly own a templated text child carrying their label.</summary>
    private static readonly HashSet<string> LabelOwnerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CheckBox", "RadioButton", "Button", "Hyperlink", "ListItem", "MenuItem", "TabItem", "TreeItem", "SplitButton",
        "DataItem", "HeaderItem", "ComboBox",
    };

    private readonly UiAutomationThread _thread;
    private readonly ILogger _logger;
    private readonly ElementCache _cache;
    private CacheRequest? _request; // UIA thread only
    private volatile bool _disposed;

    /// <summary>Creates an inspector with its own UI Automation thread.</summary>
    public UiAutomationInspector(UiAutomationOptions? options = null, ILogger? logger = null)
    {
        Options = options ?? new UiAutomationOptions();
        Options.Validate();
        _logger = logger ?? NullLogger.Instance;
        _cache = new ElementCache(Options.InspectorCacheCapacity);
        _thread = new UiAutomationThread("WindowVeil UIA inspector", _logger);
    }

    /// <summary>Options in effect.</summary>
    public UiAutomationOptions Options { get; }

    private CacheRequest Request => _request ??= UiaHelpers.CreateSearchCacheRequest(Options.UseRawView, forInspector: true);

    /// <summary>
    /// Returns the element at a screen point (physical pixels), or null when there is none. Text elements that only
    /// label a check box, button or similar control are replaced by that control (see the overload with
    /// <c>preferLabelOwner</c>).
    /// </summary>
    /// <param name="screenPoint">Point in physical virtual-desktop pixels.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<AutomationElementInfo?> ElementFromPointAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default) =>
        ElementFromPointAsync(screenPoint, preferLabelOwner: true, cancellationToken);

    /// <summary>
    /// Returns the element at a screen point (physical pixels), or null when there is none.
    /// </summary>
    /// <remarks>
    /// WindowVeil overlays are ignored: when the point hits a window registered in <see cref="OverlayWindowRegistry"/>
    /// (checked on the element's window and its top-level root), the inspector walks the top-level windows below the
    /// overlay in z-order, picks the first visible one containing the point and descends its UIA tree to the deepest
    /// element containing the point. When nothing is found below, the result is null.
    /// </remarks>
    /// <param name="screenPoint">Point in physical virtual-desktop pixels.</param>
    /// <param name="preferLabelOwner">
    /// When true, a text element that is merely the label of a check box, button, radio button, hyperlink or
    /// list/menu/tab item is replaced by that owning control, so clicking the label of <c>StartupCheck</c> yields the
    /// check box itself.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<AutomationElementInfo?> ElementFromPointAsync(PixelPoint screenPoint, bool preferLabelOwner, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _thread.InvokeAsync(ct =>
        {
            try
            {
                var request = Request;
                var element = AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
                if (element is null) return null;
                element = element.GetUpdatedCache(request);
                nint top = TopLevelHandle(element, request);
                if ((top != 0 && OverlayWindowRegistry.IsOverlay(top)) || UiaHelpers.IsOverlay(element, null))
                {
                    element = HitTestBelow(top != 0 ? top : NativeMethods.GetRoot(element.Cached.NativeWindowHandle), screenPoint, request, ct);
                    if (element is null) return null;
                    top = TopLevelHandle(element, request);
                }
                if (preferLabelOwner) element = PromoteLabel(element, request);
                return Build(element, top, request);
            }
            catch (Exception ex) when (UiaHelpers.IsStale(ex))
            {
                _logger.LogDebug("ElementFromPoint {Point} failed: {Error}", screenPoint, ex.Message);
                return (AutomationElementInfo?)null;
            }
        }, cancellationToken);
    }

    /// <summary>Returns the UIA element of a window, or null when UI Automation is unavailable for it.</summary>
    public Task<AutomationElementInfo?> GetRootAsync(nint hwnd, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _thread.InvokeAsync(ct =>
        {
            try
            {
                var request = Request;
                var element = AutomationElement.FromHandle(hwnd).GetUpdatedCache(request);
                nint top = NativeMethods.GetRoot(hwnd);
                return Build(element, top == 0 ? hwnd : top, request);
            }
            catch (Exception ex) when (UiaHelpers.IsStale(ex) || ex is ArgumentException)
            {
                _logger.LogDebug("GetRoot 0x{Handle:X} failed: {Error}", hwnd, ex.Message);
                return (AutomationElementInfo?)null;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Returns up to <paramref name="maxChildren"/> children of a previously returned element (WindowVeil overlays are
    /// skipped). Returns an empty list when the parent is gone or no longer remembered.
    /// </summary>
    public Task<IReadOnlyList<AutomationElementInfo>> GetChildrenAsync(AutomationElementInfo parent, int maxChildren = 500, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChildren);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _thread.InvokeAsync<IReadOnlyList<AutomationElementInfo>>(ct =>
        {
            var element = _cache.Get(parent.RuntimeId);
            if (element is null) return [];
            var request = Request;
            var result = new List<AutomationElementInfo>();
            foreach (var child in UiaHelpers.GetChildren(element, request))
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= maxChildren) break;
                try
                {
                    if (UiaHelpers.IsOverlay(child, null)) continue;
                    nint own = child.Cached.NativeWindowHandle;
                    nint top = own != 0 ? NativeMethods.GetRoot(own) : parent.TopLevelWindowHandle;
                    result.Add(Build(child, top, request));
                }
                catch (Exception ex) when (UiaHelpers.IsStale(ex))
                {
                    // skip children that vanished
                }
            }
            return result;
        }, cancellationToken);
    }

    /// <summary>Returns the parent of a previously returned element, or null for top-level windows, gone or unknown elements.</summary>
    public Task<AutomationElementInfo?> GetParentAsync(AutomationElementInfo element, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _thread.InvokeAsync(ct =>
        {
            var current = _cache.Get(element.RuntimeId);
            if (current is null) return null;
            try
            {
                var request = Request;
                var parent = UiaHelpers.Walker(Options.UseRawView).GetParent(current, request);
                if (parent is null || UiaAutomation.Compare(parent, AutomationElement.RootElement)) return null;
                return Build(parent, TopLevelHandle(parent, request), request);
            }
            catch (Exception ex) when (UiaHelpers.IsStale(ex))
            {
                return (AutomationElementInfo?)null;
            }
        }, cancellationToken);
    }

    /// <summary>Re-reads the properties of a previously returned element; null when it is gone or unknown.</summary>
    public Task<AutomationElementInfo?> RefreshAsync(AutomationElementInfo element, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _thread.InvokeAsync(ct =>
        {
            var current = _cache.Get(element.RuntimeId);
            if (current is null) return null;
            try
            {
                var request = Request;
                var fresh = current.GetUpdatedCache(request);
                return Build(fresh, element.TopLevelWindowHandle != 0 ? element.TopLevelWindowHandle : TopLevelHandle(fresh, request), request);
            }
            catch (Exception ex) when (UiaHelpers.IsStale(ex))
            {
                _cache.Remove(element.RuntimeId);
                return (AutomationElementInfo?)null;
            }
        }, cancellationToken);
    }

    /// <summary>Stops the UI Automation thread and forgets remembered elements.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thread.Dispose();
        _cache.Clear();
    }

    // ---------------------------------------------------------------- UIA thread helpers

    private nint TopLevelHandle(AutomationElement element, CacheRequest request)
    {
        var walker = TreeWalker.RawViewWalker;
        var current = element;
        for (int i = 0; i < MaxHitTestDepth && current is not null; i++)
        {
            nint handle = current.Cached.NativeWindowHandle;
            if (handle != 0) return NativeMethods.GetRoot(handle);
            current = walker.GetParent(current, request);
        }
        return 0;
    }

    private AutomationElement? HitTestBelow(nint overlayTop, PixelPoint point, CacheRequest request, CancellationToken ct)
    {
        if (overlayTop == 0) return null;
        nint h = overlayTop;
        for (int i = 0; i < MaxWindowsBelow; i++)
        {
            ct.ThrowIfCancellationRequested();
            h = NativeMethods.GetWindow(h, NativeMethods.GW_HWNDNEXT);
            if (h == 0) return null;
            if (OverlayWindowRegistry.IsOverlay(h) || !NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) continue;
            if (!NativeMethods.GetWindowRect(h, out var r)) continue;
            if (!PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(point)) continue;
            try
            {
                var root = AutomationElement.FromHandle(h).GetUpdatedCache(request);
                return Descend(root, point, request, ct);
            }
            catch (Exception ex) when (UiaHelpers.IsStale(ex) || ex is ArgumentException)
            {
                continue;
            }
        }
        return null;
    }

    private static AutomationElement Descend(AutomationElement root, PixelPoint point, CacheRequest request, CancellationToken ct)
    {
        var current = root;
        for (int depth = 0; depth < MaxHitTestDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();
            AutomationElement? best = null;
            long bestArea = long.MaxValue;
            foreach (var child in UiaHelpers.GetChildren(current, request))
            {
                var c = child.Cached;
                if (c.IsOffscreen || UiaHelpers.IsOverlay(child, null) || !UiaHelpers.TryToPixelRect(c.BoundingRectangle, out var r) || !r.Contains(point)) continue;
                if (r.Area < bestArea) { best = child; bestArea = r.Area; }
            }
            if (best is null) break;
            current = best;
        }
        return current;
    }

    private AutomationElement PromoteLabel(AutomationElement element, CacheRequest request)
    {
        var c = element.Cached;
        if (!string.Equals(UiaHelpers.ControlTypeName(c.ControlType), "Text", StringComparison.OrdinalIgnoreCase) || c.AutomationId.Length > 0)
            return element;
        try
        {
            var parent = UiaHelpers.Walker(Options.UseRawView).GetParent(element, request);
            if (parent is null) return element;
            var p = parent.Cached;
            bool owner = LabelOwnerTypes.Contains(UiaHelpers.ControlTypeName(p.ControlType));
            bool sameLabel = c.Name.Length == 0 || string.Equals(UiaHelpers.CollapseWhitespace(p.Name), UiaHelpers.CollapseWhitespace(c.Name), StringComparison.Ordinal);
            return owner && sameLabel ? parent : element;
        }
        catch (Exception ex) when (UiaHelpers.IsStale(ex))
        {
            return element;
        }
    }

    private AutomationElementInfo Build(AutomationElement element, nint topLevel, CacheRequest request)
    {
        var c = element.Cached;
        string runtimeId = UiaHelpers.RuntimeIdKey(element.GetRuntimeId());
        bool hasBounds = UiaHelpers.TryToPixelRect(c.BoundingRectangle, out var bounds);
        bool supportsText = UiaHelpers.CachedSupportsTextPattern(element);
        var info = new AutomationElementInfo
        {
            RuntimeId = runtimeId,
            Name = c.Name ?? "",
            AutomationId = c.AutomationId ?? "",
            ControlType = UiaHelpers.ControlTypeName(c.ControlType),
            ClassName = c.ClassName ?? "",
            FrameworkId = c.FrameworkId ?? "",
            Bounds = hasBounds ? bounds : PixelRect.Empty,
            IsOffscreen = c.IsOffscreen,
            IsEnabled = c.IsEnabled,
            ProcessId = c.ProcessId,
            NativeWindowHandle = c.NativeWindowHandle,
            TopLevelWindowHandle = topLevel,
            SupportsTextPattern = supportsText,
        };
        info = info with { SuggestedLocators = Suggest(info, element) };
        if (runtimeId.Length > 0) _cache.Put(runtimeId, element);
        return info;
    }

    private static List<ElementLocator> Suggest(AutomationElementInfo info, AutomationElement element)
    {
        var list = new List<ElementLocator>();
        if (LooksStable(info.AutomationId)) list.Add(ElementLocator.ByAutomationId(info.AutomationId));
        bool usableName = info.Name.Length > 0 && info.Name.Length <= 256;
        if (usableName) list.Add(ElementLocator.ByName(info.Name));
        if (usableName && info.ControlType.Length > 0) list.Add(ElementLocator.ByControlType(info.ControlType).WithName(info.Name));
        if (info.SupportsTextPattern)
        {
            string text = info.Name.Length > 0 ? info.Name : FirstLine(element);
            if (text.Length > 64) text = text[..64].TrimEnd();
            if (text.Length > 0)
            {
                var textLocator = ElementLocator.ByText(text);
                if (LooksStable(info.AutomationId)) textLocator = textLocator with { Scope = ElementLocator.ByAutomationId(info.AutomationId) };
                list.Add(textLocator);
            }
        }
        if (!info.Bounds.IsEmpty && info.TopLevelWindowHandle != 0)
        {
            var origin = new NativeMethods.POINT();
            if (NativeMethods.ClientToScreen(info.TopLevelWindowHandle, ref origin))
                list.Add(ElementLocator.WindowRelative(info.Bounds.X - origin.X, info.Bounds.Y - origin.Y, info.Bounds.Width, info.Bounds.Height));
        }
        return list;
    }

    private static string FirstLine(AutomationElement element)
    {
        try
        {
            if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern pattern) return "";
            string text = pattern.DocumentRange.GetText(256) ?? "";
            foreach (var line in text.Split('\n'))
            {
                var trimmed = UiaHelpers.CollapseWhitespace(line);
                if (trimmed.Length > 0) return trimmed;
            }
            return "";
        }
        catch (Exception ex) when (UiaHelpers.IsStale(ex))
        {
            return "";
        }
    }

    /// <summary>Heuristic: AutomationIds that look generated (GUIDs, long numbers) are not suggested.</summary>
    internal static bool LooksStable(string automationId)
    {
        if (string.IsNullOrWhiteSpace(automationId) || automationId.Length > 200) return false;
        if (Guid.TryParse(automationId, out _)) return false;
        if (automationId.Length > 6 && automationId.All(char.IsAsciiDigit)) return false;
        return true;
    }

    /// <summary>Bounded LRU map from runtime id to element. Accessed only on the UIA thread (Clear on dispose excepted).</summary>
    private sealed class ElementCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<(string Key, AutomationElement Element)>> _map = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, AutomationElement Element)> _order = new();
        private readonly object _gate = new();

        public ElementCache(int capacity) => _capacity = capacity;

        public AutomationElement? Get(string key)
        {
            lock (_gate)
            {
                if (!_map.TryGetValue(key, out var node)) return null;
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Element;
            }
        }

        public void Put(string key, AutomationElement element)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing)) { _order.Remove(existing); _map.Remove(key); }
                _map[key] = _order.AddFirst((key, element));
                while (_map.Count > _capacity && _order.Last is { } last)
                {
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        public void Remove(string key)
        {
            lock (_gate)
            {
                if (_map.Remove(key, out var node)) _order.Remove(node);
            }
        }

        public void Clear()
        {
            lock (_gate) { _map.Clear(); _order.Clear(); }
        }
    }
}
