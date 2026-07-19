using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusMonitor.UI.Views;

/// <summary>
/// Generic OK/Cancel confirmation dialog. Introduced for the batch-kill confirmation
/// (process-table multi-select, N &gt;= 2) — the single-process kill flow has never shown a
/// confirmation dialog and continues not to.
/// </summary>
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialogViewModel ViewModel { get; }

    public ConfirmationDialog(string title, string message)
    {
        ViewModel = new ConfirmationDialogViewModel(title, message);
        DataContext = ViewModel;
        InitializeComponent();
    }

    // parameterless constructor for XAML designer
    public ConfirmationDialog() : this("Confirm", "Are you sure?") { }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

public partial class ConfirmationDialogViewModel : ObservableObject
{
    public string Title   { get; }
    public string Message { get; }

    public ConfirmationDialogViewModel(string title, string message)
    {
        Title   = title;
        Message = message;
    }
}
