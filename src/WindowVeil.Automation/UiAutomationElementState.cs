using System.Windows.Automation;
using WindowVeil.Locators;

namespace WindowVeil.Automation;

/// <summary>
/// Opaque resolver state stored in <see cref="LocatorResolution.State"/>: the UI Automation element found by the
/// previous resolution, so the next one can re-validate it instead of searching the tree again.
/// Disposing releases the element reference (the session does this when the rule is removed or re-resolved).
/// </summary>
internal sealed class UiAutomationElementState : IDisposable
{
    private volatile AutomationElement? _element;

    public UiAutomationElementState(AutomationElement element, ElementLocator locator, nint windowHandle, string runtimeId)
    {
        _element = element;
        Locator = locator;
        WindowHandle = windowHandle;
        RuntimeId = runtimeId;
    }

    /// <summary>The cached element, or null once disposed.</summary>
    public AutomationElement? Element => _element;

    /// <summary>The locator the element was found for.</summary>
    public ElementLocator Locator { get; }

    /// <summary>The top-level window the element was found in.</summary>
    public nint WindowHandle { get; }

    /// <summary>Runtime id of the element at the time it was found (dotted form).</summary>
    public string RuntimeId { get; }

    /// <summary>True once disposed.</summary>
    public bool IsDisposed => _element is null;

    /// <inheritdoc />
    public void Dispose() => _element = null;

    /// <inheritdoc />
    public override string ToString() => $"UIA element {RuntimeId} in 0x{WindowHandle:X}{(IsDisposed ? " (released)" : "")}";
}
