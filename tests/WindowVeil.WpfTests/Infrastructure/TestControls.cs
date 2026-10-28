using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowVeil.WpfTests.Infrastructure;

/// <summary>A custom UserControl with a known, opaque background color.</summary>
public sealed class SolidUserControl : UserControl
{
    public static readonly Color Fill = Color.FromRgb(0x12, 0xA4, 0x5E);

    public SolidUserControl()
    {
        var brush = new SolidColorBrush(Fill);
        brush.Freeze();
        Content = new Border
        {
            Background = brush,
            Child = new TextBlock { Text = "Custom control", Margin = new Thickness(6), Foreground = Brushes.White },
        };
        Width = 160;
        Height = 60;
    }
}
