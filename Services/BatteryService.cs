using System;
using Microsoft.UI.Dispatching;
using Windows.Devices.Power;
using Windows.System.Power;
using DynamicIsland.Helpers;
using WinRTBatteryReport = Windows.Devices.Power.BatteryReport;

namespace DynamicIsland.Services;

/// <summary>
/// Monitors the system aggregate battery and fires NotificationRequired only on
/// meaningful category transitions, with 800 ms debounce for charger events.
///
/// UX suppression rules:
///  1. No notification fired on app launch (initial steady-state is silent).
///  2. Only fires when the notification category changes.
///  3. Charging/Discharging events are debounced to suppress rapid plug/unplug.
///  4. Low (≤20%) and Critical (≤10%) are each fired once per downward transition.
///  5. Recovering from Low/Critical does not fire a notification.
/// </summary>
public class BatteryService
{
    // ── Internal notification categories ──────────────────────────────────

    private enum NotificationCategory
    {
        None,
        Charging,
        Discharging,
        Low,
        Critical,
    }

    // ── State ──────────────────────────────────────────────────────────────

    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherQueueTimer? _debounceTimer;
    private Battery? _aggregateBattery;

    // Tracks which category was last notified to suppress redundant events.
    private NotificationCategory _lastNotified = NotificationCategory.None;

    // ── Public surface ─────────────────────────────────────────────────────

    /// <summary>Always reflects the latest polled battery reading.</summary>
    public BatteryState CurrentState { get; private set; } = new(0, false, false, false);

    /// <summary>
    /// Fires on the UI thread when a user-visible notification is warranted.
    /// Carries the state snapshot and the recommended auto-dismiss duration.
    /// IslandController is the only expected subscriber.
    /// </summary>
    public event EventHandler<(BatteryState State, TimeSpan Duration)>? NotificationRequired;

    public BatteryService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    // ── Initialization ─────────────────────────────────────────────────────

    public void Initialize()
    {
        try
        {
            _aggregateBattery = Battery.AggregateBattery;
            _aggregateBattery.ReportUpdated += OnReportUpdated;

            // Read initial state silently — set _lastNotified so we do NOT fire
            // a notification just because the app launched while at Low battery.
            var report = _aggregateBattery.GetReport();
            CurrentState = BuildState(report);
            _lastNotified = GetCategory(CurrentState);

            Logger.Info($"BatteryService: initialized at {CurrentState.ChargePercent}% " +
                        $"(charging={CurrentState.IsCharging})");
        }
        catch (Exception ex)
        {
            Logger.Error("BatteryService: failed to initialize", ex);
        }
    }

    // ── Report handler (fires on a background thread) ──────────────────────

    private void OnReportUpdated(Battery sender, object args)
    {
        // Marshal immediately to the UI thread so all timer and state logic
        // runs on a single thread without locks.
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var report = sender.GetReport();
                var newState = BuildState(report);
                CurrentState = newState;

                var category = GetCategory(newState);
                Logger.Info($"BatteryService: report update {newState.ChargePercent}% (charging={newState.IsCharging}, supply={PowerManager.PowerSupplyStatus})");

                if (category is NotificationCategory.Charging or NotificationCategory.Discharging)
                {
                    // Debounce: rapid plug/unplug within 800 ms produces no notification.
                    _debounceTimer?.Stop();
                    _debounceTimer = _dispatcherQueue.CreateTimer();
                    _debounceTimer.Interval = TimeSpan.FromMilliseconds(800);
                    _debounceTimer.IsRepeating = false;
                    _debounceTimer.Tick += (_, _) => EvaluateAndFire(NotificationCategory.Charging,
                                                                      NotificationCategory.Discharging);
                    _debounceTimer.Start();
                }
                else
                {
                    // Low / Critical: immediate notification on first downward transition.
                    if (category != _lastNotified)
                        FireNotification(CurrentState, category);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("BatteryService: error processing ReportUpdated", ex);
            }
        });
    }

    /// <summary>
    /// Called after the debounce window expires. Re-reads CurrentState so that
    /// if the charger was plugged/unplugged again within the window, the final
    /// settled state is what drives the notification decision.
    /// </summary>
    private void EvaluateAndFire(params NotificationCategory[] allowedCategories)
    {
        var category = GetCategory(CurrentState);

        // Only fire if we are still in one of the categories that triggered the debounce
        // AND the category has actually changed from what was last notified.
        if (Array.IndexOf(allowedCategories, category) >= 0 && category != _lastNotified)
            FireNotification(CurrentState, category);
    }

    // ── Notification dispatch ──────────────────────────────────────────────

    private void FireNotification(BatteryState state, NotificationCategory category)
    {
        _lastNotified = category;

        var duration = category switch
        {
            NotificationCategory.Critical => NotificationDuration.Critical,
            NotificationCategory.Low      => NotificationDuration.Extended,
            _                             => NotificationDuration.Standard,
        };

        Logger.Info($"BatteryService: firing {category} notification at {state.ChargePercent}%");
        NotificationRequired?.Invoke(this, (state, duration));
    }

    // ── State construction ─────────────────────────────────────────────────

    private static BatteryState BuildState(WinRTBatteryReport report)
    {
        int percent = 0;

        if (report.FullChargeCapacityInMilliwattHours.HasValue &&
            report.RemainingCapacityInMilliwattHours.HasValue &&
            report.FullChargeCapacityInMilliwattHours.Value > 0)
        {
            percent = (int)Math.Round(
                report.RemainingCapacityInMilliwattHours.Value * 100.0
                / report.FullChargeCapacityInMilliwattHours.Value);

            percent = Math.Clamp(percent, 0, 100);
        }

        // On many systems, plugging in while near/full battery reports Idle instead of Charging.
        // Treat any external power source as "charging" for user-facing plug/unplug transitions.
        bool isCharging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;

        return new BatteryState(
            ChargePercent: percent,
            IsCharging:    isCharging,
            IsLow:         percent <= 20,
            IsCritical:    percent <= 10);
    }

    private static NotificationCategory GetCategory(BatteryState state)
    {
        if (state.IsCritical) return NotificationCategory.Critical;
        if (state.IsLow)      return NotificationCategory.Low;
        return state.IsCharging ? NotificationCategory.Charging : NotificationCategory.Discharging;
    }

    // ── Cleanup ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        try
        {
            if (_aggregateBattery != null)
            {
                _aggregateBattery.ReportUpdated -= OnReportUpdated;
                _aggregateBattery = null;
            }
        }
        catch { /* best-effort cleanup */ }
    }
}
