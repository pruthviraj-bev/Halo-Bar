using System;
using System.Runtime.InteropServices;
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
/// Halo Bar lives at a fixed home position — the screen's left edge (X=0):
///   HaloX          = 0                                       (fixed, never moves)
///   AvailableWidth = AppStripLeft − SafetyMargin − UnmeasuredIconBuffer
///   HaloWidth      = min(AvailableWidth, Ideal) — no lower floor, so a
///                    crowded taskbar degrades honestly instead of the pill
///                    re-overlapping the tray
///
/// The only boundary the controller measures is the app-button strip's left
/// edge (MSTaskSwWClass / ReBarWindow32, equal in the current layout).
/// Start/Search are deliberately not detected — icon mode renders them
/// XAML-only with no HWND — so the strip's left edge is the single, stable
/// measurement signal.
///
/// The anchor decision is delegated to an <see cref="IAnchorStrategy"/>; the
/// default is a single <see cref="FixedHomeAnchorStrategy"/>. This controller
/// only measures and publishes — it never computes haloX inline.
///
/// Stability mechanisms:
///  - Resize-observer debounce: a measurement is only trusted once the SAME raw
///    value has persisted for <see cref="SettleDelay"/> (120 ms) without changing.
///    Jitter sequences (340→344→339→341…) are thrown away, exactly like a browser
///    resize observer.
///  - Hysteresis: a confirmed width change is published only when it differs from
///    the last published width by ≥ <see cref="HysteresisDips"/> (20 DIP).
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
///    media state. Its only job is app-strip boundary → (HaloX, HaloWidth).
///
/// Scope note: the pill is docked to the primary monitor's work area, so the
/// primary taskbar (Shell_TrayWnd) is the correct measurement signal. Secondary
/// monitors are not docked and are out of scope.
/// </summary>
public sealed class CompactLayoutController
{
    // ── Design tokens ───────────────────────────────────────────────────────
    public const int CompactIdealWidth = 350; // ideal band is 340–360; 350 is the midpoint

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

    /// <summary>
    /// Device scale (physical / DIP) used to convert raw window rects. Owned by
    /// WindowService, which derives it from the realized window — the controller
    /// must not query DPI from its own stale handle.
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Pill left edge X (DIPs, relative to the screen's left edge).</summary>
    public double HaloX { get; private set; }

    /// <summary>Published, settled pill width in DIPs.</summary>
    public double HaloWidth { get; private set; }

    /// <summary>Effective compact width — what WindowService should be at.</summary>
    public double CurrentWidth => HaloWidth;

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
        // Pass 15 diagnostic: HALO_P15_NOCOMPACT=1 skips the repeating poll so
        // the idle render stream can be attributed to the compact-width poll vs.
        // other sustainers. One initial measurement still runs so the pill gets
        // correct geometry. Default behavior unchanged.
        if (Helpers.MotionDiagnostics.P15NoCompactPoll)
        {
            Logger.Info("[P15] compact-width polling disabled (HALO_P15_NOCOMPACT=1, diagnostic).");
            Poll();
            return;
        }
        _pollTimer?.Stop();
        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = PollInterval;
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
        Logger.Info($"[COMPACT_WIDTH] polling started (ideal={CompactIdealWidth}, safetyMargin={SafetyMargin}, hysteresis={HysteresisDips}, settle={SettleDelay.TotalMilliseconds}ms, pointerIdle={PointerIdleDelay.TotalMilliseconds}ms).");
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

        // Width lives in [0, Ideal]. The ideal cap keeps the pill at its natural
        // size instead of stretching across an empty taskbar. There is no lower
        // floor here: the anchor strategy only returns a width it can back up, and
        // on a crowded taskbar that can legitimately be below the ideal width —
        // re-raising it here would push the pill past the tray again.
        double desired = Math.Min(Math.Max(raw, 0), CompactIdealWidth);

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

        // Confirmed stable. Publish when the width moves past hysteresis.
        bool widthChanged = Math.Abs(desired - LastStableWidth) >= HysteresisDips;
        if (!widthChanged)
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
        State = CompactLayoutState.Animating;
        Logger.Info($"[COMPACT_WIDTH] width {previous:F1} → {desired:F1} (haloX={HaloX:F1}, available={raw:F1})");
        WidthChanged?.Invoke(this, desired);
    }

    /// <summary>
    /// Measures the free-zone width the pill may occupy: the horizontal room
    /// between the screen's left edge and the app-button strip's left edge, minus
    /// the pill's margins. The strip's left edge is the only boundary the
    /// controller needs — the pill is anchored at X=0 and never extends into the
    /// app buttons. Returns -1 when the signal is unavailable.
    /// </summary>
    public double MeasureAvailableWidthDips()
    {
        double scale = Scale;

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return -1;

        // Tasklist strip lives at varying depths depending on the Windows
        // version (Win11 24H2 nests it under ReBarWindow32), so search
        // recursively instead of assuming a fixed hierarchy.
        IntPtr taskList = FindChildWindow(tray, "MSTaskListWClass");
        if (taskList == IntPtr.Zero)
            taskList = FindChildWindow(tray, "MSTaskSwWClass");
        if (taskList == IntPtr.Zero) return -1;

        if (!GetWindowRect(taskList, out RECT stripRect)) return -1;
        if (stripRect.Left <= 0) return -1;

        // The pill is anchored at X=0, so it is bounded on the right by where the
        // app-button strip begins (MSTaskSwWClass.Left == ReBarWindow32.Left in
        // the current layout). Detecting Start/Search separately proved flaky
        // (icon mode exposes no HWND), so the strip's left edge is the single,
        // stable boundary consumed by the anchor strategy.
        double appStripLeft = stripRect.Left / scale;

        // Package the DIP-converted measurement and let the anchor strategy
        // decide where the pill goes. The strategy is the only place that
        // computes haloX / available.
        var snapshot = new TaskbarSnapshot(appStripLeft);

        AnchorResult result = _strategy.Resolve(snapshot);
        HaloX = result.X;

        return result.MaxAvailableWidth;
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
