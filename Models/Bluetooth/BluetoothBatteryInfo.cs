namespace DynamicIsland.Models;

/// <summary>
/// Battery levels for a Bluetooth device.
///
/// V1 populates <see cref="DeviceLevel"/> only — Windows exposes a single
/// percentage (System.Devices.BatteryLife, or the GATT 0x180F Battery Level
/// characteristic which also has one level). Left/Right/Case are the future
/// shape for vendor-specific providers and remain null — never fabricate
/// values for unavailable data.
/// </summary>
public sealed class BluetoothBatteryInfo
{
    public int? DeviceLevel { get; init; }
    public int? LeftLevel { get; init; }
    public int? RightLevel { get; init; }
    public int? CaseLevel { get; init; }

    public static BluetoothBatteryInfo FromDeviceLevel(int? level)
        => new() { DeviceLevel = level };
}
