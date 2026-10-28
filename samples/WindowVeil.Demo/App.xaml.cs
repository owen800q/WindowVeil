using System.Globalization;
using System.Windows;

namespace WindowVeil.Demo;

/// <summary>
/// Starts the demo UI; with <c>--demo-target [--left X --top Y]</c> the bundled target window (in its own process);
/// with <c>--screenshots DIR</c> an automated before/after capture of the target with WindowVeil overlays.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Arg(string name) { int i = Array.IndexOf(e.Args, name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }

        if (Arg("--screenshots") is { } dir)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await ScreenshotTour.RunAsync(dir);
            Shutdown(code);
            return;
        }

        Window window = e.Args.Contains("--demo-target")
            ? new DemoTarget.MainWindow
            {
                Left = double.Parse(Arg("--left") ?? "760", CultureInfo.InvariantCulture),
                Top = double.Parse(Arg("--top") ?? "120", CultureInfo.InvariantCulture),
                Title = "WindowVeil Demo Target",
            }
            : new DemoWindow();
        MainWindow = window;
        window.Show();
    }
}
