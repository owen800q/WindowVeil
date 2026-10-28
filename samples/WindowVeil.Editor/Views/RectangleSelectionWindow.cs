using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.Services;
using WindowVeil.Geometry;
using WindowVeil.Platform;

namespace WindowVeil.Editor.Views;

/// <summary>
/// The "Draw rectangle" tool: a borderless, slightly tinted, topmost window placed exactly over the target window, in
/// which the user drags a rectangle.
/// </summary>
/// <remarks>
/// <para>Coordinates: the window is positioned in PHYSICAL pixels with <c>SetWindowPos</c> (not with Left/Top in DIPs,
/// which depend on the DPI WPF assumes for the window and are wrong across mixed-DPI monitors). The resulting rectangle
/// is computed from <c>GetCursorPos</c> at mouse-down and mouse-up (physical pixels in this Per-Monitor-V2 process) and
/// clamped to the target bounds. DIPs (<see cref="MouseEventArgs.GetPosition"/>) are only used to draw the rubber band.</para>
/// <para>Esc or a right click cancels.</para>
/// </remarks>
public sealed class RectangleSelectionWindow : Window
{
    private readonly PixelRect _area;
    private readonly TaskCompletionSource<PixelRect?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Canvas _canvas = new();
    private readonly Rectangle _band;
    private readonly TextBlock _sizeLabel;
    private PixelPoint _startPhysical;
    private Point _startLocal;
    private bool _dragging;

    private RectangleSelectionWindow(TargetWindowInfo target)
    {
        _area = target.Bounds;
        double scale = target.Dpi.Scale > 0 ? target.Dpi.Scale : 1;
        var approx = ScreenGeometry.PhysicalToDip(_area, scale);

        Title = "Draw a rectangle";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Approximate placement so the window is created on the right monitor; SetWindowPos then makes it exact.
        Left = approx.X; Top = approx.Y; Width = Math.Max(1, approx.Width); Height = Math.Max(1, approx.Height);
        Background = new SolidColorBrush(Color.FromArgb(0x38, 0x11, 0x18, 0x27)); // must not be fully transparent: it has to receive the mouse
        Cursor = Cursors.Cross;
        FontFamily = new FontFamily("Segoe UI");

        _band = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x25, 0x63, 0xEB)),
            Visibility = Visibility.Collapsed,
        };
        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x18, 0x27)),
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 11,
            Visibility = Visibility.Collapsed,
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x11, 0x18, 0x27)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                Text = $"Drag a rectangle over \"{target.Title}\" · Esc or right-click cancels",
            },
        };
        Canvas.SetLeft(hint, 12);
        Canvas.SetTop(hint, 12);
        _canvas.Children.Add(hint);
        _canvas.Children.Add(_band);
        _canvas.Children.Add(_sizeLabel);
        Content = _canvas;

        SourceInitialized += (_, _) => PlaceExactly();
        ContentRendered += (_, _) => { PlaceExactly(); Activate(); Focus(); };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>Shows the tool over <paramref name="target"/> and returns the rectangle in physical screen pixels (null = cancelled).</summary>
    public static Task<PixelRect?> SelectAsync(TargetWindowInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var window = new RectangleSelectionWindow(target);
        window.Show();
        return window._result.Task;
    }

    private void PlaceExactly()
    {
        // A DPI change while moving to the target monitor can make WPF resize the window; re-applying the physical
        // rectangle once the content is rendered keeps it exact.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            NativeMethods.SetWindowPos(hwnd, 0, _area.X, _area.Y, _area.Width, _area.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!NativeMethods.TryGetCursorPosition(out _startPhysical)) return;
        _startLocal = e.GetPosition(_canvas);
        _dragging = CaptureMouse();
        UpdateBand(_startLocal, _startPhysical);
        _band.Visibility = Visibility.Visible;
        _sizeLabel.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || !NativeMethods.TryGetCursorPosition(out var physical)) return;
        UpdateBand(e.GetPosition(_canvas), physical);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (!NativeMethods.TryGetCursorPosition(out var end)) return;
        var rect = ScreenGeometry.Clamp(ScreenGeometry.FromCorners(_startPhysical, end), _area);
        if (rect.Width < 3 || rect.Height < 3)
        {
            // A click, not a drag: let the user try again.
            _band.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _result.TrySetResult(rect);
        Close();
    }

    /// <inheritdoc />
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Cancel();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Cancel();
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    private void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }

    private void UpdateBand(Point local, PixelPoint physical)
    {
        double x = Math.Min(local.X, _startLocal.X), y = Math.Min(local.Y, _startLocal.Y);
        Canvas.SetLeft(_band, x);
        Canvas.SetTop(_band, y);
        _band.Width = Math.Abs(local.X - _startLocal.X);
        _band.Height = Math.Abs(local.Y - _startLocal.Y);
        var r = ScreenGeometry.FromCorners(_startPhysical, physical);
        _sizeLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{r.Width} × {r.Height} px");
        Canvas.SetLeft(_sizeLabel, x);
        Canvas.SetTop(_sizeLabel, Math.Max(0, y - 20));
    }
}
