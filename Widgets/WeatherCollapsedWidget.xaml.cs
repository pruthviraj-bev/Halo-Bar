using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;

namespace DynamicIsland.Widgets;

public sealed partial class WeatherCollapsedWidget : UserControl, IIslandWidget
{
    public WidgetPriority Priority => WidgetPriority.Default;
    public bool AutoExpand => false;
    public WindowProfile PreferredProfile => WindowProfile.Collapsed;

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnSuspended() { }
    public void OnResumed() { }

    public WeatherCollapsedWidget()
    {
        InitializeComponent();
    }
}
