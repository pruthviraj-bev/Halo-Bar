using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Microsoft.UI.Dispatching;
using DynamicIsland.Helpers;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Watches paired Bluetooth devices (classic + BLE) through the Windows
/// Association-Endpoint (AEP) store, translating DeviceInformation into
/// platform-independent <see cref="BluetoothDeviceInfo"/> snapshots.
///
/// This is the ONLY layer that touches Windows device objects. It raises
/// domain events that <see cref="BluetoothService"/> consumes; it decides
/// nothing about the UI.
///
/// Lifecycle: <see cref="Start"/> begins the watcher, <see cref="Stop"/> tears
/// it down. BluetoothService owns the radio on/off lifecycle and drives these
/// calls; <see cref="WatcherStopped"/> fires only for unexpected stops (radio
/// toggled off, watcher error) so the service can recover.
///
/// All events are marshaled to the supplied DispatcherQueue.
/// </summary>
public sealed class BluetoothDeviceWatcher
{
    // AEP protocol GUIDs — classic Bluetooth and Bluetooth LE.
    private const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa4f431f0}";

    // Both protocols, paired devices only. One watcher covers classic + BLE
    // through the AEP store (per-protocol selectors cannot be combined).
    private static readonly string AqsFilter =
        $"((System.Devices.Aep.ProtocolId:=\"{ClassicProtocol}\") OR " +
        $"(System.Devices.Aep.ProtocolId:=\"{LowEnergyProtocol}\")) AND " +
        "(System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True)";

    private static readonly string[] RequestedProperties =
    {
        "System.ItemNameDisplay",
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.ProtocolId",
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.IsPresent",
        "System.Devices.Aep.IsConnected",
        "System.Devices.BatteryLife",
        // Bluetooth Class of Device (major class) — identifies a phone/watch/
        // audio device even when the user has renamed it to a custom name
        // (name-based classification alone cannot see "PRUTHVIRAJ Bevinkatti"
        // is a phone). Phone = 0x200, Audio/Video = 0x400, Wearable = 0x700.
        "System.Devices.Aep.Bluetooth.Cod.Major"
    };

    private readonly DispatcherQueue _dispatcherQueue;
    private DeviceWatcher? _watcher;
    private bool _started;

    public event EventHandler<BluetoothDeviceInfo>? DeviceAdded;
    public event EventHandler<BluetoothDeviceInfo>? DeviceUpdated;
    public event EventHandler<string>? DeviceRemoved;
    public event EventHandler? EnumerationCompleted;
    public event EventHandler? WatcherStopped;

    public BluetoothDeviceWatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool IsRunning => _started;

    /// <summary>
    /// Re-fetches one device's full snapshot and raises <see cref="DeviceUpdated"/>.
    /// Used by the service to re-verify a device whose connection latch expired
    /// (see BluetoothService): it distinguishes "genuinely still connected" from
    /// "disconnected but present" without waiting for a spontaneous watcher event.
    /// </summary>
    public void Refresh(string id) => _ = RefreshAndRaiseUpdatedAsync(id);

    public void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            _watcher = DeviceInformation.CreateWatcher(AqsFilter, RequestedProperties, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += OnAdded;
            _watcher.Updated += OnUpdated;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Stopped += OnStopped;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("BluetoothDeviceWatcher: failed to start", ex);
            _started = false;
            _watcher = null;
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        if (_watcher != null)
        {
            try { _watcher.Stop(); } catch { /* already stopped */ }
            _watcher.Added -= OnAdded;
            _watcher.Updated -= OnUpdated;
            _watcher.Removed -= OnRemoved;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stopped -= OnStopped;
            _watcher = null;
        }
    }

    // ── DeviceWatcher events → domain events on the dispatcher ─────────────

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        var device = Parse(info);
        if (device == null) return;
        _dispatcherQueue.TryEnqueue(() => DeviceAdded?.Invoke(this, device));
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate info)
        => _ = RefreshAndRaiseUpdatedAsync(info.Id);

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate info)
    {
        string id = info.Id;
        _dispatcherQueue.TryEnqueue(() => DeviceRemoved?.Invoke(this, id));
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        => _dispatcherQueue.TryEnqueue(() => EnumerationCompleted?.Invoke(this, EventArgs.Empty));

    private void OnStopped(DeviceWatcher sender, object args)
    {
        // Stop() clears _started BEFORE calling watcher.Stop(), so this only
        // fires for unexpected stops — the signal the service needs to recover.
        bool wasRunning = _started;
        _started = false;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (wasRunning)
                WatcherStopped?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// An Updated event carries only the changed properties, so re-fetch the
    /// full snapshot before raising a complete domain update.
    /// </summary>
    private async Task RefreshAndRaiseUpdatedAsync(string id)
    {
        try
        {
            var full = await DeviceInformation.CreateFromIdAsync(id, RequestedProperties, DeviceInformationKind.AssociationEndpoint);
            if (full == null) return;
            var device = Parse(full);
            if (device == null) return;
            _dispatcherQueue.TryEnqueue(() => DeviceUpdated?.Invoke(this, device));
        }
        catch (Exception ex)
        {
            Logger.Error("BluetoothDeviceWatcher: failed to refresh updated device", ex);
        }
    }

    // ── DeviceInformation → domain snapshot ────────────────────────────────

    private static BluetoothDeviceInfo? Parse(DeviceInformation info)
    {
        string name = info.Properties.TryGetValue("System.ItemNameDisplay", out var nameVal) && nameVal is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : info.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;

        info.Properties.TryGetValue("System.Devices.Aep.ProtocolId", out var protoVal);
        bool isLowEnergy = protoVal is Guid protoGuid
            && protoGuid.ToString("D").Equals(LowEnergyProtocol, StringComparison.OrdinalIgnoreCase);

        bool connected = GetBool(info, "System.Devices.Aep.IsConnected");
        bool present = GetBool(info, "System.Devices.Aep.IsPresent");
        bool paired = GetBool(info, "System.Devices.Aep.IsPaired");
        uint? codMajor = GetCodMajor(info);

        info.Properties.TryGetValue("System.Devices.BatteryLife", out var batVal);
        int? battery = batVal is int batInt
            ? (batInt >= 0 ? batInt : (int?)null)
            : null;

        // Diagnostic snapshot line (INFO, matching ClipboardService's per-item
        // observability): the property VALUE TYPES are logged so a ProtocolId or
        // BatteryLife type mismatch (silent gate/parse failure) is visible.
        Logger.Info(
            $"BluetoothDeviceWatcher: '{name}' id='{info.Id}' " +
            $"proto={protoVal?.GetType().Name}:{protoVal} le={isLowEnergy} " +
            $"conn={connected} present={present} paired={paired} " +
            $"battery={batVal?.GetType().Name}:{batVal} codMajor={codMajor?.ToString("X") ?? "-"}");

        var nameType = ClassifyDeviceType(name);
        var type = ClassifyWithCod(nameType, codMajor);
        Logger.Info($"BluetoothDeviceWatcher: '{name}' type={type} codMajor={codMajor?.ToString("X") ?? "-"}");
        return new BluetoothDeviceInfo
        {
            Id = info.Id,
            Name = name,
            Type = type,
            ConnectionState = connected ? BluetoothConnectionState.Connected : BluetoothConnectionState.Disconnected,
            IsPaired = paired,
            IsPresent = present,
            IsLowEnergy = isLowEnergy,
            Battery = battery.HasValue ? BluetoothBatteryInfo.FromDeviceLevel(battery) : null
        };
    }

    private static bool GetBool(DeviceInformation info, string key)
        => info.Properties.TryGetValue(key, out var val) && val is bool b && b;

    /// <summary>
    /// Reads the Bluetooth Class of Device major class from the AEP store.
    /// Value type varies (uint/int/ushort/string) depending on the store, so
    /// all common projections are accepted defensively.
    /// </summary>
    private static uint? GetCodMajor(DeviceInformation info)
    {
        if (!info.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out var val) || val == null)
            return null;
        return val switch
        {
            uint u => u,
            ushort us => us,
            int i => i >= 0 ? (uint)i : null,
            long l => l >= 0 ? (uint)l : null,
            string s when uint.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// The name hint wins when it identifies the device; the COD major class
    /// fills the gap for custom-named devices (e.g. a phone renamed to a
    /// person's name still reports the Phone major class). Measured on this
    /// machine: AEP Cod.Major reports the classic major class SHIFTED >> 4
    /// (phone=0x2, audio=0x4, watch would be 0x7), so the shifted values are
    /// matched first; the unshifted standard classes (0x20 Phone, 0x40
    /// Audio/Video, 0x70 Wearable) are accepted too in case another machine
    /// reports them unshifted.
    /// </summary>
    private static BluetoothDeviceType ClassifyWithCod(BluetoothDeviceType nameType, uint? codMajor)
    {
        if (nameType != BluetoothDeviceType.Other) return nameType;
        return codMajor switch
        {
            0x2 or 0x20 => BluetoothDeviceType.Phone,      // Phone
            0x4 or 0x40 => BluetoothDeviceType.Headphones, // Audio/Video — speakers, headsets
            0x6 or 0x60 => BluetoothDeviceType.Tv,         // Imaging — TVs
            0x7 or 0x70 => BluetoothDeviceType.Watch,      // Wearable
            _ => BluetoothDeviceType.Other
        };
    }

    /// <summary>
    /// Name-based classification (the Windows APIs expose no reliable type for
    /// AEP endpoints; BluetoothClassOfDevice would require per-device objects).
    /// Ordered so the more specific hints win.
    /// </summary>
    private static BluetoothDeviceType ClassifyDeviceType(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("earbud") || n.Contains("buds") || n.Contains("airpod") || n.Contains("freebud")) return BluetoothDeviceType.Earbuds;
        if (n.Contains("headphone") || n.Contains("headset") || n.Contains("wh-") || n.Contains("audio") || n.Contains("speaker") || n.Contains("sound")) return BluetoothDeviceType.Headphones;
        if (n.Contains("keyboard") || n.Contains("keys") || n.Contains("kb") || n.Contains("type cover")) return BluetoothDeviceType.Keyboard;
        if (n.Contains("mouse") || n.Contains("trackpad") || n.Contains("pointer")) return BluetoothDeviceType.Mouse;
        if (n.Contains("gamepad") || n.Contains("controller") || n.Contains("joy-") || n.Contains("xbox")) return BluetoothDeviceType.Gamepad;
        if (n.Contains("watch") || n.Contains("band")) return BluetoothDeviceType.Watch;
        // Phone hints include the common Android-brand model names that carry
        // no literal "phone" (e.g. "Redmi Note 6 Pro") — without them those
        // fall through to Other and render the generic microphone asset.
        if (n.Contains("phone") || n.Contains("galaxy") || n.Contains("iphone") || n.Contains("pixel")
            || n.Contains("redmi") || n.Contains("note") || n.Contains("xiaomi") || n.Contains("oneplus")
            || n.Contains("oppo") || n.Contains("vivo") || n.Contains("nokia") || n.Contains("huawei")
            || n.Contains("honor") || n.Contains("poco") || n.Contains("moto")) return BluetoothDeviceType.Phone;
        if (n.Contains("printer")) return BluetoothDeviceType.Printer;
        if (n.Contains("tv") || n.Contains("television") || n.Contains("bravia")) return BluetoothDeviceType.Tv;
        return BluetoothDeviceType.Other;
    }
}
