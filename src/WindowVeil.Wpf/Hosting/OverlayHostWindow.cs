using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowVeil.Geometry;
using DpiScale = WindowVeil.Geometry.DpiScale;
using WindowVeil.Rendering;
using WindowVeil.Wpf.Interop;

namespace WindowVeil.Wpf.Hosting;

/// <summary>
/// The layered, borderless, non-activating top-level window that hosts overlay content (one overlay, or a group).
/// Created, positioned and destroyed on its dispatcher thread only.
/// </summary>
/// <remarks>
/// Positioning bypasses <see cref="Window.Left"/>/<see cref="Window.Top"/>/<see cref="FrameworkElement.Width"/>: those are
/// DIPs relative to the DPI of the monitor the window is <i>currently</i> on, which is wrong as soon as the window moves to a
/// monitor with a different scale. Bounds are applied in physical pixels with <c>SetWindowPos</c>, and the content root is
/// sized in DIPs from the scale WPF actually renders at.
/// </remarks>
internal sealed class OverlayHostWindow : Window
{
    private readonly OverlayInputMode _inputMode;
    private readonly OverlayFocusPolicy _focusPolicy;
    private HwndSource? _source;

    public OverlayHostWindow(OverlayInputMode inputMode, OverlayFocusPolicy focusPolicy, OverlayZOrder zOrder, Brush? background)
    {
        _inputMode = inputMode;
        _focusPolicy = focusPolicy;
        ZOrder = zOrder;

        Title = "WindowVeil Overlay";
        WindowStyle = WindowStyle.None;
        // Layered window updated with UpdateLayeredWindow: per-pixel alpha, and pixels with alpha 0 are not hit-tested,
        // so transparent areas of an interactive overlay still let clicks reach the window underneath.
        AllowsTransparency = true;
        Background = background;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        ShowActivated = false;
        // ShowInTaskbar = false would make WPF parent the window to a hidden owner window, and an owned window can never be
        // placed below its owner in the z-order, which interferes with "directly above the target" placement.
        // WS_EX_TOOLWINDOW (added in OnSourceInitialized, before the first show) keeps the window out of the taskbar and
        // Alt+Tab without an owner.
        ShowInTaskbar = true;
        Topmost = zOrder == OverlayZOrder.Topmost;
        Focusable = inputMode == OverlayInputMode.Interactive && focusPolicy == OverlayFocusPolicy.ActivateOnClick;
        UseLayoutRounding = false;
    }

    public OverlayZOrder ZOrder { get; }

    /// <summary>The HWND (0 until <see cref="EnsureCreated"/>).</summary>
    public nint Handle { get; private set; }

    /// <summary>The physical bounds last applied with <c>SetWindowPos</c>.</summary>
    public PixelRect? AppliedBounds { get; set; }

    /// <summary>Re-lays out the content for a scale; set by the owner (single overlay or group).</summary>
    public Action<double>? Relayout { get; set; }

    /// <summary>Whether <see cref="Window.Closed"/> has been raised.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Whether the window has been shown at least once (and not hidden since).</summary>
    public bool IsShown { get; private set; }

    /// <summary>Creates the HWND without showing the window (raises SourceInitialized).</summary>
    public nint EnsureCreated()
    {
        if (Handle == 0) Handle = new WindowInteropHelper(this).EnsureHandle();
        return Handle;
    }

    /// <summary>
    /// The scale WPF renders this window at (device pixels per DIP). Normally equals the target monitor's scale; it differs
    /// when the process is not per-monitor aware, in which case sizing from it keeps the content filling the HWND exactly.
    /// </summary>
    public double RenderScale(DpiScale fallback)
    {
        var m = _source?.CompositionTarget?.TransformToDevice.M11 ?? 0;
        return m > 0 && double.IsFinite(m) ? m : fallback.Scale;
    }

    /// <summary>Shows the window without activating it.</summary>
    public void ShowNoActivate()
    {
        if (IsShown || IsClosed) return;
        // ShowActivated == false makes WPF use SW_SHOWNA: the window appears at its current z-order position without
        // becoming active or foreground.
        Show();
        IsShown = true;
    }

    /// <summary>Hides the window.</summary>
    public void HideWindow()
    {
        if (!IsShown || IsClosed) return;
        Hide();
        IsShown = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Handle = hwnd;
        long ex = NativeMethods.GetExStyle(hwnd);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        // A WS_EX_NOACTIVATE window is never activated by clicks and never becomes the foreground window, so the target
        // application keeps activation and keyboard focus.
        if (_focusPolicy == OverlayFocusPolicy.NoActivate) ex |= NativeMethods.WS_EX_NOACTIVATE;
        // WS_EX_LAYERED | WS_EX_TRANSPARENT: the window is skipped entirely by hit-testing, so every mouse message goes to
        // the window below (the target application).
        if (_inputMode == OverlayInputMode.PassThrough) ex |= NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetExStyle(hwnd, ex);
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE when _focusPolicy == OverlayFocusPolicy.NoActivate:
                // Clicking an interactive control must not take activation from the target application. Buttons still
                // receive the mouse messages and raise Click / execute their command.
                handled = true;
                return NativeMethods.MA_NOACTIVATE;

            case NativeMethods.WM_GETMINMAXINFO when lParam != 0:
                // Window answers this with a minimum track size derived from SM_CXMINTRACK/SM_CYMINTRACK, which would
                // clamp small overlays (badges, icons) when SetWindowPos sizes them. Overlays are sized exactly as asked.
                Marshal.WriteInt32(lParam, NativeMethods.MINMAXINFO_MinTrackX, 1);
                Marshal.WriteInt32(lParam, NativeMethods.MINMAXINFO_MinTrackY, 1);
                Marshal.WriteInt32(lParam, NativeMethods.MINMAXINFO_MaxTrackX, short.MaxValue);
                Marshal.WriteInt32(lParam, NativeMethods.MINMAXINFO_MaxTrackY, short.MaxValue);
                handled = true;
                return 0;

            case NativeMethods.WM_DPICHANGED when AppliedBounds is { IsEmpty: false } b && lParam != 0:
                // Public hooks run before WPF's own handling, which resizes the window to the suggested rectangle. Replace
                // the suggestion with the bounds we own so WPF updates its DPI but keeps our physical placement, then
                // re-size the content for the new render scale once WPF has processed the change.
                Marshal.StructureToPtr(new NativeMethods.RECT { Left = b.X, Top = b.Y, Right = b.Right, Bottom = b.Bottom }, lParam, false);
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ReapplyAfterDpiChange));
                break;
        }
        return 0;
    }

    private void ReapplyAfterDpiChange()
    {
        if (IsClosed || AppliedBounds is not { IsEmpty: false } b) return;
        if (NativeMethods.TryGetWindowRect(Handle) is { } current && current != b)
            NativeMethods.SetWindowPos(Handle, 0, b.X, b.Y, b.Width, b.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        Relayout?.Invoke(RenderScale(DpiScale.Default));
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        IsShown = false;
        _source?.RemoveHook(WndProc);
        _source = null;
        base.OnClosed(e);
    }
}
