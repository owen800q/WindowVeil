using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowVeil.Demo.Overlays;

/// <summary>An interactive toolbar added to the external window. Buttons run ICommands in the demo process.</summary>
public sealed class FloatingToolbar : Border
{
    /// <summary>Creates the toolbar with (caption, command) buttons.</summary>
    public FloatingToolbar(params (string Caption, ICommand Command)[] buttons)
    {
        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x32, 0x38));
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(6, 4, 6, 4);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "WindowVeil", Foreground = Brushes.LightGray, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0),
        });
        foreach (var (caption, command) in buttons)
            panel.Children.Add(new Button { Content = caption, Command = command, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand });
        Child = panel;
    }
}
