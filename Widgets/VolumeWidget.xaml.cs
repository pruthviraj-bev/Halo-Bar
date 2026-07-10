using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.ViewModels;

namespace DynamicIsland.Widgets;

/// <summary>
/// UserControl hosting the Volume Widget UI.
/// </summary>
public sealed partial class VolumeWidget : UserControl, IIslandWidget
{
    public WidgetPriority Priority => WidgetPriority.Volume;
    public bool AutoExpand => false;
    public WindowProfile PreferredProfile => WindowProfile.Collapsed;

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnSuspended() { }
    public void OnResumed() { }

    public VolumeWidgetViewModel ViewModel { get; }

    public VolumeWidget(VolumeState state)
    {
        InitializeComponent();
        ViewModel = new VolumeWidgetViewModel(state);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        string state = e.NewSize.Height > 50 ? "Expanded" : "Collapsed";
        VisualStateManager.GoToState(this, state, false);
    }
}

