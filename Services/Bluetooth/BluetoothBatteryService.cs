using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Bluetooth battery capability subsystem.
///
/// Responsibilities:
///   • AEP battery (System.Devices.BatteryLife) — read by the watcher directly
///     (a cheap property arriving with device snapshots).
///   • GATT investigation — run once per device per session when a connected
///     device has no AEP battery. Enumerates the FULL GATT service/characteristic
///     inventory (diagnostics), recognizes the standard Battery Service
///     0x180F / Battery Level 0x2A19, and reads a validated 0–100 level.
///
/// This is a diagnostic-capable pipeline, NOT a polling system and NOT a
/// manufacturer decoder: an absent 0x180F logs the custom 128-bit services for
/// later research instead of guessing that an arbitrary characteristic is
/// battery. Every failure resolves to Unavailable — never a fabricated level.
/// </summary>
public sealed class BluetoothBatteryService
{
    private static readonly Guid BatteryServiceUuid = GattServiceUuids.Battery;            // 0x180F
    private static readonly Guid BatteryLevelUuid = GattCharacteristicUuids.BatteryLevel;  // 0x2A19

    /// <summary>
    /// Full GATT capability investigation. Connects to the device (BLE by AEP
    /// id, classic/dual-mode by the MAC parsed from the AEP id — most earbuds
    /// pair over classic yet expose 0x180F over BLE), enumerates every service
    /// and characteristic (logging UUIDs + properties, never writing or
    /// subscribing), then reads the standard battery level when present.
    /// Returns a <see cref="BluetoothBatteryResult"/>; any failure or absent
    /// service resolves to Unavailable(Unknown) without throwing.
    /// </summary>
    public async Task<BluetoothBatteryResult> InvestigateAsync(string aepDeviceId, bool isLowEnergy, string deviceName)
    {
        string summary = $"Device='{deviceName}' id='{aepDeviceId}' IsBle={isLowEnergy} AEP=Unavailable";
        try
        {
            // Connect probe: the AEP id path first (it can resolve dual-mode
            // devices paired over classic), then the MAC-address path. Both are
            // attempted and the winning path is logged — this pass is about
            // discovering what actually works per device.
            BluetoothLEDevice? leDevice = null;
            string connectPath = "none";
            try
            {
                leDevice = await BluetoothLEDevice.FromIdAsync(aepDeviceId);
                if (leDevice != null) connectPath = "byId";
            }
            catch (Exception ex)
            {
                Logger.Info($"[BluetoothBattery] {summary} ConnectById=Failed ({ex.Message})");
            }
            if (leDevice == null)
            {
                ulong mac = ParseMacFromAepId(aepDeviceId);
                leDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);
                if (leDevice != null) connectPath = mac != 0 ? "byMac" : "byMac(macParseFailed)";
            }
            if (leDevice == null)
            {
                Logger.Info($"[BluetoothBattery] {summary} GattConnect=Failed -> Battery=Unavailable Source=Unknown");
                return BluetoothBatteryResult.Unavailable();
            }
            Logger.Info($"[BluetoothBattery] {summary} GattConnect=Ok({connectPath})");

            var servicesResult = await leDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                Logger.Info($"[BluetoothBattery] {summary} GattServices=DiscoveryFailed({servicesResult.Status}) -> Battery=Unavailable Source=Unknown");
                return BluetoothBatteryResult.Unavailable();
            }

            var services = servicesResult.Services;
            Logger.Info($"[BluetoothBattery] {summary} GattServices={services.Count}");

            int customCount = 0;
            GattDeviceService? batteryService = null;
            foreach (var service in services)
            {
                bool standard = IsStandardUuid(service.Uuid);
                if (service.Uuid == BatteryServiceUuid)
                {
                    batteryService = service;
                }
                else if (!standard)
                {
                    customCount++;
                }

                var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                int charCount = charsResult.Status == GattCommunicationStatus.Success ? charsResult.Characteristics.Count : 0;
                Logger.Info($"[BluetoothBattery]   Service {UuidLabel(service.Uuid)} ({(standard ? "standard" : "custom")}) characteristics={charCount}");

                if (charsResult.Status != GattCommunicationStatus.Success) continue;
                foreach (var ch in charsResult.Characteristics)
                {
                    Logger.Info($"[BluetoothBattery]     Char {UuidLabel(ch.Uuid)} props={ch.CharacteristicProperties}");
                }
            }

            if (batteryService == null)
            {
                Logger.Info($"[BluetoothBattery] {summary} GattBatteryService=NotFound CustomServices={customCount} -> Battery=Unavailable Source=Unknown" +
                            (customCount > 0 ? " VendorInvestigationRequired" : ""));
                return BluetoothBatteryResult.Unavailable();
            }

            Logger.Info($"[BluetoothBattery] {summary} GattBatteryService=Found");

            var batteryChars = await batteryService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (batteryChars.Status != GattCommunicationStatus.Success)
            {
                Logger.Info($"[BluetoothBattery] {summary} BatteryChar=DiscoveryFailed({batteryChars.Status}) -> Battery=Unavailable Source=Unknown");
                return BluetoothBatteryResult.Unavailable();
            }

            var levelCharacteristic = batteryChars.Characteristics.FirstOrDefault(c => c.Uuid == BatteryLevelUuid);
            if (levelCharacteristic == null)
            {
                Logger.Info($"[BluetoothBattery] {summary} BatteryChar=NotFound -> Battery=Unavailable Source=Unknown");
                return BluetoothBatteryResult.Unavailable();
            }

            var readResult = await levelCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (readResult.Status != GattCommunicationStatus.Success)
            {
                Logger.Info($"[BluetoothBattery] {summary} BatteryRead=Failed({readResult.Status}) -> Battery=Unavailable Source=Unknown");
                return BluetoothBatteryResult.Unavailable();
            }

            using var reader = DataReader.FromBuffer(readResult.Value);
            if (reader.UnconsumedBufferLength < 1)
            {
                Logger.Info($"[BluetoothBattery] {summary} BatteryRead=Empty -> Battery=Unavailable Source=Unknown");
                return BluetoothBatteryResult.Unavailable();
            }

            byte level = reader.ReadByte();
            if (level > 100)
            {
                Logger.Info($"[BluetoothBattery] {summary} BatteryRead=Invalid({level}) -> Battery=Unavailable Source=Unknown");
                return BluetoothBatteryResult.Unavailable();
            }

            Logger.Info($"[BluetoothBattery] {summary} GattBatteryService=Found BatteryLevel={level}% Source=StandardGatt");
            return BluetoothBatteryResult.FromLevel(level, BluetoothBatterySource.StandardGatt);
        }
        catch (Exception ex)
        {
            Logger.Error($"[BluetoothBattery] {summary} investigation failed", ex);
            return BluetoothBatteryResult.Unavailable();
        }
    }

    /// <summary>
    /// 16-bit standard UUIDs share the "-0000-1000-8000-00805f9b34fb" suffix;
    /// everything else is a vendor/custom 128-bit UUID.
    /// </summary>
    private static bool IsStandardUuid(Guid uuid)
    {
        const string standardTail = "-0000-1000-8000-00805f9b34fb";
        return uuid.ToString().EndsWith(standardTail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Short "0x180F"-style label for standard UUIDs; full GUID otherwise.</summary>
    private static string UuidLabel(Guid uuid)
    {
        string s = uuid.ToString();
        const string standardTail = "-0000-1000-8000-00805f9b34fb";
        if (s.EndsWith(standardTail, StringComparison.OrdinalIgnoreCase))
            return "0x" + s[..4].ToUpperInvariant();
        return s;
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
