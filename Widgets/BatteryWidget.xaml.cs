using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.ViewModels;

namespace DynamicIsland.Widgets;

/// <summary>
/// UserControl hosting the Battery Widget UI.
/// Implements IIslandWidget so IslandController can manage its lifecycle.
/// Priority 15 — higher than Media but lower than Clipboard. AutoExpand = true so the window opens
/// immediately on activation without requiring a hover gesture.
/// </summary>
public sealed partial class BatteryWidget : UserControl, IIslandWidget
{
    // ── IIslandWidget ──────────────────────────────────────────────────────

    public WidgetPriority Priority => WidgetPriority.Battery;

    /// <summary>Battery expands immediately — it is a transient notification.</summary>
    public bool AutoExpand => true;

    public WindowProfile PreferredProfile => WindowProfile.Expanded;

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnSuspended() { }
    public void OnResumed() { }

    // ── Widget ─────────────────────────────────────────────────────────────

    public BatteryWidgetViewModel ViewModel { get; }

    public BatteryWidget(BatteryState state)
    {
        InitializeComponent();
        ViewModel = new BatteryWidgetViewModel(state);
        
        if (ViewModel.IsCharging)
        {
            try { PulseStoryboard?.Begin(); } catch {}
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        string state = e.NewSize.Height > 50 ? "Expanded" : "Collapsed";
        VisualStateManager.GoToState(this, state, false);
    }
}
