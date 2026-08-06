using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.Services;
using DynamicIsland.ViewModels;

namespace DynamicIsland.Widgets;

public sealed partial class ClipboardWidget : UserControl, IIslandWidget
{
    private DispatcherQueueTimer? _hoverTimer;
    private bool _isWidgetExpanded = false;

    // ── IIslandWidget ──────────────────────────────────────────────────────

    public WidgetPriority Priority => WidgetPriority.Clipboard;

    // Clipboard starts collapsed now.
    public bool AutoExpand => false;

    public WindowProfile PreferredProfile => WindowProfile.Collapsed;

    public void OnActivated()
    {
        // Set collapsed state on activation
        _isWidgetExpanded = false;
        VisualStateManager.GoToState(this, "Collapsed", false);
    }

    public void OnDeactivated()
    {
        _hoverTimer?.Stop();
    }

    public void OnSuspended() { }
    public void OnResumed() { }

    // ── Widget ─────────────────────────────────────────────────────────────

    public ClipboardWidgetViewModel ViewModel { get; }

    public ClipboardWidget(ClipboardItem item)
    {
        InitializeComponent();
        ViewModel = new ClipboardWidgetViewModel(item);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(400); // 400ms hover delay
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (s, ev) =>
        {
            ExpandWidget();
        };
        _hoverTimer.Start();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;

        // If hovered out, return to collapsed capsule
        if (_isWidgetExpanded)
        {
            CollapseWidget();
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            _hoverTimer?.Stop();
            ExpandWidget();
            e.Handled = true;
        }
    }

    private void ExpandWidget()
    {
        if (_isWidgetExpanded) return;
        _isWidgetExpanded = true;

        VisualStateManager.GoToState(this, "Expanded", true);

        // LEGACY (deferred to a later phase): widget-level preview that resizes
        // the window to 320×180. Will move to the dashboard/overlay system.
        App.WindowService.StartSizeAnimation(320, 180);
    }

    private void CollapseWidget()
    {
        if (!_isWidgetExpanded) return;
        _isWidgetExpanded = false;

        VisualStateManager.GoToState(this, "Collapsed", true);

        // Phase 1: compact pill geometry is fixed — collapse back to the single
        // compact size (width token × taskbar height). No more width tiers.
        var (width, height) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(width, height);
    }
}
