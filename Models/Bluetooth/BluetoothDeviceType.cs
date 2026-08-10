namespace DynamicIsland.Models;

/// <summary>
/// Logical classification of a Bluetooth device. Derived by the watcher from
/// the device name (and protocol); drives the UI icon and descriptive label.
/// Pure domain — no Windows API types.
/// </summary>
public enum BluetoothDeviceType
{
    Unknown,
    Headphones,
    Earbuds,
    Mouse,
    Keyboard,
    Gamepad,
    Watch,
    Phone,
    Other
}
