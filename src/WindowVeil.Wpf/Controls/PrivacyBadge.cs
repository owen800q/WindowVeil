using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace WindowVeil.Wpf.Controls;

/// <summary>
/// A compact badge with a padlock glyph and a caption, for marking masked or private regions
/// (<c>WpfAppearance.Create(() =&gt; new PrivacyBadge { Text = "Hidden" })</c>).
/// </summary>
/// <remarks>
/// <see cref="Control.Background"/>, <see cref="Control.BorderBrush"/>, <see cref="Control.BorderThickness"/>,
/// <see cref="Control.Foreground"/> (glyph and text), <see cref="Control.Padding"/> and the font properties are honored.
/// Defined in code (no XAML) so the package has no BAML resources.
/// </remarks>
public class PrivacyBadge : UserControl
{
    /// <summary>Padlock outline (16x16 units): body with a keyhole, plus the shackle. Even-odd so the keyhole is a hole.</summary>
    private const string LockGlyphData =
        "F0 M2,7 L14,7 L14,16 L2,16 Z M4.5,7 L4.5,5 A3.5,3.5 0 0 1 11.5,5 L11.5,7 L10,7 L10,5 A2,2 0 0 0 6,5 L6,7 Z M7.25,10 L8.75,10 L8.75,13 L7.25,13 Z";

    /// <summary>Identifies the <see cref="Text"/> dependency property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PrivacyBadge), new FrameworkPropertyMetadata("Private", FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Identifies the <see cref="CornerRadius"/> dependency property.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(PrivacyBadge), new FrameworkPropertyMetadata(new CornerRadius(4)));

    /// <summary>Identifies the <see cref="GlyphSize"/> dependency property.</summary>
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(PrivacyBadge),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && d >= 0 && double.IsFinite(d));

    /// <summary>Creates a badge with a dark translucent background and white foreground.</summary>
    public PrivacyBadge()
    {
        Background = Frozen(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));
        BorderBrush = Frozen(Color.FromArgb(0xFF, 0x60, 0x60, 0x60));
        BorderThickness = new Thickness(1);
        Foreground = Brushes.White;
        Padding = new Thickness(8, 3, 8, 3);
        FontSize = 12;
        FontWeight = FontWeights.SemiBold;
        SnapsToDevicePixels = true;

        var glyph = new Path
        {
            Data = System.Windows.Media.Geometry.Parse(LockGlyphData),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        glyph.SetBinding(Shape.FillProperty, Bind(nameof(Foreground)));
        glyph.SetBinding(WidthProperty, Bind(nameof(GlyphSize)));
        glyph.SetBinding(HeightProperty, Bind(nameof(GlyphSize)));

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.SetBinding(TextBlock.TextProperty, Bind(nameof(Text)));

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(glyph);
        panel.Children.Add(text);

        var border = new Border { Child = panel };
        border.SetBinding(Border.BackgroundProperty, Bind(nameof(Background)));
        border.SetBinding(Border.BorderBrushProperty, Bind(nameof(BorderBrush)));
        border.SetBinding(Border.BorderThicknessProperty, Bind(nameof(BorderThickness)));
        border.SetBinding(Border.PaddingProperty, Bind(nameof(Padding)));
        border.SetBinding(Border.CornerRadiusProperty, Bind(nameof(CornerRadius)));
        Content = border;
    }

    /// <summary>Caption shown next to the padlock. Default "Private".</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Corner radius of the badge. Default 4.</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Size of the padlock glyph in DIPs. Default 14.</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    private Binding Bind(string path) => new(path) { Source = this };

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
