using System.Globalization;
using System.Windows;

namespace WindowVeil.DemoTarget;

/// <summary>
/// Demo target application. Command line:
/// <c>--left N --top N</c> window position in DIPs (default 100, 100), <c>--title X</c> window title
/// (default "WindowVeil Demo Target"), <c>--topmost</c> keeps the window above others (useful for hit-test tests).
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = DemoOptions.Parse(e.Args);
        var window = new MainWindow
        {
            Left = options.Left,
            Top = options.Top,
            Title = options.Title,
            Topmost = options.Topmost,
        };
        MainWindow = window;
        window.Show();
    }
}

/// <summary>Parsed command line options.</summary>
internal sealed record DemoOptions(double Left, double Top, string Title, bool Topmost)
{
    public const string DefaultTitle = "WindowVeil Demo Target";

    public static DemoOptions Parse(string[] args)
    {
        double left = 100, top = 100;
        string title = DefaultTitle;
        bool topmost = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a.ToLowerInvariant())
            {
                case "--left" when next is not null:
                    left = double.Parse(next, NumberStyles.Float, CultureInfo.InvariantCulture); i++; break;
                case "--top" when next is not null:
                    top = double.Parse(next, NumberStyles.Float, CultureInfo.InvariantCulture); i++; break;
                case "--title" when next is not null:
                    title = next; i++; break;
                case "--topmost":
                    topmost = true; break;
            }
        }
        return new DemoOptions(left, top, title, topmost);
    }
}
