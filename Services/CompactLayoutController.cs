using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Sole authority for compact-pill geometry.
///
/// This is a state machine, not a measurement passthrough. It decides where and
/// how wide Halo Bar SHOULD be and only announces genuinely settled changes.
/// WindowService is a passive renderer that consumes (<see cref="HaloX"/>,
/// <see cref="HaloWidth"/>) and never asks Windows anything itself.
///
/// Pipeline:  Measure → Validate → Clamp → Hysteresis → Debounce → State machine
///            → WidthChanged
///
/// Docking model — the taskbar has two zones:
///   Reserved zone:      Start, Search, Widgets, Copilot — permanent Windows UI.
///   Free zone:          task buttons + empty background, bounded by the tray.
///
/// Halo Bar lives at a fixed home position — the screen's left edge (X=0) —
/// in the free zone left of the reserved cluster (relevant for the centered
/// taskbar on this machine):
///   HaloX          = 0                                       (fixed, never moves)
///   AvailableWidth = AppStripLeft − SafetyMargin  (free space until the app strip begins)
///   HaloWidth      = min(AvailableWidth, Ideal) — no lower floor, so a
///                    crowded taskbar degrades honestly instead of the pill
///                    re-overlapping the tray
///
/// The anchor decision is delegated to an <see cref="IAnchorStrategy"/>; the
/// default is a single <see cref="FixedHomeAnchorStrategy"/>. This controller
/// only measures and publishes — it never computes haloX inline.
///
/// The reserved cluster is measured as the union of the known permanent-UI
/// windows (Start button, Search control, …) so Halo Bar never depends on
/// whether Search is hidden, an icon, or a full box.
///
/// Stability mechanisms:
///  - Resize-observer debounce: a measurement is only trusted once the SAME raw
///    value has persisted for <see cref="SettleDelay"/> (120 ms) without changing.
///    Jitter sequences (340→344→339→341…) are thrown away, exactly like a browser
///    resize observer.
///  - Hysteresis: a confirmed width change is published only when it differs from
///    the last published width by ≥ <see cref="HysteresisDips"/> (20 DIP). A
///    position change (reserved cluster moved) is published when the pill's left
///    edge moves by ≥ 1 DIP, so the pill never drifts over the reserved cluster.
///  - Signal-loss policy: if the taskbar/tasklist is temporarily unavailable, the
///    controller keeps the previous geometry and waits. Frozen geometry is
///    infinitely better than nervous geometry — the pill never hides or resets.
///  - Interaction freeze: while the pointer is over the taskbar (hovering buttons,
///    dragging icons, pinning/unpinning), the current geometry is held. The
///    controller resumes only after the pointer leaves and an idle delay elapses,
///    then it measures once and animates once — so Halo Bar feels calm, not nervous.
///
/// Ownership rules:
///  - Geometry belongs here. Widgets, IslandController, and WindowService never
///    calculate width or offsets.
///  - The controller never hides/recreates the window, never toggles visibility,
///    never switches profiles, and never touches IslandController, widgets, or
///    media state. Its only job is reserved-cluster + tray → (HaloX, HaloWidth).
///
/// Scope note: the pill is docked to the primary monitor's work area, so the
/// primary taskbar (Shell_TrayWnd) is the correct measurement signal. Secondary
/// monitors are not docked and are out of scope.
/// </summary>
public sealed class CompactLayoutController
{
    // ── Design tokens ───────────────────────────────────────────────────────
    public const int CompactMinWidth = 260;
    public const int CompactIdealWidth = 350; // ideal band is 340–360; 350 is the midpoint
    public const int CompactMaxWidth = 420;

    // Gap between the reserved cluster's right edge and the pill's left edge.
    public const int HaloGap = 16;

    // Gap between the pill's right edge and the system tray.
    public const int SafetyMargin = 12;

    // Publish only when |new − lastPublished| >= 20 DIP.
    private const int HysteresisDips = 20;

    // How often the taskbar is re-measured. 150 ms is already below human
    // perception for layout adaptation; polling faster just burns cycles.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    // A measurement must persist unchanged for this long before it is trusted.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(120);

    // After the pointer leaves the taskbar, wait this long before publishing.
    private static readonly TimeSpan PointerIdleDelay = TimeSpan.FromMilliseconds(150);

    // Permanent-UI windows that form the reserved left cluster. Measured as a
    // union so Halo Bar never depends on Search being hidden, an icon, or a box.
    private static readonly string[] ReservedClusterClasses =
    {
        "Start",
        "TrayDummySearchControl",
        "SearchBoxContainer",
        "SearchControlHost",
    };

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly IAnchorStrategy _strategy;

    private DispatcherQueueTimer? _pollTimer;

    // Candidate measurement being confirmed for stability.
    private bool _hasCandidate;
    private double _pendingRaw;
    private double _stableSinceMs;

    // Signal-loss and pointer-freeze bookkeeping.
    private bool _signalLost;
    private bool _hoverActive;
    private double _hoverExitAtMs = double.NegativeInfinity;

    // Last left edge actually published, so position-only changes (reserved
    // cluster growing/shrinking) still get announced without animating width.
    private double _lastPublishedX = double.NaN;

    /// <summary>Right edge of the reserved cluster (Start/Search/…) in DIPs.</summary>
    public double ReservedLeftWidth { get; private set; }

    /// <summary>
    /// Device scale (physical / DIP) used to convert raw window rects. Owned by
    /// WindowService, which derives it from the realized window — the controller
    /// must not query DPI from its own stale handle.
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Right edge of the app-button strip (MSTaskListWClass) in DIPs.</summary>
    public double AppStripRight { get; private set; }

    /// <summary>Free horizontal room between the pill and the tray, in DIPs.</summary>
    public double AvailableWidth { get; private set; }

    /// <summary>Pill left edge X (DIPs, relative to the screen's left edge).</summary>
    public double HaloX { get; private set; }

    /// <summary>Published, settled pill width in DIPs.</summary>
    public double HaloWidth { get; private set; }

    /// <summary>Effective compact width — what WindowService should be at.</summary>
    public double CurrentWidth => HaloWidth;

    /// <summary>Latest computed target after validation/clamp, before confirmation.</summary>
    public double DesiredWidth { get; private set; }

    /// <summary>Last width actually published to WindowService.</summary>
    public double LastStableWidth { get; private set; }

    /// <summary>Width awaiting publication (null when none is pending).</summary>
    public double? PendingWidth { get; private set; }

    /// <summary>Current lifecycle state of the controller.</summary>
    public CompactLayoutState State { get; private set; } = CompactLayoutState.Stable;

    /// <summary>Raised on the UI thread only when a genuinely settled change is published.</summary>
    public event EventHandler<double>? WidthChanged;

    public CompactLayoutController(Window window, IAnchorStrategy? strategy = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        _dispatcherQueue = window.DispatcherQueue;
        _clock.Start();
        _strategy = strategy ?? new FixedHomeAnchorStrategy();
        HaloWidth = CompactIdealWidth;
        LastStableWidth = CompactIdealWidth;
    }

    public void Start()
    {
        _pollTimer?.Stop();
        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = PollInterval;
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
        Logger.Info($"[COMPACT_WIDTH] polling started (min={CompactMinWidth}, ideal={CompactIdealWidth}, max={CompactMaxWidth}, haloGap={HaloGap}, safetyMargin={SafetyMargin}, hysteresis={HysteresisDips}, settle={SettleDelay.TotalMilliseconds}ms, pointerIdle={PointerIdleDelay.TotalMilliseconds}ms).");
        Poll();
    }

    public void Stop()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void Poll()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double raw = MeasureAvailableWidthDips();

        // Signal loss (Explorer restart, auto-hide, transient rebuild): freeze the
        // current geometry and wait. Never guess, never reset, never hide.
        if (raw < 0)
        {
            if (!_signalLost)
            {
                _signalLost = true;
                Logger.Info("[COMPACT_WIDTH] taskbar signal lost — keeping last geometry (frozen).");
            }
            _hasCandidate = false;
            PendingWidth = null;
            State = CompactLayoutState.Waiting;
            return;
        }
        _signalLost = false;

        // Adopt the first measured geometry without announcing it, so the initial
        // window placement is already anchored in the free zone.
        if (double.IsNaN(_lastPublishedX))
            _lastPublishedX = HaloX;

        // Width lives in [0, Ideal]. The ideal cap keeps the pill at its natural
        // size instead of stretching across an empty taskbar. There is no lower
        // floor here: the anchor strategy only returns a width it can back up, and
        // on a crowded taskbar that can legitimately be below CompactMinWidth —
        // re-raising it here would push the pill past the tray again.
        double desired = Math.Min(Math.Max(raw, 0), CompactIdealWidth);
        DesiredWidth = desired;

        // Resize-observer stability: (re)arm the confirmation window whenever the
        // raw value changes. Only a value that holds still is trusted.
        if (!_hasCandidate || Math.Abs(raw - _pendingRaw) >= 1.0)
        {
            _pendingRaw = raw;
            _stableSinceMs = now;
            _hasCandidate = true;
            PendingWidth = desired;
            State = CompactLayoutState.Measuring;
        }

        // Interaction freeze: while the user is over the taskbar, hold the current
        // geometry. Explorer churn under the cursor is never followed.
        bool hover = IsPointerOverTaskbar();
        if (hover)
        {
            if (!_hoverActive)
                Logger.Info("[COMPACT_WIDTH] pointer over taskbar — geometry frozen.");
            _hoverActive = true;
            State = CompactLayoutState.Waiting;
            return;
        }
        if (_hoverActive)
        {
            _hoverActive = false;
            _hoverExitAtMs = now;
            Logger.Info($"[COMPACT_WIDTH] pointer left taskbar — resume in {PointerIdleDelay.TotalMilliseconds:F0}ms.");
            State = CompactLayoutState.Waiting;
            return;
        }
        if (_hoverExitAtMs != double.NegativeInfinity && now - _hoverExitAtMs < PointerIdleDelay.TotalMilliseconds)
        {
            State = CompactLayoutState.Waiting;
            return;
        }
        _hoverExitAtMs = double.NegativeInfinity;

        // Not confirmed stable yet.
        if (now - _stableSinceMs < SettleDelay.TotalMilliseconds)
        {
            State = CompactLayoutState.Measuring;
            return;
        }

        // Confirmed stable. Publish when the width moves past hysteresis OR the
        // pill's left edge moved (reserved cluster changed) — either way the
        // renderer needs to re-anchor.
        bool widthChanged = Math.Abs(desired - LastStableWidth) >= HysteresisDips;
        bool xChanged = Math.Abs(HaloX - _lastPublishedX) >= 1.0;
        if (!widthChanged && !xChanged)
        {
            _hasCandidate = false;
            PendingWidth = null;
            State = CompactLayoutState.Stable;
            return;
        }

        // Publish once — WindowService animates to this geometry.
        double previous = LastStableWidth;
        _hasCandidate = false;
        PendingWidth = null;
        LastStableWidth = desired;
        HaloWidth = desired;
        _lastPublishedX = HaloX;
        State = CompactLayoutState.Animating;
        Logger.Info($"[COMPACT_WIDTH] width {previous:F1} → {desired:F1} (reserved={ReservedLeftWidth:F1}, stripRight={AppStripRight:F1}, haloX={HaloX:F1}, available={raw:F1})");
        WidthChanged?.Invoke(this, desired);
    }

    /// <summary>
    /// Measures the free-zone width the pill may occupy: the horizontal room
    /// between the reserved cluster (Start/Search/…) and the system tray, minus
    /// the pill's margins. This is layout-agnostic — it reads the real window
    /// positions, so it works for left, centered, or right-aligned taskbars
    /// without assuming a specific Windows layout.
    /// Returns -1 when the signal is unavailable.
    /// </summary>
    public double MeasureAvailableWidthDips()
    {
        double scale = Scale;

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return -1;

        // Monitor's right edge: the taskbar strip spans the full primary monitor
        // width, so its right edge is the screen-edge anchor boundary. Sourced
        // the same way TrayLeft is — a raw window rect converted by Scale.
        if (!GetWindowRect(tray, out RECT trayBarRect)) return -1;
        double monitorRight = trayBarRect.Right / scale;

        // Tasklist strip lives at varying depths depending on the Windows
        // version (Win11 24H2 nests it under ReBarWindow32), so search
        // recursively instead of assuming a fixed hierarchy.
        IntPtr taskList = FindChildWindow(tray, "MSTaskListWClass");
        bool foundAppStrip = taskList != IntPtr.Zero;
        if (taskList == IntPtr.Zero)
            taskList = FindChildWindow(tray, "MSTaskSwWClass");
        if (taskList == IntPtr.Zero) return -1;

        if (!GetWindowRect(taskList, out RECT stripRect)) return -1;
        if (stripRect.Left <= 0) return -1;

        // Reserved cluster: union of the known permanent-UI windows, clamped to
        // never extend past the strip. Falls back to the strip's left edge, which
        // is exactly where the free zone begins in the default left-aligned layout.
        int reservedRightPx = MeasureReservedClusterRightPx(tray);
        double reservedRight = reservedRightPx > 0
            ? Math.Min(reservedRightPx, stripRect.Left) / scale
            : stripRect.Left / scale;

        // The pill is anchored at X=0, so it is bounded on the right by where the
        // app-button strip begins (MSTaskSwWClass.Left == ReBarWindow32.Left in
        // the current layout). Detecting Start/Search separately proved flaky
        // (icon mode exposes no HWND), so the strip's left edge is the single,
        // stable boundary consumed by the anchor strategy.
        double appStripLeft = stripRect.Left / scale;

        // System tray's left edge bounds the free zone on the right.
        IntPtr trayNotify = FindChildWindow(tray, "TrayNotifyWnd");
        if (trayNotify == IntPtr.Zero) return -1;
        if (!GetWindowRect(trayNotify, out RECT trayRect)) return -1;
        if (trayRect.Left <= 0) return -1;

        // Package the DIP-converted measurements and let the anchor strategy
        // decide where the pill goes. The strategy is the only place that
        // computes haloX / available.
        double appStripRight = stripRect.Right / scale;
        var snapshot = new TaskbarSnapshot(
            ReservedRight: reservedRight,
            AppStripLeft: appStripLeft,
            AppStripRight: appStripRight,
            TrayLeft: trayRect.Left / scale,
            MonitorRight: monitorRight,
            HasAppStrip: foundAppStrip,
            Scale: scale);

        AnchorResult result = _strategy.Resolve(snapshot);
        double haloX = result.X;
        double available = result.MaxAvailableWidth;

        AppStripRight = appStripRight;
        ReservedLeftWidth = reservedRight;
        HaloX = haloX;
        AvailableWidth = available;

        return available;
    }

    /// <summary>
    /// Rightmost pixel of the reserved left cluster across all known permanent-UI
    /// windows (Start, Search, …), searching the whole taskbar subtree. The union
    /// approach means Halo Bar never cares whether Search is hidden, an icon, or
    /// a full box — whatever occupies the reserved zone is measured as-is.
    /// Returns 0 when none of the known classes are present.
    /// </summary>
    private static int MeasureReservedClusterRightPx(IntPtr tray)
    {
        int maxRight = 0;
        var sb = new StringBuilder(256);
        CollectReservedWindows(tray, ref maxRight, sb);
        return maxRight;
    }

    private static void CollectReservedWindows(IntPtr parent, ref int extent, StringBuilder sb)
    {
        IntPtr child = IntPtr.Zero;
        while ((child = FindWindowEx(parent, child, null, null)) != IntPtr.Zero)
        {
            if (GetClassName(child, sb, sb.Capacity) > 0)
            {
                foreach (string cls in ReservedClusterClasses)
                {
                    if (sb.ToString() == cls && GetWindowRect(child, out RECT r) && r.Right > extent)
                    {
                        extent = r.Right;
                        break;
                    }
                }
            }
            CollectReservedWindows(child, ref extent, sb);
        }
    }

    /// <summary>
    /// True while the pointer is anywhere over the taskbar. Hovering the taskbar
    /// is when Explorer churns the button layout, so the controller freezes the
    /// geometry during that time and only resumes after the pointer leaves.
    /// </summary>
    private bool IsPointerOverTaskbar()
    {
        if (!GetCursorPos(out POINT pt)) return false;

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        if (!GetWindowRect(tray, out RECT r)) return false;
        return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
    }

    private static IntPtr FindChildWindow(IntPtr parent, string className)
    {
        IntPtr child = FindWindowEx(parent, IntPtr.Zero, className, null);
        if (child != IntPtr.Zero) return child;

        IntPtr current = IntPtr.Zero;
        while ((current = FindWindowEx(parent, current, null, null)) != IntPtr.Zero)
        {
            IntPtr found = FindChildWindow(current, className);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}

/// <summary>Lifecycle state of the compact layout controller.</summary>
public enum CompactLayoutState
{
    /// <summary>The current width is published; no pending change.</summary>
    Stable,

    /// <summary>A candidate width is being confirmed for stability (debounce).</summary>
    Measuring,

    /// <summary>Frozen — signal lost, pointer over taskbar, or idle after hover.</summary>
    Waiting,

    /// <summary>A width change was just published; WindowService is animating.</summary>
    Animating,
}
