namespace DynamicIsland.Models;

/// <summary>
/// Which provider produced a Bluetooth battery level. Aep and StandardGatt are
/// implemented; VendorGatt and Hfp are reserved for future providers — the
/// result abstraction exists so those can be added without rewriting
/// BluetoothService.
/// </summary>
public enum BluetoothBatterySource
{
    /// <summary>Windows device property (System.Devices.BatteryLife).</summary>
    Aep,

    /// <summary>Standard Bluetooth Battery Service 0x180F / 0x2A19 read.</summary>
    StandardGatt,

    /// <summary>Vendor/custom 128-bit GATT service (future provider).</summary>
    VendorGatt,

    /// <summary>Hands-Free Profile battery (future provider).</summary>
    Hfp,

    /// <summary>No provider produced a level.</summary>
    Unknown
}

/// <summary>
/// Capability result of a Bluetooth battery investigation. IsAvailable=false
/// means the device did not expose battery through the attempted providers —
/// never a fabricated level. Carries the source so future providers (VendorGatt,
/// Hfp) can slot in without touching the publishing path.
/// </summary>
public sealed class BluetoothBatteryResult
{
    public bool IsAvailable { get; init; }
    public int? Percentage { get; init; }
    public BluetoothBatterySource Source { get; init; }

    public static BluetoothBatteryResult FromLevel(int level, BluetoothBatterySource source)
        => new() { IsAvailable = true, Percentage = level, Source = source };

    public static BluetoothBatteryResult Unavailable(BluetoothBatterySource source = BluetoothBatterySource.Unknown)
        => new() { IsAvailable = false, Percentage = null, Source = source };
}
