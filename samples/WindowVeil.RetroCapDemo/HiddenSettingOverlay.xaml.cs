using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowVeil.RetroCapDemo;

/// <summary>
/// A WPF replacement shown over an external setting: lock icon, "Setting Hidden" label, custom background,
/// border and padding. Used by Demo B through <c>WpfAppearance.Create(() => new HiddenSettingOverlay())</c>.
/// </summary>
public partial class HiddenSettingOverlay : UserControl
{
    /// <summary>Background color used by the overlay; the UI tests look for it in screenshots.</summary>
    public static readonly Color BackgroundColor = Color.FromRgb(0x2B, 0x34, 0x40);

    /// <summary>Label text.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(HiddenSettingOverlay), new PropertyMetadata("Setting Hidden"));

    /// <summary>Creates the overlay.</summary>
    public HiddenSettingOverlay() => InitializeComponent();

    /// <summary>Label text (default "Setting Hidden").</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
