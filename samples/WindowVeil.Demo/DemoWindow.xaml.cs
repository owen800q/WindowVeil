using System.Windows;
using System.Windows.Controls;

namespace WindowVeil.Demo;

/// <summary>Main demo window.</summary>
public partial class DemoWindow : Window
{
    private readonly DemoViewModel _vm = new();

    /// <summary>Creates the window.</summary>
    public DemoWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closed += async (_, _) => await _vm.DisposeAsync();
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
