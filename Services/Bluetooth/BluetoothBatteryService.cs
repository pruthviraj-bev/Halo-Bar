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
    /// devices that are classic-only or expose no battery service.
    /// </summary>
    public async Task<int?> TryReadGattBatteryAsync(string aepDeviceId)
    {
        try
        {
            var leDevice = await BluetoothLEDevice.FromIdAsync(aepDeviceId);
            if (leDevice == null) return null;

            var servicesResult = await leDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success) return null;

            var batteryService = servicesResult.Services.FirstOrDefault(s => s.Uuid == GattServiceUuids.Battery);
            if (batteryService == null) return null;

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
}
