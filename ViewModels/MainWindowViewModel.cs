using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicIsland.Widgets;

namespace DynamicIsland.ViewModels;

/// <summary>
/// Thin binding adapter between IslandController and the MainWindow XAML.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty]
    public partial UserControl? ActiveWidget { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial UserControl? Dashboard { get; set; }

    public MainWindowViewModel()
    {
        App.IslandController.ActiveControlChanged += (_, control) =>
            _dispatcherQueue.TryEnqueue(() => ActiveWidget = control);

        App.IslandController.IsExpandedChanged += (_, expanded) =>
            _dispatcherQueue.TryEnqueue(() => {
                IsExpanded = expanded;
                if (expanded && Dashboard == null)
                    Dashboard = new ExpandedDashboard();
            });
    }
}
