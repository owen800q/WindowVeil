using System.Windows.Controls;

namespace WindowVeil.Editor.Overlays;

/// <summary>A sample custom overlay (registered as <c>sample-card</c> in the editor gallery).</summary>
public partial class SampleCardOverlay : UserControl
{
    /// <summary>Creates the card.</summary>
    public SampleCardOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Creates the card with custom texts.</summary>
    public SampleCardOverlay(string title, string body)
        : this()
    {
        TitleText.Text = title;
        BodyText.Text = body;
    }
}
