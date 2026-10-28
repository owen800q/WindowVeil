using System.Windows;
using System.Windows.Threading;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.ViewModels;
using WindowVeil.Editor.Views;

namespace WindowVeil.Editor;

/// <summary>Editor entry point.</summary>
public partial class App : Application
{
    private MainViewModel? _viewModel;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _viewModel = new MainViewModel(Dispatcher);
        }
        catch (Exception ex)
        {
            MessageBox.Show("WindowVeil could not start: " + ErrorMessages.Describe(ex), "WindowVeil Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never crash on a UI bug: report it in the diagnostics panel.
        if (_viewModel is null) return;
        _viewModel.ReportError(e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _viewModel?.ReportError(e.Exception);
        e.SetObserved();
    }
}
