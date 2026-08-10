namespace DynamicIsland.Models;

/// <summary>
/// Top-level Bluetooth adapter state, derived from radio discovery. The UI uses
/// this to render honest empty states (no adapter / off / scanning) instead of
/// a blank list — "honest degradation" applied to the adapter itself.
/// </summary>
public enum BluetoothAdapterStatus
{
    /// <summary>No Bluetooth radio exists on this machine.</summary>
    NoAdapter,

    /// <summary>A radio exists but is switched off.</summary>
    Disabled,

    /// <summary>Radio is on; the device watcher has not finished first enumeration yet.</summary>
    Initializing,

    /// <summary>Radio on and the initial enumeration is complete — Devices is stable.</summary>
    Ready
}
