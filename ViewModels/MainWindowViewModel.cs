using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicIsland.Widgets;

namespace DynamicIsland.ViewModels;

/// <summary>
/// Thin binding adapter between IslandController and the MainWindow XAML.
/// This VM is a read-only projection of controller state — it never mutates the
/// controller and holds no source-of-truth data of its own.
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

    // Observable Visibility projections of IsExpanded, so the XAML never needs a
    // function x:Bind (which does not track its observable arguments).
    [ObservableProperty]
    public partial Visibility ExpandedVisibility { get; set; }

    [ObservableProperty]
    public partial Visibility PillVisibility { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        ExpandedVisibility = value ? Visibility.Visible : Visibility.Collapsed;
        PillVisibility    = value ? Visibility.Collapsed : Visibility.Visible;
    }

    public MainWindowViewModel()
    {
        InitVisibilities();

        Subscribe();
        SyncFromController();
    }

    private void InitVisibilities()
    {
        ExpandedVisibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        PillVisibility    = IsExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Subscribe()
    {
        App.IslandController.ActiveControlChanged += (_, control) =>
            _dispatcherQueue.TryEnqueue(() =>
            {
                ActiveWidget = control;
            });

        App.IslandController.IsExpandedChanged += (_, expanded) =>
            _dispatcherQueue.TryEnqueue(() =>
            {
                IsExpanded = expanded;
                if (expanded && Dashboard == null)
                    Dashboard = new ExpandedDashboard();
            });
    }

    /// <summary>
    /// Pulls the controller's current state once. Run only after Subscribe(). Reconciles
    /// anything the controller published before this VM existed (e.g. the default widget
    /// pushed in the IslandController constructor). Extend here as the projected state
    /// grows (ActiveWidget, IsExpanded, Priority, badges, ...) — one place to update.
    /// </summary>
    private void SyncFromController()
    {
        var current = App.IslandController.CurrentControl;
        ActiveWidget = current;
    }
}
