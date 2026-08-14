using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
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

    // Whether the user has toggled manual expansion via click.
    private bool _clickExpanded;

    // Auto-collapse: collapses the expanded panel after a period of inactivity.
    private DispatcherQueueTimer? _autoCollapseTimer;

    // True while the mouse cursor is inside the expanded panel.
    private bool _mouseIsOver;

    // Number of open interaction surfaces (gear flyout, Focus settings)
    // that suppress all auto-collapse until released.
    private int _awakeHoldCount;

    // Persistent PillDashboard instance (ambient pill host for weather + music cards).
    private PillDashboard? _pillDashboard;

    // Persistent ExpandedDashboard instance (shown when user clicks/expands).
    private UserControl? _dashboardWidget;

    // Pass 17: true once the dashboard's first real measure/arrange (its
    // explicit 780×640) has completed OFF the click path — either via the
    // invisible preload warm-up or via an earlier real expansion. When true,
    // the first user expand re-lays-out the already-warm tree (Pass 16 warm
    // mode: ~15–18 samples, max 31–32 ms, h33=0) instead of paying the
    // 94–125 ms cold first-layout stall.
    private bool _dashboardLayoutWarmed;

    // ── Public surface ─────────────────────────────────────────────────────

    /// <summary>
    /// Fires on the UI thread whenever the active UserControl changes.
    /// MainWindowViewModel is the only expected subscriber.
    /// </summary>
    public event EventHandler<UserControl?> ActiveControlChanged = delegate { };

    /// <summary>
    /// The currently active compact widget control (stack[0]), or null when the stack is empty.
    /// Needed because the initial selection is published in the IslandController constructor,
    /// before MainWindowViewModel subscribes to <see cref="ActiveControlChanged"/> — so late
    /// subscribers must read this to pick up the default widget instead of being left empty.
    /// </summary>
    public UserControl? CurrentControl => _stack.Count > 0 ? _stack[0].Control : null;

    /// <summary>
    /// Fires whenever the manual click-expansion state changes.
    /// </summary>
    public event EventHandler<bool> IsExpandedChanged = delegate { };

    /// <summary>
    /// True while the manual click-expansion (dashboard) is showing.
    /// </summary>
    public bool IsExpanded => _clickExpanded;

    public IslandController(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        App.ClipboardService.ClipboardChanged += OnClipboardChanged;
        App.BatteryService.NotificationRequired += OnBatteryNotification;
        App.VolumeService.NotificationRequired += OnVolumeNotification;

        // Push default pill dashboard (weather card always, music card when a track
        // is playing) to serve as ambient taskbar display.
        _pillDashboard = new PillDashboard();
        _pillDashboard.TotalWidthChanged += OnPillTotalWidthChanged;
        Push(_pillDashboard, _pillDashboard);
    }

    // ── Interaction signals (from MainWindow) ─────────────────────────────

    /// <summary>
    /// Called on left-click. Toggles expanded/collapsed.
    /// When expanding, arms the 6-second idle auto-collapse timer.
    /// </summary>
    public void NotifyIslandClick()
    {
        Logger.Info($"[PROFILE] NotifyIslandClick start ms={Environment.TickCount64}");

        if (_clickExpanded) return;

        // Don't expand the dashboard if the file shelf is open —
        // the click belongs to the shelf panel.
        if (_pillDashboard?.IsShelfExpanded == true) return;

        Logger.Info("[MOTION-P8] ExpandRequested");
        MotionDiagnostics.P16BeginSegment("expand");

        _clickExpanded = true;
        IsExpandedChanged.Invoke(this, true);
        Commit();

        Logger.Info($"[PROFILE] NotifyIslandClick after Commit ms={Environment.TickCount64}");

        ArmAutoCollapse(TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// Called by MainWindow.PointerEntered.
    /// Cancels any pending auto-collapse while the user is hovering.
    /// </summary>
    public void NotifyMouseEnter()
    {
        if (Helpers.MotionDiagnostics.EnableP13)
            Logger.Info($"[P13DBG] NotifyMouseEnter (was {_mouseIsOver})");
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
        if (Helpers.MotionDiagnostics.EnableP13)
            Logger.Info($"[P13DBG] NotifyMouseLeave (was {_mouseIsOver})");
        _mouseIsOver = false;
        if (_awakeHoldCount > 0) return; // Don't arm while an interaction surface is open.
        if (_clickExpanded)
            ArmAutoCollapse(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Forwards the region monitor's hover state to the active widget
    /// (PASS 35). The stable-window per-frame SetWindowRgn can drop XAML
    /// pointer events, so widget-level hover behaviors (the Clipboard
    /// hover-expand preview) are driven by MainWindow's region-accurate hover
    /// monitor, not by PointerEntered/Exited delivery. Gated to compact mode:
    /// while the dashboard is expanded the hover belongs to the dashboard, and
    /// a widget popup must not grow over it.
    /// </summary>
    public void NotifyWidgetHover(bool inside)
    {
        var top = _stack.Count > 0 ? _stack[0] : null;
        if (top?.Control is not ClipboardWidget cw) return;
        if (inside && !_clickExpanded)
            cw.NotifyHoverEnter();
        else
            cw.NotifyHoverExit();
    }

    /// <summary>
    /// Called when the application window loses activation (user clicked elsewhere).
    /// Immediately collapses the expanded panel.
    /// </summary>
    public void NotifyFocusLost()
    {
        // Pass 13 diagnostics (env-gated, HALO_P13_YOFFSET present): pin down the
        // synthetic-input collapse path.
        if (Helpers.MotionDiagnostics.EnableP13)
            Logger.Info($"[P13DBG] NotifyFocusLost mouseIsOver={_mouseIsOver} awakeHold={_awakeHoldCount} clickExpanded={_clickExpanded}");

        // A press while the pointer is over the dock (e.g. clicking the gear to open a
        // surface, possibly mid-expand-animation while the rect is still small) is never
        // a click "outside" — don't collapse it.
        if (_mouseIsOver) return;
        if (_awakeHoldCount > 0) return; // Surface open — don't collapse.
        if (_clickExpanded)
        {
            Logger.Info("[MOTION-P8] CollapseRequested reason=focusLost");
            MotionDiagnostics.P16BeginSegment("collapse");
            _clickExpanded = false;
            IsExpandedChanged.Invoke(this, false);
            DisarmAutoCollapse();
            _awakeHoldCount = 0;
            Commit();
        }
    }

    /// <summary>Marks an interaction surface as open; suppresses all auto-collapse until released.</summary>
    public void BeginAwake()
    {
        _awakeHoldCount++;
        DisarmAutoCollapse();
    }

    public void EndAwake()
    {
        _awakeHoldCount = Math.Max(0, _awakeHoldCount - 1);
        // Restore normal behavior when the last surface closes and the pointer has left.
        if (_awakeHoldCount == 0 && _clickExpanded && !_mouseIsOver)
            ArmAutoCollapse(TimeSpan.FromSeconds(2));
    }

    // ── PASS 47 (GOAL 2) — native OLE drag signals ─────────────────────────
    // Explorer file drags do not surface as XAML DragEnter on this window, so
    // WindowService's native OLE drop target routes them here. Enter/Leave are
    // SIGNAL-only (no payload resolution); Drop carries the resolved paths.
    // All run on the UI thread (the drop target marshals via the dispatcher).

    /// <summary>Opens the File Shelf when a shell-file drag enters the Halo drop target.</summary>
    public void NotifyFileDragEnter() => _pillDashboard?.NotifyExternalDragEnter();

    /// <summary>Closes a drag-opened shelf when the shell-file drag leaves the Halo drop target.</summary>
    public void NotifyFileDragLeave() => _pillDashboard?.NotifyExternalDragLeave();

    /// <summary>Adds the dropped file/folder paths to the File Shelf and collapses it.</summary>
    public void NotifyFilesDropped(string[] paths) => _pillDashboard?.AcceptExternalDrop(paths);

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
            // Only collapse if nothing holds the island awake and the user isn't hovering.
            if (_awakeHoldCount == 0 && !_mouseIsOver && _clickExpanded)
            {
                Logger.Info("[MOTION-P8] CollapseRequested reason=autoCollapse");
                MotionDiagnostics.P16BeginSegment("collapse");
                _clickExpanded = false;
                IsExpandedChanged.Invoke(this, false);
                _awakeHoldCount = 0;
                Commit();
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
        Helpers.Logger.Info($"IslandController.Push: {control.GetType().Name} (priority={widget.Priority}, stackBefore={_stack.Count})");

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
        int idx = _stack.FindIndex(e => e.Widget == widget);
        if (idx < 0) return; // Already removed — safe to ignore.

        Helpers.Logger.Info($"IslandController.Pop: {control.GetType().Name} (wasTop={idx == 0}, stackBefore={_stack.Count})");

        bool wasTop = (idx == 0);
        _stack.RemoveAt(idx);
        widget.OnDeactivated();

        // Resume the new top widget if the removed one was active.
        if (wasTop && _stack.Count > 0)
            _stack[0].Widget.OnResumed();

        Commit();
    }

    // ── Persistent dashboard instance ───────────────────────────────────────

    /// <summary>
    /// Returns the persistent dashboard instance, constructing it on first use.
    /// This controller is the single owner: MainWindowViewModel consumes this
    /// exact instance for DashboardHost, so the dashboard is never constructed
    /// twice (previously one instance was built for DashboardHost and another
    /// for the active-widget slot on first expand).
    /// </summary>
    public UserControl EnsureDashboard()
    {
        if (_dashboardWidget == null)
        {
            Logger.Info($"[PROFILE] EnsureDashboard constructing dashboard ms={Environment.TickCount64}");
            MotionDiagnostics.P16Mark("DashboardConstructionStarted", "preload");
            long startAlloc = GC.GetAllocatedBytesForCurrentThread();
            long c0 = Environment.TickCount64;
            // Pass 16 Mode B (HALO_P16_EMPTY=1): swap in a trivial Grid+Rectangle
            // so the first-expand cost can be attributed to the dashboard's real
            // visual tree vs. the window/motion system. Production unchanged.
            _dashboardWidget = MotionDiagnostics.P16EmptyDashboard
                ? new EmptyDashboard()
                : new ExpandedDashboard();
            long c1 = Environment.TickCount64;
            MotionDiagnostics.P16Mark("DashboardConstructionCompleted", "preload", $"durMs={c1 - c0}");
            Logger.Info($"[MEM] dashboard construction allocated: {(GC.GetAllocatedBytesForCurrentThread() - startAlloc) / 1024.0:F0} KB (UI-thread, incl. 470-item clipboard population)");
            Logger.Info($"[PROFILE] EnsureDashboard constructed dashboard ms={Environment.TickCount64}");
        }
        return _dashboardWidget;
    }

    /// <summary>
    /// Pre-constructs the dashboard off the click path (called shortly after
    /// startup) so the first click-to-expand is not delayed by full dashboard
    /// construction. Idempotent — the click path still creates it lazily if the
    /// user expands before this runs.
    /// </summary>
    public void PreloadDashboard()
    {
        _dispatcherQueue.TryEnqueue(() => EnsureDashboard());
    }

    /// <summary>Pass 17: true once the dashboard's first full-size layout is done.</summary>
    public bool DashboardLayoutWarmed => _dashboardLayoutWarmed;

    /// <summary>
    /// Pass 17: warms the dashboard's first layout off the click path. Called
    /// from the preload tick right after <see cref="PreloadDashboard"/>; this
    /// method re-enqueues on the UI thread, so it runs AFTER the construction
    /// item (same dispatcher queue, FIFO order). HALO_P17_DISABLE=1 turns it
    /// off for A/B measurement of the cold path.
    /// </summary>
    public void WarmupDashboardLayout()
    {
        if (_dashboardLayoutWarmed || MotionDiagnostics.P17DisableWarmup) return;
        _dispatcherQueue.TryEnqueue(WarmupDashboardLayoutCore);
    }

    /// <summary>
    /// Pass 17 core (UI thread). The dashboard XAML root declares Width=780 /
    /// Height=640, so a temporary window sized to the production expanded
    /// profile (800×664 DIP via WindowService.ResolveProfileSize) arranges it
    /// at exactly its production size. The temp window is positioned far
    /// outside every monitor, styled tool-window + no-activate (no taskbar
    /// button, no focus steal) BEFORE first show, and closed immediately after
    /// the synchronous layout. No production motion runs, no window geometry
    /// changes, no pill state changes — the warm-up is never visible. Any
    /// failure falls back to the existing cold expand path unchanged.
    /// </summary>
    private async void WarmupDashboardLayoutCore()
    {
        if (_dashboardLayoutWarmed || MotionDiagnostics.P17DisableWarmup) return;

        // The user already expanded — the real first layout ran on the click
        // path, so the tree is already warm.
        if (_clickExpanded)
        {
            _dashboardLayoutWarmed = true;
            return;
        }

        var dashboard = EnsureDashboard(); // constructs if the preload hasn't run yet
        if (dashboard == null)
        {
            _dashboardLayoutWarmed = true; // nothing to warm
            return;
        }

        long t0 = Environment.TickCount64;
        MotionDiagnostics.P17Log("DashboardWarmupStarted");
        try
        {
            var warm = new Window { Content = dashboard };

            // No taskbar button + no focus steal, applied while still hidden.
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(warm);
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            var appWindow = warm.AppWindow;
            appWindow.IsShownInSwitchers = false;
            // Far outside every monitor — the window is presented there, so the
            // warm-up can never be seen.
            appWindow.Move(new PointInt32(-20000, -20000));

            // Same content area the production window gives the dashboard
            // (DIPs → physical at this window's scale; primary-monitor scale,
            // matching the docked window).
            var (dipW, dipH) = App.WindowService?.ResolveProfileSize(WindowProfile.Expanded) ?? (800, 664);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            appWindow.Resize(new SizeInt32(
                (int)Math.Round(dipW * scale),
                (int)Math.Round(dipH * scale)));

            warm.Activate();

            // Force the first measure/arrange synchronously; if the first
            // present is still pending, yield a few dispatcher turns and
            // re-check against the dashboard's explicit 780×640.
            for (int i = 0; i < 6; i++)
            {
                dashboard.UpdateLayout();
                if (dashboard.ActualWidth >= 760.0 && dashboard.ActualHeight >= 620.0) break;
                await Task.Yield();
            }

            bool ok = dashboard.ActualWidth >= 760.0 && dashboard.ActualHeight >= 620.0;

            // Window teardown is best-effort: even if Close() throws, the layout
            // succeeded, so the warm state must be recorded regardless.
            try { warm.Close(); }
            catch (Exception closeEx) { Logger.Error($"[MOTION-P17] warm window close failed: {closeEx.Message}", closeEx); }

            _dashboardLayoutWarmed = ok;
            MotionDiagnostics.P17Log(
                $"DashboardWarmupCompleted elapsedMs={Environment.TickCount64 - t0} " +
                $"size={(int)dashboard.ActualWidth}x{(int)dashboard.ActualHeight} ok={ok}");
            MotionDiagnostics.P17Log($"DashboardWarmState={_dashboardLayoutWarmed}");
        }
        catch (Exception ex)
        {
            // Fail safe: leave the dashboard unwarmed — the existing cold path
            // still expands correctly, just with the pre-Pass-17 first layout.
            Logger.Error($"[MOTION-P17] DashboardWarmupFailed: {ex.Message}", ex);
            _dashboardLayoutWarmed = false;
        }
    }

    /// <summary>
    /// Publishes the current active control and recalculates the window profile.
    /// Owns the auto-dismiss timer — starts/restarts it for the new top widget if it has AutoDismiss.
    /// Called after every stack mutation.
    /// </summary>
    private void Commit()
    {
        Logger.Info($"[PROFILE] Commit start ms={Environment.TickCount64} clickExpanded={_clickExpanded}");

        if (_clickExpanded)
        {
            // The dashboard is hosted solely by MainWindowViewModel.Dashboard
            // (DashboardHost). The active-widget slot keeps showing the pill —
            // its row is collapsed while expanded, so nothing visible changes,
            // and first expand never constructs the dashboard twice.
            var top = _stack.Count > 0 ? _stack[0] : null;
            ActiveControlChanged.Invoke(this, top?.Control);
            _autoDismissTimer?.Stop();
            _autoDismissTimer = null;
        }
        else
        {
            var top = _stack.Count > 0 ? _stack[0] : null;
            var published = top?.Control;
            ActiveControlChanged.Invoke(this, published);

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

        Logger.Info($"[PROFILE] ApplyWindowProfile: desired={desired} ms={Environment.TickCount64}");

        // Deduplication happens inside WindowService.SetProfile, which compares the
        // resolved target size. Compact is stateless (one fixed width), so a change
        // between two compact widgets resolves to the same target and is a no-op.
        App.WindowService.SetProfile(desired);
    }

    private void OnPillTotalWidthChanged(object? sender, double newWidth)
    {
        App.WindowService?.SetOverrideCollapsedWidth(newWidth);
        ApplyWindowProfile();
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

            // Create a fresh widget for this copy event and push with a 2s transient.
            var clipWidget = new ClipboardWidget(item);
            Push(clipWidget, clipWidget, NotificationDuration.Short);
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

    /// <summary>
    /// Tells the active clipboard widget to show its "Copied" confirmation for 2s
    /// (then it dismisses itself). Called by the Re-copy command.
    /// </summary>
    public void ShowCopiedFeedback()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var entry = _stack.FirstOrDefault(e => e.Control is ClipboardWidget);
            if (entry?.Control is ClipboardWidget widget)
                widget.ShowCopiedConfirmation();
        });
    }

    /// <summary>
    /// Restarts the transient auto-dismiss timer without repushing the widget —
    /// used to re-arm dismissal once the user stops hovering a transient pill.
    /// </summary>
    public void RenewAutoDismiss(TimeSpan duration)
    {
        if (_stack.Count == 0) return;

        var top = _stack[0];
        if (top.AutoDismiss is null) return;

        _autoDismissTimer?.Stop();
        _autoDismissTimer = _dispatcherQueue.CreateTimer();
        _autoDismissTimer.Interval = duration;
        _autoDismissTimer.IsRepeating = false;
        _autoDismissTimer.Tick += (_, _) => Pop(top.Widget, top.Control);
        _autoDismissTimer.Start();
    }

    /// <summary>
    /// Stops the transient auto-dismiss timer entirely — used while the cursor
    /// is present over a transient pill so it never times out mid-interaction.
    /// </summary>
    public void CancelAutoDismiss()
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;
    }

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

    // ── Pass 17 warm-up window P/Invoke (same style pattern ControlWindow uses) ──

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080; // Hides from Alt+Tab & taskbar button
    private const int WS_EX_NOACTIVATE = 0x08000000; // Prevents focus steal

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
