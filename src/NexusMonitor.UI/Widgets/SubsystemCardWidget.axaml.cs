using Avalonia.Controls;

namespace NexusMonitor.UI.Widgets;

/// <summary>
/// One live subsystem card (CPU/Memory/Disk/GPU) as a standalone page-engine widget.
/// The factory binds this control's DataContext to the relevant
/// <c>SubsystemCardViewModel</c> instance (e.g. <c>CpuCard</c>) on the dashboard view model;
/// the bindings inside read the card VM's members directly.
/// </summary>
public partial class SubsystemCardWidget : UserControl
{
    /// <summary>Initializes the widget and loads its XAML.</summary>
    public SubsystemCardWidget()
    {
        InitializeComponent();
    }
}
