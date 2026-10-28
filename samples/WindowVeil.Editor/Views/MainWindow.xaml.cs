using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WindowVeil.Editor.Services;
using WindowVeil.Editor.ViewModels;
using WindowVeil.Geometry;
using WindowVeil.Platform;

namespace WindowVeil.Editor.Views;

/// <summary>
/// The editor window. Code-behind only handles view concerns: the crosshair mouse gesture, tree selection, log
/// auto-scroll, dialogs and orderly shutdown. Everything else is in <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window, IEditorDialogs
{
    private readonly MainViewModel _vm;
    private bool _picking;
    private bool _shutdownComplete;
    private bool _shuttingDown;

    /// <summary>Creates the window.</summary>
    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _vm;
        _vm.AttachView(this);
        _vm.Log.EntriesAppended += OnLogAppended;
        Loaded += async (_, _) =>
        {
            try { await _vm.InitializeAsync(); }
            catch (Exception ex) { _vm.ReportError(ex); }
        };
    }

    // ================================================================ crosshair picker

    private void OnPickerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_picking || !PickerTarget.CaptureMouse()) return;
        _picking = true;
        Mouse.OverrideCursor = Cursors.Cross;
        _vm.BeginPick();
        e.Handled = true;
    }

    private void OnPickerMouseMove(object sender, MouseEventArgs e)
    {
        // Physical pixels from GetCursorPos: converting WPF's DIP position with one DPI factor is wrong across monitors.
        if (_picking && NativeMethods.TryGetCursorPosition(out var p)) _vm.PickMove(p);
    }

    private async void OnPickerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        _picking = false; // before ReleaseMouseCapture, which raises LostMouseCapture synchronously
        PickerTarget.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        e.Handled = true;
        if (!NativeMethods.TryGetCursorPosition(out var p)) { _vm.CancelPick(); return; }
        try { await _vm.EndPickAsync(p); }
        catch (Exception ex) { _vm.ReportError(ex); }
    }

    private void OnPickerLostCapture(object sender, MouseEventArgs e)
    {
        if (!_picking) return;
        _picking = false;
        Mouse.OverrideCursor = null;
        _vm.CancelPick();
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_picking && e.Key == Key.Escape)
        {
            PickerTarget.ReleaseMouseCapture(); // → OnPickerLostCapture → CancelPick
            e.Handled = true;
        }
    }

    // ================================================================ hierarchy

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as ElementNodeViewModel;

    private void OnTreeItemSelected(object sender, RoutedEventArgs e)
    {
        // Keep the revealed / selected node visible.
        if (ReferenceEquals(e.OriginalSource, sender) && sender is TreeViewItem item) item.BringIntoView();
    }

    // ================================================================ diagnostics

    private void OnLogAppended(object? sender, EventArgs e)
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    // ================================================================ IEditorDialogs

    /// <inheritdoc />
    public string? PickOpenProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import WindowVeil profile",
            Filter = "WindowVeil profile (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickSaveProfile(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export WindowVeil profile",
            Filter = "WindowVeil profile (*.json)|*.json",
            FileName = suggestedName,
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException)
        {
            return false; // clipboard held by another process
        }
    }

    /// <inheritdoc />
    public async Task<PixelRect?> DrawRectangleAsync(TargetWindowInfo window)
    {
        // Minimize the editor so it does not hide the target while drawing.
        var previous = WindowState;
        WindowState = WindowState.Minimized;
        try
        {
            return await RectangleSelectionWindow.SelectAsync(window);
        }
        finally
        {
            WindowState = previous == WindowState.Minimized ? WindowState.Normal : previous;
            Activate();
        }
    }

    // ================================================================ shutdown

    /// <inheritdoc />
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownComplete || e.Cancel) return;
        // Dispose the engine (which removes every overlay), sessions and the inspector before the window closes.
        e.Cancel = true;
        if (_shuttingDown) return;
        _shuttingDown = true;
        IsEnabled = false;
        Title = "WindowVeil Editor — closing…";
        _vm.Log.EntriesAppended -= OnLogAppended;
        try
        {
            await _vm.DisposeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        _shutdownComplete = true;
        await Dispatcher.InvokeAsync(Close);
    }
}
