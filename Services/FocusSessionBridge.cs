using System;

namespace DynamicIsland.Services;

/// <summary>
/// Static bridge between the Focus Session timer (owned by ExpandedDashboard,
/// whose 1 s DispatcherTimer keeps ticking even while the dashboard is
/// collapsed) and lightweight consumers such as the pill cards that must not
/// depend on the dashboard's code-behind. ExpandedDashboard publishes the
/// current state here on every timer tick and state change; consumers
/// subscribe to <see cref="StateChanged"/>.
/// </summary>
public static class FocusSessionBridge
{
    private static bool _isRunning;
    private static bool _isActive;
    private static double _progressFraction;

    /// <summary>True while the focus/pomodoro session is counting down.</summary>
    public static bool IsRunning => _isRunning;

    /// <summary>
    /// True while a session is in progress — counting down OR paused
    /// mid-session (started but not completed or reset). False before a
    /// session starts, after it completes, and after a reset.
    /// </summary>
    public static bool IsActive => _isActive;

    /// <summary>
    /// Session progress as a 0.0–1.0 fraction of TIME ELAPSED (0 at start,
    /// 1 at completion) — the ring fills clockwise from the top as the
    /// session runs down, mirroring FocusProgressFraction in the dashboard.
    /// </summary>
    public static double ProgressFraction => _progressFraction;

    /// <summary>
    /// Raised whenever the published state changes (session start/pause/
    /// reset/completion, or the per-second progress tick). Raised on the UI
    /// thread.
    /// </summary>
    public static event EventHandler? StateChanged;

    /// <summary>
    /// Publishes the current focus state. Called by ExpandedDashboard from its
    /// 1 s timer and the play/pause/reset/duration handlers. Skips the event
    /// when nothing changed so idle consumers are not woken every second.
    /// </summary>
    public static void Publish(bool isRunning, bool isActive, double progressFraction)
    {
        double fraction = Math.Clamp(progressFraction, 0, 1);
        if (_isRunning == isRunning
            && _isActive == isActive
            && Math.Abs(_progressFraction - fraction) < 0.0001)
            return;

        _isRunning = isRunning;
        _isActive = isActive;
        _progressFraction = fraction;
        StateChanged?.Invoke(null, EventArgs.Empty);
    }
}
