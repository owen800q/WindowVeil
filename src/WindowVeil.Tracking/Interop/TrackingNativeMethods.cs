using System.Runtime.InteropServices;

namespace WindowVeil.Tracking.Interop;

/// <summary>WinEvent declarations (user32). Callbacks are raw pointers to <c>[UnmanagedCallersOnly]</c> methods.</summary>
internal static partial class TrackingNativeMethods
{
    private const string User32 = "user32.dll";

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNTHREAD = 0x0001;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_SCROLLINGEND = 0x0013;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;

    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_REORDER = 0x8004;
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_VALUECHANGE = 0x800E;
    public const uint EVENT_OBJECT_CONTENTSCROLLED = 0x8015;

    public const int OBJID_WINDOW = 0;
    public const int OBJID_CARET = -8;
    public const int OBJID_CURSOR = -9;
    public const int CHILDID_SELF = 0;

    public const uint GA_ROOT = 2;

    /// <param name="eventMin">Lowest event constant.</param>
    /// <param name="eventMax">Highest event constant.</param>
    /// <param name="module">DLL containing the callback; 0 for out-of-context hooks.</param>
    /// <param name="callback">delegate* unmanaged&lt;nint hHook, uint event, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime, void&gt;.</param>
    /// <param name="processId">Process to receive events from, 0 for all.</param>
    /// <param name="threadId">Thread to receive events from, 0 for all.</param>
    /// <param name="flags">WINEVENT_* flags.</param>
    [LibraryImport(User32)]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, nint callback, uint processId, uint threadId, uint flags);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport(User32)]
    public static partial nint GetAncestor(nint hwnd, uint flags);
}
