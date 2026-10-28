using System.Windows;

namespace WindowVeil.Demo;

/// <summary>
/// Starts either the demo UI or, with <c>--demo-target</c>, the bundled target window (in its own process).
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Window window = e.Args.Contains("--demo-target")
            ? new DemoTarget.MainWindow { Left = 760, Top = 120, Title = "WindowVeil Demo Target" }
            : new DemoWindow();
        MainWindow = window;
        window.Show();
    }
}
