namespace DynamicIsland.Models;

/// <summary>
/// Observed connection state as reported by Windows.
///
/// Deliberately does NOT include Connecting/Disconnecting: Windows only
/// reports Disconnected/Connected, and V1 has no connect/disconnect operations
/// of its own. A future operation (GATT/RFCOMM/audio routing) carries its own
/// transient state — the domain model never fabricates a state machine the
/// platform does not provide.
/// </summary>
public enum BluetoothConnectionState
{
    Unknown,
    Disconnected,
    Connected
}
