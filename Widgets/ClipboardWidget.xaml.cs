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
    private DispatcherQueueTimer? _leaveTimer;
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
        _leaveTimer?.Stop();

        // The legacy preview resizes the window on expand, so the pop
        // path (Dismiss / Delete / auto-dismiss) must hand the window back to the
        // compact pill — otherwise it stays blown up after the widget is gone.
        if (_isWidgetExpanded)
        {
            _isWidgetExpanded = false;
            VisualStateManager.GoToState(this, "Collapsed", true);
            var (width, height) = App.WindowService.CompactSize;
            App.WindowService.StartSizeAnimation(width, height);
        }
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

    /// <summary>
    /// Hover-enter from the region monitor (PASS 35). The stable-window
    /// per-frame SetWindowRgn can drop the XAML PointerEntered, so MainWindow's
    /// region-accurate hover monitor is the hover authority and feeds this
    /// method directly. Idempotent: a repeat enter while the countdown is
    /// already running (or the preview already expanded) does not restart it.
    /// </summary>
    public void NotifyHoverEnter()
    {
        if (_isWidgetExpanded)
        {
            // Already expanded — the cursor is back over the preview; cancel
            // any pending collapse so it stays open while hovered.
            _leaveTimer?.Stop();
            _leaveTimer = null;
            return;
        }
        if (_hoverTimer != null) return; // countdown already running

        // While the cursor is over the pill it must never time out; re-entering
        // the area also cancels any pending collapse.
        App.IslandController.CancelAutoDismiss();
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(400); // 400ms hover delay
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (s, ev) =>
        {
            ExpandWidget();
        };
        _hoverTimer.Start();
    }

    /// <summary>
    /// Hover-leave from the region monitor (PASS 35) — the mirror of
    /// <see cref="NotifyHoverEnter"/>.
    /// </summary>
    public void NotifyHoverExit()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;

        if (_isWidgetExpanded)
        {
            // The big pill has NO countdown — it stays while the cursor is present.
            // Grace period so the cursor can actually reach the action buttons (and
            // so the window resize while expanding can't flap-flicker the pill shut).
            _leaveTimer?.Stop();
            _leaveTimer = DispatcherQueue.CreateTimer();
            _leaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _leaveTimer.IsRepeating = false;
            _leaveTimer.Tick += (s, ev) =>
            {
                CollapseWidget();
            };
            _leaveTimer.Start();
        }
        else
        {
            // Small transient pill: re-arm the 2s auto-dismiss once the cursor leaves.
            App.IslandController.RenewAutoDismiss(NotificationDuration.Short);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        => NotifyHoverEnter();

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        => NotifyHoverExit();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Expansion is hover-only. Swallow the press so clicking the pill never
        // bubbles into the island's click-to-expand dashboard toggle.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
        }
    }

    private void ExpandWidget()
    {
        if (_isWidgetExpanded) return;
        _isWidgetExpanded = true;

        _hoverTimer?.Stop();
        _leaveTimer?.Stop();

        VisualStateManager.GoToState(this, "Expanded", true);

        // LEGACY (deferred to a later phase): widget-level preview that resizes
        // the window. Width matches the compact pill; only the height grows.
        var (width, _) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(width, 180);
    }

    private void CollapseWidget()
    {
        if (!_isWidgetExpanded) return;
        _isWidgetExpanded = false;

        _leaveTimer?.Stop();

        VisualStateManager.GoToState(this, "Collapsed", true);

        // Phase 1: compact pill geometry is fixed — collapse back to the single
        // compact size (width token × taskbar height). No more width tiers.
        var (width, height) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(width, height);

        // The big pill is gone; the small pill may now auto-dismiss (2s).
        App.IslandController.RenewAutoDismiss(NotificationDuration.Short);
    }

    /// <summary>
    /// Re-copy confirmation: collapse to the small "• Copied" pill for 2 seconds,
    /// then dismiss. Called by IslandController.ShowCopiedFeedback().
    /// </summary>
    public void ShowCopiedConfirmation()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
        _leaveTimer?.Stop();
        _leaveTimer = null;

        App.IslandController.CancelAutoDismiss();

        _isWidgetExpanded = false;
        VisualStateManager.GoToState(this, "Collapsed", true);

        var (width, height) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(width, height);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = NotificationDuration.Short;
        timer.IsRepeating = false;
        timer.Tick += (s, ev) => App.IslandController.DismissClipboard();
        timer.Start();
    }
}
