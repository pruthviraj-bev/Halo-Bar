namespace DynamicIsland.Models;

/// <summary>
/// Pure domain snapshot of a Bluetooth device. 100% platform-independent — no
/// Windows objects (BluetoothLEDevice, DeviceInformation, ...) ever reach the
/// UI. The watcher builds these from Windows device data; the service caches
/// and publishes them.
///
/// Properties are settable (not init-only) because the type is used as an
/// x:DataType — the XAML compiler generates property-assignment metadata that
/// cannot target init-only setters (same convention as ClipboardItem).
/// </summary>
public sealed class BluetoothDeviceInfo
{
    /// <summary>Stable AEP device id (also the cache key).</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public BluetoothDeviceType Type { get; set; } = BluetoothDeviceType.Unknown;

    public BluetoothConnectionState ConnectionState { get; set; } = BluetoothConnectionState.Unknown;

    public bool IsPaired { get; set; }

    /// <summary>False when the paired device is out of range.</summary>
    public bool IsPresent { get; set; }

    /// <summary>True when the device speaks BLE (used to decide the GATT battery fallback).</summary>
    public bool IsLowEnergy { get; set; }

    /// <summary>Null when Windows exposes no battery — never a fabricated 0.</summary>
    public BluetoothBatteryInfo? Battery { get; set; }

    public bool IsConnected => ConnectionState == BluetoothConnectionState.Connected;
}
