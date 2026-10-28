using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WindowVeil.DemoTarget;

/// <summary>The demo target's only window. See the XAML for the element inventory.</summary>
public partial class MainWindow : Window
{
    /// <summary>Distance MovingLabel moves per MoveButton click, in DIPs.</summary>
    public const double MoveDistance = 40;

    /// <summary>Number of rows in ScrollArea.</summary>
    public const int RowCount = 40;

    /// <summary>
    /// Deterministic content of NotesBox. "secret token" occurs twice: once on line 2 and once spanning the end of
    /// line 3 and the start of line 4.
    /// </summary>
    public static readonly string NotesText = string.Join("\r\n",
        "Meeting notes (demo)",
        "The secret token is rotated",
        "weekly. Never share the secret",
        "token with anyone outside",
        "the team.");

    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        NotesBox.Text = NotesText;
        for (int i = 1; i <= RowCount; i++)
        {
            var row = new TextBlock { Name = "Row" + i, Text = "Row " + i, Margin = new Thickness(4, 1, 4, 1) };
            AutomationProperties.SetAutomationId(row, "Row" + i);
            ScrollRows.Children.Add(row);
        }
    }

    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        double left = Canvas.GetLeft(MovingLabel);
        Canvas.SetLeft(MovingLabel, left > 0 ? 0 : MoveDistance);
    }

    private void OnToggleVisibilityClick(object sender, RoutedEventArgs e) =>
        CollapsibleLabel.Visibility = CollapsibleLabel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
