using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Microsoft.UI.Dispatching;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Bluetooth facade — the single source of truth for Bluetooth state.
///
/// Owns:
///   • adapter status (radio-driven: NoAdapter / Disabled / Initializing / Ready)
///   • the device cache and public Devices collection (UI-thread mutations only)
///   • the AEP watcher lifecycle (start / stop / restart on radio toggles)
///   • coarse BluetoothUpdated + granular DeviceChanged events
///
/// The UI consumes state and events; it never touches Windows Bluetooth APIs.
/// </summary>
public class BluetoothService
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly BluetoothDeviceWatcher _watcher;
    private readonly BluetoothBatteryService _batteryService = new();
    private readonly Dictionary<string, BluetoothDeviceInfo> _cache = new();
    private readonly HashSet<string> _gattAttempted = new();
    private Radio? _radio;
    private bool _initialized;

    public BluetoothAdapterStatus AdapterStatus { get; private set; } = BluetoothAdapterStatus.Initializing;

    /// <summary>Ordered device snapshots (connected first, then by name). UI-thread safe.</summary>
    public ObservableCollection<BluetoothDeviceInfo> Devices { get; } = new();

    /// <summary>Coarse: "something changed, refresh." Existing consumers already understand this event.</summary>
    public event EventHandler? BluetoothUpdated;

    /// <summary>Granular: exactly which device changed, and how.</summary>
    public event EventHandler<BluetoothDeviceChangedEventArgs>? DeviceChanged;

    public BluetoothService()
    {
        // Constructed on the UI thread (App composition root), same as the previous service.
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _watcher = new BluetoothDeviceWatcher(_dispatcherQueue);
        _watcher.DeviceAdded += (_, device) => OnDeviceSnapshot(device, BluetoothDeviceChange.Added);
        _watcher.DeviceUpdated += (_, device) => OnDeviceSnapshot(device, BluetoothDeviceChange.Updated);
        _watcher.DeviceRemoved += (_, id) => OnDeviceRemoved(id);
        _watcher.EnumerationCompleted += (_, _) => OnEnumerationCompleted();
        _watcher.WatcherStopped += OnWatcherStopped;
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        _ = DiscoverRadioAsync();
    }

    // ── Adapter status / radio lifecycle ───────────────────────────────────

    private async Task DiscoverRadioAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _radio = radios?.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);

            if (_radio == null)
            {
                AdapterStatus = BluetoothAdapterStatus.NoAdapter;
                _watcher.Stop();
                RaiseUpdated();
                return;
            }

            _radio.StateChanged += OnRadioStateChanged;
            ApplyRadioState();
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("BluetoothService: radio discovery failed", ex);
            AdapterStatus = BluetoothAdapterStatus.NoAdapter;
            RaiseUpdated();
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
        => _dispatcherQueue.TryEnqueue(ApplyRadioState);

    private void ApplyRadioState()
    {
        if (_radio == null) return;

        if (_radio.State == RadioState.On)
        {
            AdapterStatus = BluetoothAdapterStatus.Initializing;
            if (!_watcher.IsRunning) _watcher.Start();
            RaiseUpdated();
        }
        else
        {
            AdapterStatus = BluetoothAdapterStatus.Disabled;
            _watcher.Stop();
            _cache.Clear();
            _gattAttempted.Clear();
            RebuildDevices();
            RaiseUpdated();
        }
    }

    /// <summary>
    /// Watcher died unexpectedly (radio toggled or an error). Re-check the
    /// radio and recover after a short delay; ApplyRadioState restarts the
    /// watcher only when the radio is genuinely on, so this cannot hot-loop.
    /// </summary>
    private void OnWatcherStopped(object? sender, EventArgs e)
        => _ = Task.Delay(1000).ContinueWith(_ => _dispatcherQueue.TryEnqueue(ApplyRadioState));

    private void OnEnumerationCompleted()
    {
        // Initial enumeration done — the snapshot is stable now.
        if (_radio?.State == RadioState.On)
            AdapterStatus = BluetoothAdapterStatus.Ready;
        RaiseUpdated();
    }

    // ── Device cache ───────────────────────────────────────────────────────

    private void OnDeviceSnapshot(BluetoothDeviceInfo device, BluetoothDeviceChange change)
    {
        // Guard against late-arriving watcher events: a snapshot that lands after
        // the radio was toggled off (cache cleared) must not resurrect devices,
        // and an Updated must never introduce a device that isn't cached.
        if (AdapterStatus != BluetoothAdapterStatus.Initializing &&
            AdapterStatus != BluetoothAdapterStatus.Ready)
        {
            return;
        }
        if (change == BluetoothDeviceChange.Updated && !_cache.ContainsKey(device.Id))
        {
            return;
        }

        // GATT battery fallback: connected device without an AEP battery →
        // attempt once per session (never repeatedly hammer the device). BLE
        // devices connect by AEP id; classic/dual-mode devices (most earbuds
        // are dual-mode and expose 0x180F over BLE) connect by MAC parsed from
        // the AEP id. Failure resolves to null — never a fabricated level.
        if (device.IsConnected && device.Battery == null && _gattAttempted.Add(device.Id))
        {
            Helpers.Logger.Info($"BluetoothService: attempting GATT battery read for '{device.Name}' (le={device.IsLowEnergy})");
            _ = ResolveGattBatteryAsync(device);
        }

        _cache[device.Id] = device;
        RebuildDevices();
        RaiseUpdated();
        DeviceChanged?.Invoke(this, new BluetoothDeviceChangedEventArgs(device, change));
    }

    private void OnDeviceRemoved(string id)
    {
        if (!_cache.TryGetValue(id, out var removed)) return;

        _cache.Remove(id);
        RebuildDevices();
        RaiseUpdated();
        DeviceChanged?.Invoke(this, new BluetoothDeviceChangedEventArgs(removed, BluetoothDeviceChange.Removed));
    }

    private async Task ResolveGattBatteryAsync(BluetoothDeviceInfo device)
    {
        int? level = await _batteryService.TryReadGattBatteryAsync(device.Id, device.IsLowEnergy);
        if (!level.HasValue) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_cache.TryGetValue(device.Id, out var cached)) return;

            var updated = new BluetoothDeviceInfo
            {
                Id = cached.Id,
                Name = cached.Name,
                Type = cached.Type,
                ConnectionState = cached.ConnectionState,
                IsPaired = cached.IsPaired,
                IsPresent = cached.IsPresent,
                IsLowEnergy = cached.IsLowEnergy,
                Battery = BluetoothBatteryInfo.FromDeviceLevel(level)
            };

            _cache[device.Id] = updated;
            RebuildDevices();
            RaiseUpdated();
            DeviceChanged?.Invoke(this, new BluetoothDeviceChangedEventArgs(updated, BluetoothDeviceChange.Updated));
        });
    }

    private void RebuildDevices()
    {
        Devices.Clear();
        foreach (var device in _cache.Values
                     .OrderByDescending(d => d.IsConnected)
                     .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            Devices.Add(device);
        }
    }

    private void RaiseUpdated() => BluetoothUpdated?.Invoke(this, EventArgs.Empty);
}

/// <summary>How a device snapshot changed.</summary>
public enum BluetoothDeviceChange
{
    Added,
    Updated,
    Removed
}

public sealed class BluetoothDeviceChangedEventArgs : EventArgs
{
    public BluetoothDeviceInfo Device { get; }
    public BluetoothDeviceChange Change { get; }

    public BluetoothDeviceChangedEventArgs(BluetoothDeviceInfo device, BluetoothDeviceChange change)
    {
        Device = device;
        Change = change;
    }
}
