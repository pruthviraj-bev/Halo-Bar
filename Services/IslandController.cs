using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.Widgets;

namespace DynamicIsland.Services;

/// <summary>
/// Central coordinator for all widget transitions and window animations.
///
/// Design rules enforced here:
///  - WindowService is called only from this class (via SetProfile).
///  - Widgets are created only from this class.
///  - The priority stack ensures lower-priority widgets are automatically
///    suspended/resumed when higher-priority widgets arrive or leave.
///  - Animation conflicts are prevented by deduplicating consecutive
///    SetProfile calls with the same target profile.
/// </summary>
public class IslandController
{
    // ── Internal stack entry ───────────────────────────────────────────────

    private sealed record WidgetEntry(IIslandWidget Widget, UserControl Control, TimeSpan? AutoDismiss);

    // ── State ──────────────────────────────────────────────────────────────

    private readonly DispatcherQueue _dispatcherQueue;

    // Index 0 = highest priority = currently active widget.
    private readonly List<WidgetEntry> _stack = new();

    // Auto-dismiss timer for transient widgets (currently: Clipboard).
    private DispatcherQueueTimer? _autoDismissTimer;

    // Tracks the last profile sent to WindowService to avoid redundant animations.
    private WindowProfile? _currentProfile;

    // Whether the user has toggled manual expansion via click.
    private bool _clickExpanded;

    // Auto-collapse: collapses the expanded panel after a period of inactivity.
    private DispatcherQueueTimer? _autoCollapseTimer;

    // True while the mouse cursor is inside the expanded panel.
    private bool _mouseIsOver;

    // Persistent MediaWidget instance (reused across track changes).
    private MediaWidget? _mediaWidget;

    // Persistent ExpandedDashboard instance (shown when user clicks/expands).
    private UserControl? _dashboardWidget;

    // ── Public surface ─────────────────────────────────────────────────────

    /// <summary>
    /// Fires on the UI thread whenever the active UserControl changes.
    /// MainWindowViewModel is the only expected subscriber.
    /// </summary>
    public event EventHandler<UserControl?> ActiveControlChanged = delegate { };

    /// <summary>
    /// Fires whenever the manual click-expansion state changes.
    /// </summary>
    public event EventHandler<bool> IsExpandedChanged = delegate { };

    public IslandController(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        App.MediaService.MediaStateChanged += OnMediaStateChanged;
        App.ClipboardService.ClipboardChanged += OnClipboardChanged;
        App.BatteryService.NotificationRequired += OnBatteryNotification;
        App.VolumeService.NotificationRequired += OnVolumeNotification;

        // Push default weather collapsed widget to serve as ambient taskbar display
        var defaultWidget = new WeatherCollapsedWidget();
        Push(defaultWidget, defaultWidget);
    }

    /// <summary>
    /// Called by WindowService (150ms timer thread) when the width tier changes.
    /// Routes the update to the MediaWidget on the UI thread.
    /// </summary>
    public void SetMediaWidgetTier(int tier)
    {
        _dispatcherQueue.TryEnqueue(() => _mediaWidget?.SetTier(tier));
    }

    // ── Interaction signals (from MainWindow) ─────────────────────────────

    /// <summary>
    /// Called on left-click. Toggles expanded/collapsed.
    /// When expanding, arms the 6-second idle auto-collapse timer.
    /// </summary>
    public void NotifyIslandClick()
    {
        _clickExpanded = !_clickExpanded;
        IsExpandedChanged.Invoke(this, _clickExpanded);
        ApplyWindowProfile();

        if (_clickExpanded)
            ArmAutoCollapse(TimeSpan.FromSeconds(6));
        else
            DisarmAutoCollapse();
    }

    /// <summary>
    /// Called by MainWindow.PointerEntered.
    /// Cancels any pending auto-collapse while the user is hovering.
    /// </summary>
    public void NotifyMouseEnter()
    {
        _mouseIsOver = true;
        DisarmAutoCollapse();
    }

    /// <summary>
    /// Called by MainWindow.PointerExited.
    /// Re-arms a shorter (2 s) auto-collapse so the panel closes soon after
    /// the user moves away.
    /// </summary>
    public void NotifyMouseLeave()
    {
        _mouseIsOver = false;
        if (_clickExpanded)
            ArmAutoCollapse(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Called when the application window loses activation (user clicked elsewhere).
    /// Immediately collapses the expanded panel.
    /// </summary>
    public void NotifyFocusLost()
    {
        Helpers.Logger.Info($"[DEBUG_EVENT] NotifyFocusLost: _clickExpanded={_clickExpanded} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        if (_clickExpanded)
        {
            _clickExpanded = false;
            IsExpandedChanged.Invoke(this, false);
            DisarmAutoCollapse();
            ApplyWindowProfile();
        }
    }

    // ── Auto-collapse timer ────────────────────────────────────────────────

    /// <summary>Starts (or restarts) the auto-collapse countdown.</summary>
    private void ArmAutoCollapse(TimeSpan delay)
    {
        DisarmAutoCollapse();
        _autoCollapseTimer = _dispatcherQueue.CreateTimer();
        _autoCollapseTimer.Interval = delay;
        _autoCollapseTimer.IsRepeating = false;
        _autoCollapseTimer.Tick += (_, _) =>
        {
            // Only collapse if the user isn't hovering.
            if (!_mouseIsOver && _clickExpanded)
            {
                _clickExpanded = false;
                IsExpandedChanged.Invoke(this, false);
                ApplyWindowProfile();
            }
        };
        _autoCollapseTimer.Start();
    }

    private void DisarmAutoCollapse()
    {
        _autoCollapseTimer?.Stop();
        _autoCollapseTimer = null;
    }

    // ── Stack management ───────────────────────────────────────────────────

    private void Push(IIslandWidget widget, UserControl control, TimeSpan? autoDismiss = null)
    {
        // Prevent duplicate stack entries for the same widget instance.
        if (_stack.Any(e => e.Widget == widget)) return;

        // Suspend the current top widget before displacing it.
        if (_stack.Count > 0)
            _stack[0].Widget.OnSuspended();

        _stack.Add(new WidgetEntry(widget, control, autoDismiss));

        // Keep the list sorted so index 0 is always the highest-priority widget.
        _stack.Sort((a, b) => b.Widget.Priority.CompareTo(a.Widget.Priority));

        widget.OnActivated();
        Commit();
    }

    private void Pop(IIslandWidget widget, UserControl control)
    {
        Helpers.Logger.Info($"[DEBUG_EVENT] IslandController.Pop: Widget={widget.GetType().Name} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        int idx = _stack.FindIndex(e => e.Widget == widget);
        if (idx < 0) return; // Already removed — safe to ignore.

        bool wasTop = (idx == 0);
        _stack.RemoveAt(idx);
        widget.OnDeactivated();

        // Resume the new top widget if the removed one was active.
        if (wasTop && _stack.Count > 0)
            _stack[0].Widget.OnResumed();

        Commit();
    }

    /// <summary>
    /// Publishes the current active control and recalculates the window profile.
    /// Owns the auto-dismiss timer — starts/restarts it for the new top widget if it has AutoDismiss.
    /// Called after every stack mutation.
    /// </summary>
    private void Commit()
    {
        if (_clickExpanded)
        {
            if (_dashboardWidget == null)
                _dashboardWidget = new ExpandedDashboard();
            ActiveControlChanged.Invoke(this, _dashboardWidget);
            _autoDismissTimer?.Stop();
            _autoDismissTimer = null;
        }
        else
        {
            var top = _stack.Count > 0 ? _stack[0] : null;
            ActiveControlChanged.Invoke(this, top?.Control);

            // Manage auto-dismiss timer: stop any running timer, then start a new one if needed.
            _autoDismissTimer?.Stop();
            _autoDismissTimer = null;
            if (top?.AutoDismiss is { } duration)
            {
                _autoDismissTimer = _dispatcherQueue.CreateTimer();
                _autoDismissTimer.Interval = duration;
                _autoDismissTimer.IsRepeating = false;
                _autoDismissTimer.Tick += (_, _) => Pop(top.Widget, top.Control);
                _autoDismissTimer.Start();
            }
        }

        ApplyWindowProfile();
    }

    // ── Window profile decisions ───────────────────────────────────────────

    /// <summary>
    /// Determines the correct WindowProfile given the current stack and click-toggle state,
    /// then applies it — but only if it differs from the last applied profile.
    /// This prevents duplicate animation calls from rapid state changes.
    /// </summary>
    public void ApplyWindowProfile()
    {
        if (App.WindowService == null) return;

        WindowProfile desired;

        if (_clickExpanded)
        {
            desired = WindowProfile.Expanded;
        }
        else if (_stack.Count == 0)
        {
            desired = WindowProfile.Collapsed;
        }
        else
        {
            var top = _stack[0].Widget;
            bool expand = top.AutoExpand;
            desired = expand ? top.PreferredProfile : WindowProfile.Collapsed;
        }

        // Deduplicate: skip if the window is already animating toward this profile.
        if (desired == _currentProfile) return;
        _currentProfile = desired;

        App.WindowService.SetProfile(desired);
    }

    // ── Media events ───────────────────────────────────────────────────────

    private void OnMediaStateChanged(object? sender, MediaState state)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrEmpty(state.Title))
            {
                // Lazily create the MediaWidget and push it once.
                if (_mediaWidget == null)
                    _mediaWidget = new MediaWidget();

                Push(_mediaWidget, _mediaWidget);
            }
            else
            {
                // Track ended or player closed — remove media from the stack.
                if (_mediaWidget != null)
                {
                    Pop(_mediaWidget, _mediaWidget);
                    _mediaWidget = null;
                }
            }
        });
    }

    // ── Clipboard events ───────────────────────────────────────────────────

    private void OnClipboardChanged(object? sender, ClipboardItem? item)
    {
        if (item == null) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            // Remove any existing clipboard widget.
            var existing = _stack.FirstOrDefault(e => e.Control is ClipboardWidget);
            if (existing != null)
                Pop(existing.Widget, existing.Control);

            // Create a fresh widget for this copy event and push with Brief auto-dismiss.
            var clipWidget = new ClipboardWidget(item);
            Push(clipWidget, clipWidget, NotificationDuration.Brief);
        });
    }

    /// <summary>
    /// Immediately dismisses widgets of a specific type.
    /// Called by ViewModel action commands.
    /// </summary>
    public void Dismiss<T>() where T : UserControl
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var entry = _stack.FirstOrDefault(e => e.Control is T);
            if (entry != null)
                Pop(entry.Widget, entry.Control);
        });
    }

    /// <summary>
    /// Immediately dismisses the active Clipboard widget.
    /// Called by ClipboardWidgetViewModel action commands (Dismiss, Clear).
    /// </summary>
    public void DismissClipboard() => Dismiss<ClipboardWidget>();

    // ── Battery events ─────────────────────────────────────────────────────

    private void OnBatteryNotification(object? sender, (BatteryState State, TimeSpan Duration) args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Remove any existing battery widget.
            var existing = _stack.FirstOrDefault(e => e.Control is BatteryWidget);
            if (existing != null)
                Pop(existing.Widget, existing.Control);

            // Create a fresh widget for this battery event and push with the provided duration.
            var widget = new BatteryWidget(args.State);
            Push(widget, widget, args.Duration);
        });
    }

    // ── Volume events ──────────────────────────────────────────────────────

    private void OnVolumeNotification(object? sender, (VolumeState State, TimeSpan Duration) args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Remove any existing volume widget.
            var existing = _stack.FirstOrDefault(e => e.Control is VolumeWidget);
            if (existing != null)
                Pop(existing.Widget, existing.Control);

            // Create a fresh widget for this volume event and push with provided duration.
            var widget = new VolumeWidget(args.State);
            Push(widget, widget, args.Duration);
        });
    }
}
