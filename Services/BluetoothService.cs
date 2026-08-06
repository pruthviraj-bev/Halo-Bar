using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Microsoft.UI.Dispatching;
using DynamicIsland.Controls;

namespace DynamicIsland.Services;

public class BluetoothService
{
    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;

    public bool IsBluetoothAvailable { get; private set; } = false;
    public bool IsBluetoothEnabled { get; private set; } = false;
    public List<BluetoothDeviceModel> Devices { get; private set; } = new();

    public event EventHandler? BluetoothUpdated;

    public BluetoothService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public void Initialize()
    {
        // Initial query
        _ = QueryBluetoothStatusAsync();

        // Poll every 5 seconds for live status and battery updates
        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(5);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += async (_, _) => await QueryBluetoothStatusAsync();
        _pollTimer.Start();
    }

    public async Task QueryBluetoothStatusAsync()
    {
        try
        {
            // 1. Check if Bluetooth Adapter is available
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            IsBluetoothAvailable = adapter != null;
            if (!IsBluetoothAvailable)
            {
                IsBluetoothEnabled = false;
                Devices.Clear();
                _dispatcherQueue.TryEnqueue(() => BluetoothUpdated?.Invoke(this, EventArgs.Empty));
                return;
            }

            // In typical cases, if we retrieved the default adapter, Bluetooth is enabled at the radio level.
            IsBluetoothEnabled = adapter!.IsLowEnergySupported || adapter.IsClassicSupported;

            // 2. Enumerate paired/connected Bluetooth devices and query their properties.
            // Property keys for Connection status and Battery level.
            string[] requestedProperties = new[]
            {
                "System.Devices.Aep.IsConnected",
                "System.Devices.BatteryLife",
                "System.Devices.Aep.DeviceAddress",
                "System.Devices.Aep.ProtocolId"
            };

            // Query only Bluetooth AEP (Association Endpoint) devices.
            // Standard AQS filter for Bluetooth devices:
            string aqsFilter = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\""; // Bluetooth protocol GUID

            var deviceInfos = await DeviceInformation.FindAllAsync(aqsFilter, requestedProperties);

            var list = new List<BluetoothDeviceModel>();
            foreach (var info in deviceInfos)
            {
                if (string.IsNullOrEmpty(info.Name)) continue;

                // Read property values safely
                bool isConnected = false;
                if (info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connVal) && connVal is bool connBool)
                {
                    isConnected = connBool;
                }

                int? battery = null;
                if (info.Properties.TryGetValue("System.Devices.BatteryLife", out var batVal) && batVal is int batInt)
                {
                    battery = batInt;
                }

                // Determine device class icon
                AppIconKind iconKind = AppIconKind.Bluetooth; // Default Bluetooth icon
                string nameLower = info.Name.ToLowerInvariant();
                
                if (nameLower.Contains("headphone") || nameLower.Contains("headset") || nameLower.Contains("wh-") || nameLower.Contains("earbud") || nameLower.Contains("buds") || nameLower.Contains("audio") || nameLower.Contains("speaker") || nameLower.Contains("sound"))
                {
                    iconKind = AppIconKind.Headphone; // Headphone icon
                }
                else if (nameLower.Contains("keyboard") || nameLower.Contains("keys") || nameLower.Contains("kb"))
                {
                    iconKind = AppIconKind.Keyboard; // Keyboard icon
                }
                else if (nameLower.Contains("mouse") || nameLower.Contains("trackpad") || nameLower.Contains("pointer"))
                {
                    iconKind = AppIconKind.Mouse; // Mouse icon
                }

                list.Add(new BluetoothDeviceModel(
                    Id: info.Id,
                    Name: info.Name,
                    IconKind: iconKind,
                    IsConnected: isConnected,
                    BatteryPercent: battery
                ));
            }

            // Sort connected devices to the top, then alphabetically
            Devices = list.OrderByDescending(d => d.IsConnected)
                          .ThenBy(d => d.Name)
                          .ToList();

            _dispatcherQueue.TryEnqueue(() => BluetoothUpdated?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("BluetoothService: failed to query bluetooth status", ex);
            IsBluetoothAvailable = false;
            IsBluetoothEnabled = false;
            Devices.Clear();
            _dispatcherQueue.TryEnqueue(() => BluetoothUpdated?.Invoke(this, EventArgs.Empty));
        }
    }
}

public record BluetoothDeviceModel(string Id, string Name, AppIconKind IconKind, bool IsConnected, int? BatteryPercent);
