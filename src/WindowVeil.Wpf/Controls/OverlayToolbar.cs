using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowVeil.Wpf.Controls;

/// <summary>
/// A horizontal row of buttons for interactive overlays. Each item becomes a <see cref="Button"/>:
/// <see cref="OverlayToolbarItem"/>s are mapped to their content, command and tooltip; <see cref="Button"/> items are used
/// as-is; any other item becomes the content of a button (styled with <see cref="ItemsControl.ItemTemplate"/> if set).
/// Bind <see cref="ItemsControl.ItemsSource"/> to a view-model collection for MVVM.
/// </summary>
/// <example>
/// <code>
/// WpfAppearance.Create(() =&gt; new OverlayToolbar
/// {
///     Items = { new OverlayToolbarItem("Approve", vm.ApproveCommand), new OverlayToolbarItem("Reject", vm.RejectCommand) },
/// })
/// </code>
/// </example>
public class OverlayToolbar : ItemsControl
{
    /// <summary>Identifies the <see cref="ItemSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(OverlayToolbar),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnItemSpacingChanged),
        v => v is double d && d >= 0 && double.IsFinite(d));

    /// <summary>Identifies the <see cref="ButtonPadding"/> dependency property.</summary>
    public static readonly DependencyProperty ButtonPaddingProperty = DependencyProperty.Register(
        nameof(ButtonPadding), typeof(Thickness), typeof(OverlayToolbar), new FrameworkPropertyMetadata(new Thickness(10, 4, 10, 4)));

    /// <summary>Creates an empty toolbar.</summary>
    public OverlayToolbar()
    {
        // Built per instance rather than in static metadata: templates are dispatcher-bound and every overlay thread
        // needs its own.
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        ItemsPanel = new ItemsPanelTemplate(panel);
        Focusable = false;
    }

    /// <summary>Horizontal gap between buttons in DIPs. Default 4.</summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>Padding applied to generated buttons. Default 10,4.</summary>
    public Thickness ButtonPadding
    {
        get => (Thickness)GetValue(ButtonPaddingProperty);
        set => SetValue(ButtonPaddingProperty, value);
    }

    /// <inheritdoc />
    protected override bool IsItemItsOwnContainerOverride(object item) => item is Button;

    /// <inheritdoc />
    protected override DependencyObject GetContainerForItemOverride() => new Button();

    /// <inheritdoc />
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is not Button button) return;
        if (!ReferenceEquals(element, item))
        {
            button.SetBinding(Control.PaddingProperty, new System.Windows.Data.Binding(nameof(ButtonPadding)) { Source = this });
            if (item is OverlayToolbarItem t)
            {
                button.Content = t.Content;
                button.Command = t.Command;
                button.CommandParameter = t.CommandParameter;
                if (t.ToolTip is not null) button.ToolTip = t.ToolTip;
                if (t.AutomationName is not null) System.Windows.Automation.AutomationProperties.SetName(button, t.AutomationName);
            }
        }
        ApplySpacing(button);
    }

    /// <inheritdoc />
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is Button button && !ReferenceEquals(element, item) && item is OverlayToolbarItem)
        {
            button.Command = null;
            button.CommandParameter = null;
        }
        base.ClearContainerForItemOverride(element, item);
    }

    private void ApplySpacing(Button button)
    {
        int index = ItemContainerGenerator.IndexFromContainer(button);
        if (index < 0) index = Items.IndexOf(button);
        button.Margin = new Thickness(index <= 0 ? 0 : ItemSpacing, 0, 0, 0);
    }

    private static void OnItemSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toolbar = (OverlayToolbar)d;
        for (int i = 0; i < toolbar.Items.Count; i++)
        {
            if (toolbar.ItemContainerGenerator.ContainerFromIndex(i) is Button b)
                b.Margin = new Thickness(i == 0 ? 0 : toolbar.ItemSpacing, 0, 0, 0);
        }
    }
}

/// <summary>A button description for <see cref="OverlayToolbar"/>. Plain data, safe to create on any thread.</summary>
public sealed class OverlayToolbarItem
{
    /// <summary>Creates an empty item.</summary>
    public OverlayToolbarItem() { }

    /// <summary>Creates an item with content and an optional command.</summary>
    public OverlayToolbarItem(object content, ICommand? command = null, object? commandParameter = null)
    {
        Content = content;
        Command = command;
        CommandParameter = commandParameter;
    }

    /// <summary>Button content (typically a string).</summary>
    public object? Content { get; set; }

    /// <summary>Command executed when the button is clicked.</summary>
    public ICommand? Command { get; set; }

    /// <summary>Parameter passed to <see cref="Command"/>.</summary>
    public object? CommandParameter { get; set; }

    /// <summary>Optional tooltip text.</summary>
    public string? ToolTip { get; set; }

    /// <summary>Optional UI Automation name (defaults to the content text).</summary>
    public string? AutomationName { get; set; }
}
