using System;
using System.IO;
using System.Text.Json;
using DynamicIsland.Helpers;
using DynamicIsland.Services;
using Microsoft.Win32;

namespace DynamicIsland.Models;

/// <summary>
/// Central persisted application settings (Halo Bar V1 Settings, PASS 21).
/// Every mutable setting survives a restart through a single JSON document at
/// %LOCALAPPDATA%\DynamicIsland\settings.json. Raises <see cref="Changed"/> so
/// live surfaces (footer metrics, accent brushes, Bluetooth popup) update
/// immediately without a restart.
/// </summary>
public static class AppSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland",
        "settings.json"
    );

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact JSON, mirroring ClipboardHistoryStore's settings encoding.
        WriteIndented = false
    };

    /// <summary>Raised after any setting changes so live UI can react immediately.</summary>
    public static event Action? Changed;

    public static bool StartWithWindows { get; private set; }
    public static string AccentColor { get; private set; } = "#FF5B9CFF";
    public static bool ShowWeather { get; private set; } = true;
    public static bool ShowCpu { get; private set; } = true;
    public static bool ShowDisk { get; private set; } = true;
    public static bool ShowRam { get; private set; } = true;
    public static bool ShowNetworkSpeed { get; private set; } = true;
    public static string SelectedDrive { get; private set; } = "C";
    public static int ClipboardAutoDelete { get; private set; } = 30;
    public static bool ShowBluetoothConnectionPopup { get; private set; } = true;

    private sealed class AppSettingsFile
    {
        public bool StartWithWindows { get; set; }
        public string? AccentColor { get; set; }
        public bool? ShowWeather { get; set; }
        public bool? ShowCpu { get; set; }
        public bool? ShowDisk { get; set; }
        public bool? ShowRam { get; set; }
        public bool? ShowNetworkSpeed { get; set; }
        public string? SelectedDrive { get; set; }
        public int? ClipboardAutoDelete { get; set; }
        public bool? ShowBluetoothConnectionPopup { get; set; }
    }

    /// <summary>
    /// Loads persisted settings and re-asserts startup registration. Never throws.
    /// Called once from OnLaunched before any window is created.
    /// </summary>
    public static void Initialize()
    {
        Load();
        if (StartWithWindows)
            ApplyStartWithWindows(true);
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return;
            }

            var json = File.ReadAllText(SettingsFile);
            var data = JsonSerializer.Deserialize<AppSettingsFile>(json, SerializerOptions);
            if (data == null)
            {
                return;
            }

            StartWithWindows = data.StartWithWindows;
            if (!string.IsNullOrWhiteSpace(data.AccentColor))
                AccentColor = data.AccentColor;
            if (data.ShowWeather.HasValue) ShowWeather = data.ShowWeather.Value;
            if (data.ShowCpu.HasValue) ShowCpu = data.ShowCpu.Value;
            if (data.ShowDisk.HasValue) ShowDisk = data.ShowDisk.Value;
            if (data.ShowRam.HasValue) ShowRam = data.ShowRam.Value;
            if (data.ShowNetworkSpeed.HasValue) ShowNetworkSpeed = data.ShowNetworkSpeed.Value;
            if (!string.IsNullOrWhiteSpace(data.SelectedDrive))
                SelectedDrive = data.SelectedDrive.TrimEnd('\\', ':').ToUpperInvariant();
            if (data.ClipboardAutoDelete.HasValue)
                ClipboardAutoDelete = Math.Max(0, data.ClipboardAutoDelete.Value);
            if (data.ShowBluetoothConnectionPopup.HasValue)
                ShowBluetoothConnectionPopup = data.ShowBluetoothConnectionPopup.Value;
        }
        catch (Exception ex)
        {
            Logger.Error("AppSettings: failed to load settings", ex);
        }
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var data = new AppSettingsFile
            {
                StartWithWindows = StartWithWindows,
                AccentColor = AccentColor,
                ShowWeather = ShowWeather,
                ShowCpu = ShowCpu,
                ShowDisk = ShowDisk,
                ShowRam = ShowRam,
                ShowNetworkSpeed = ShowNetworkSpeed,
                SelectedDrive = SelectedDrive,
                ClipboardAutoDelete = ClipboardAutoDelete,
                ShowBluetoothConnectionPopup = ShowBluetoothConnectionPopup,
            };
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(data, SerializerOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("AppSettings: failed to persist settings", ex);
        }
    }

    // ── Getters with live persistence ────────────────────────────────────────

    public static void SetStartWithWindows(bool value)
    {
        if (StartWithWindows == value) return;
        StartWithWindows = value;
        ApplyStartWithWindows(value);
        Save();
        Changed?.Invoke();
    }

    public static void SetAccentColor(string hex)
    {
        if (string.Equals(AccentColor, hex, StringComparison.OrdinalIgnoreCase)) return;
        AccentColor = hex;
        Save();
        AccentManager.Apply(hex);
        Changed?.Invoke();
    }

    public static void SetShowWeather(bool value)
    {
        if (ShowWeather == value) return;
        ShowWeather = value;
        Save();
        Changed?.Invoke();
    }

    public static void SetShowCpu(bool value)
    {
        if (ShowCpu == value) return;
        ShowCpu = value;
        Save();
        Changed?.Invoke();
    }

    public static void SetShowDisk(bool value)
    {
        if (ShowDisk == value) return;
        ShowDisk = value;
        Save();
        Changed?.Invoke();
    }

    public static void SetShowRam(bool value)
    {
        if (ShowRam == value) return;
        ShowRam = value;
        Save();
        Changed?.Invoke();
    }

    public static void SetShowNetworkSpeed(bool value)
    {
        if (ShowNetworkSpeed == value) return;
        ShowNetworkSpeed = value;
        Save();
        Changed?.Invoke();
    }

    public static void SetSelectedDrive(string driveLetter)
    {
        var normalized = string.IsNullOrWhiteSpace(driveLetter)
            ? "C"
            : driveLetter.TrimEnd('\\', ':').ToUpperInvariant();
        if (string.Equals(SelectedDrive, normalized, StringComparison.Ordinal)) return;
        SelectedDrive = normalized;
        Save();
        Changed?.Invoke();
    }

    public static void SetClipboardAutoDelete(int days)
    {
        int clamped = Math.Max(0, days);
        if (ClipboardAutoDelete == clamped) return;
        ClipboardAutoDelete = clamped;
        App.ClipboardService.SetRetentionDays(clamped);
        Save();
        Changed?.Invoke();
    }

    public static void SetShowBluetoothConnectionPopup(bool value)
    {
        if (ShowBluetoothConnectionPopup == value) return;
        ShowBluetoothConnectionPopup = value;
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Registers/unregisters "HaloBar" in HKCU\...\Run so the app launches with
    /// Windows. Unpackaged app — the Run key is the natural startup mechanism.
    /// Never throws.
    /// </summary>
    public static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(runKeyPath);
            if (key == null) return;

            if (enabled)
            {
                key.SetValue("HaloBar", Environment.ProcessPath ?? "DynamicIsland.exe");
            }
            else
            {
                key.DeleteValue("HaloBar", throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AppSettings: failed to update startup registry entry", ex);
        }
    }
}
