using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowVeil.Native.Interop;

/// <summary>
/// Reference-counted, process-wide window class registration. Window classes belong to the module (process), not to a
/// thread, so several renderers or message threads in one process share one registration; the class is unregistered
/// when the last user releases it.
/// </summary>
internal static class WindowClassRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> RefCounts = new(StringComparer.Ordinal);

    public static nint ModuleHandle { get; } = NativeMethods.GetModuleHandle(null);

    /// <summary>Registers the class if needed and increments its reference count.</summary>
    /// <exception cref="Win32Exception">RegisterClassEx failed.</exception>
    public static unsafe void Acquire(string className, nint wndProc, nint cursor = 0)
    {
        lock (Gate)
        {
            RefCounts.TryGetValue(className, out int count);
            if (count == 0)
            {
                fixed (char* name = className)
                {
                    var wc = new WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(WNDCLASSEXW),
                        lpfnWndProc = wndProc,
                        hInstance = ModuleHandle,
                        hCursor = cursor,
                        // No background brush: WM_ERASEBKGND/WM_PAINT are handled explicitly.
                        hbrBackground = 0,
                        lpszClassName = (nint)name,
                    };
                    if (NativeMethods.RegisterClassEx(in wc) == 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        // Another component (or a previous registration whose unregister failed because windows still
                        // existed) already registered it with the same static window procedure: reuse it.
                        if (error != NativeMethods.ERROR_CLASS_ALREADY_EXISTS) throw new Win32Exception(error);
                    }
                }
            }
            RefCounts[className] = count + 1;
        }
    }

    /// <summary>Decrements the reference count and unregisters the class when it reaches zero.</summary>
    public static void Release(string className)
    {
        lock (Gate)
        {
            if (!RefCounts.TryGetValue(className, out int count) || count <= 0) return;
            count--;
            if (count == 0)
            {
                RefCounts.Remove(className);
                // Fails with ERROR_CLASS_HAS_WINDOWS while windows of the class still exist; harmless, the next
                // Acquire then sees ERROR_CLASS_ALREADY_EXISTS and reuses the registration.
                NativeMethods.UnregisterClass(className, ModuleHandle);
            }
            else
            {
                RefCounts[className] = count;
            }
        }
    }
}
