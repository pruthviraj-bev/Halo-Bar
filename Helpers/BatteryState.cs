namespace DynamicIsland.Helpers;

/// <summary>
/// Immutable snapshot of system battery state.
/// Named BatteryState (not BatteryReport) to avoid confusion with
/// Windows.Devices.Power.BatteryReport from the WinRT API.
/// ChargePercent is exposed raw (0–100); XAML controls bind directly
/// and calculate their own visual sizing (e.g. ProgressBar.Value).
/// </summary>
public sealed record BatteryState(
    int  ChargePercent,
    bool IsCharging,
    bool IsLow,       // ChargePercent ≤ 20
    bool IsCritical   // ChargePercent ≤ 10
);
