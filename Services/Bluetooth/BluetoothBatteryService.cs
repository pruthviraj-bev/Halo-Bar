using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Resolves battery information for a known Bluetooth device.
///
/// V1 responsibilities are deliberately narrow:
///   • AEP battery (System.Devices.BatteryLife) is read by the watcher directly
///     — a cheap property that arrives with device snapshots.
///   • GATT 0x180F Battery Service is the on-demand fallback for BLE devices,
///     attempted only when a device is connected and AEP exposed no battery.
///
/// Pipeline is fast → cheap → reliable → fallback. Never fabricates a level:
/// any failure resolves to null (the UI shows no battery rather than a fake 0).
/// </summary>
public sealed class BluetoothBatteryService
{
    /// <summary>
    /// Reads the GATT Battery Service (0x180F) / Battery Level (0x2A19)
    /// characteristic of a BLE device. Returns null on any failure — including
    /// devices that expose no battery service.
    ///
    /// BLE devices connect by their AEP id. Classic/dual-mode devices (most
    /// earbuds pair over classic but STILL expose the 0x180F battery service
    /// over BLE) connect by the MAC parsed from the AEP id — that is how the
    /// vendors' companion apps show battery when the AEP store exposes none.
    /// </summary>
    public async Task<int?> TryReadGattBatteryAsync(string aepDeviceId, bool isLowEnergy)
    {
        try
        {
            var leDevice = isLowEnergy
                ? await BluetoothLEDevice.FromIdAsync(aepDeviceId)
                : await BluetoothLEDevice.FromBluetoothAddressAsync(ParseMacFromAepId(aepDeviceId));
            if (leDevice == null)
            {
                // Classic-paired device with no BLE endpoint — a dual-mode
                // read is impossible (device is classic-only).
                Logger.Info($"BluetoothBatteryService: '{aepDeviceId}' no BLE endpoint — dual-mode battery unavailable");
                return null;
            }

            var servicesResult = await leDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                Logger.Info($"BluetoothBatteryService: '{aepDeviceId}' GATT services query failed ({servicesResult.Status})");
                return null;
            }

            var batteryService = servicesResult.Services.FirstOrDefault(s => s.Uuid == GattServiceUuids.Battery);
            if (batteryService == null)
            {
                Logger.Info($"BluetoothBatteryService: '{aepDeviceId}' exposes no 0x180F battery service");
                return null;
            }

            var charsResult = await batteryService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success) return null;

            var levelCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == GattCharacteristicUuids.BatteryLevel);
            if (levelCharacteristic == null) return null;

            var readResult = await levelCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (readResult.Status != GattCommunicationStatus.Success) return null;

            using var reader = DataReader.FromBuffer(readResult.Value);
            if (reader.UnconsumedBufferLength < 1) return null;

            byte level = reader.ReadByte();
            return level <= 100 ? (int?)level : null;
        }
        catch (Exception ex)
        {
            Logger.Error("BluetoothBatteryService: GATT battery read failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Extracts the device MAC address from an AEP id
    /// ("Bluetooth#Bluetooth44:a3:bb:69:21:83-b0:38:e2:a4:0b:c3" → the part
    /// after the last '-') and packs it into the ulong BluetoothLEDevice
    /// expects. Falls back to 0 (which fails the connect gracefully) when the
    /// id has no parseable MAC.
    /// </summary>
    private static ulong ParseMacFromAepId(string aepDeviceId)
    {
        int dash = aepDeviceId.LastIndexOf('-');
        string mac = dash >= 0 ? aepDeviceId[(dash + 1)..] : aepDeviceId;
        ulong value = 0;
        foreach (var part in mac.Split(':'))
        {
            if (!byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var octet))
                return 0;
            value = (value << 8) | octet;
        }
        return value;
    }
}
