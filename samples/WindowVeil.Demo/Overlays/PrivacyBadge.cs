using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WindowVeil.Demo.Overlays;

/// <summary>A small "privacy" badge with a shield icon. Shows a bound text and a pulsing border animation.</summary>
public sealed class PrivacyBadge : Border
{
    /// <summary>Creates the badge.</summary>
    public PrivacyBadge(string text)
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
        BorderBrush = Brushes.White;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 2, 10, 2);
        var shield = new Path
        {
            Data = Geometry.Parse("M6,0 L12,2 V7 C12,10.5 9.5,13 6,14 C2.5,13 0,10.5 0,7 V2 Z"),
            Fill = Brushes.White,
            Width = 10, Height = 12, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BadgeModel.Text)));
        DataContext = new BadgeModel { Text = text };
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { shield, label } };

        var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.75, TimeSpan.FromSeconds(1.2))
        { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        Loaded += (_, _) => BeginAnimation(OpacityProperty, pulse);
    }

    /// <summary>View model bound by the badge (data binding demo).</summary>
    public sealed class BadgeModel : ObservableObject
    {
        private string _text = "";

        /// <summary>Badge text.</summary>
        public string Text { get => _text; set => Set(ref _text, value); }
    }
}
