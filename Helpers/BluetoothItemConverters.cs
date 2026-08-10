using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using DynamicIsland.Controls;
using DynamicIsland.Models;

namespace DynamicIsland.Helpers;

/// <summary>BluetoothDeviceType → AppIconKind for the device list rows.</summary>
public class BluetoothTypeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is BluetoothDeviceType type ? type switch
        {
            BluetoothDeviceType.Headphones or BluetoothDeviceType.Earbuds => AppIconKind.Headphone,
            BluetoothDeviceType.Mouse => AppIconKind.Mouse,
            BluetoothDeviceType.Keyboard => AppIconKind.Keyboard,
            _ => AppIconKind.Bluetooth
        } : AppIconKind.Bluetooth;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Device → sub-line, e.g. "Headphones · 80%". Battery shown only when Windows
/// exposes it — unavailable battery never renders as a fake "0%".
/// </summary>
public class BluetoothDeviceDetailConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not BluetoothDeviceInfo device) return "";

        string label = device.Type switch
        {
            BluetoothDeviceType.Headphones => "Headphones",
            BluetoothDeviceType.Earbuds => "Earbuds",
            BluetoothDeviceType.Mouse => "Mouse",
            BluetoothDeviceType.Keyboard => "Keyboard",
            BluetoothDeviceType.Gamepad => "Gamepad",
            BluetoothDeviceType.Watch => "Watch",
            BluetoothDeviceType.Phone => "Phone",
            _ => "Device"
        };

        int? level = device.Battery?.DeviceLevel;
        return level.HasValue ? $"{label} · {level.Value}%" : label;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Device → "Connected" / "Available" / "Out of range".</summary>
public class BluetoothDeviceStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is BluetoothDeviceInfo device
            ? device.ConnectionState == BluetoothConnectionState.Connected
                ? "Connected"
                : device.IsPresent
                    ? "Available"
                    : "Out of range"
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Device → accent brush when connected, secondary brush otherwise.</summary>
public class BluetoothDeviceStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool connected = value is BluetoothDeviceInfo device
            && device.ConnectionState == BluetoothConnectionState.Connected;

        string key = connected ? "AccentBrush" : "TextSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(key, out var res) && res is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
