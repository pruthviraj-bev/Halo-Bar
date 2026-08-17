using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
            BluetoothDeviceType.Tv => "TV",
            BluetoothDeviceType.Printer => "Printer",
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

/// <summary>
/// PASS 7.2: Device → green (Semantic.State.Success) when connected,
/// secondary brush otherwise. Available/out-of-range stay neutral.
/// </summary>
public class BluetoothDeviceStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool connected = value is BluetoothDeviceInfo device
            && device.ConnectionState == BluetoothConnectionState.Connected;

        string key = connected ? "Semantic.State.Success" : "TextSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(key, out var res) && res is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// PASS 7: BluetoothDeviceType → device PNG asset (Assets/BluetoothItemIcons).
/// Returns null for types without a matching asset (Gamepad/Watch/Unknown) —
/// the device-row template keeps the Fluent glyph rendered underneath the Image, so the
/// glyph shows through whenever the asset is absent or fails to load.
///
/// Images are decoded once per type and cached: the converter instance is shared from
/// XAML resources and every device row rebinds the same snapshot on each refresh.
/// </summary>
public class BluetoothDeviceImageConverter : IValueConverter
{
    private static readonly Uri HeadphoneUri = new("ms-appx:///Assets/BluetoothItemIcons/headphone.png");
    private static readonly Uri MobileUri = new("ms-appx:///Assets/BluetoothItemIcons/mobile.png");
    private static readonly Uri MicrophoneUri = new("ms-appx:///Assets/BluetoothItemIcons/microphone.png");
    private static readonly Uri KeyboardUri = new("ms-appx:///Assets/BluetoothItemIcons/keyboard.png");
    private static readonly Uri MouseUri = new("ms-appx:///Assets/BluetoothItemIcons/mouse.png");
    private static readonly Uri TvUri = new("ms-appx:///Assets/BluetoothItemIcons/tv.png");
    private static readonly Uri PrinterUri = new("ms-appx:///Assets/BluetoothItemIcons/printer.png");
    private static readonly Uri WatchUri = new("ms-appx:///Assets/BluetoothItemIcons/watch.png");

    private static readonly Dictionary<BluetoothDeviceType, BitmapImage> Cache = new();

    private static BitmapImage? AssetFor(BluetoothDeviceType type)
    {
        Uri? uri = type switch
        {
            BluetoothDeviceType.Earbuds or BluetoothDeviceType.Headphones => HeadphoneUri,
            BluetoothDeviceType.Phone => MobileUri,
            BluetoothDeviceType.Keyboard => KeyboardUri,
            BluetoothDeviceType.Mouse => MouseUri,
            BluetoothDeviceType.Tv => TvUri,
            BluetoothDeviceType.Printer => PrinterUri,
            BluetoothDeviceType.Watch => WatchUri,
            BluetoothDeviceType.Other => MicrophoneUri,
            _ => null
        };
        if (uri == null) return null;

        if (!Cache.TryGetValue(type, out var image))
        {
            image = new BitmapImage(uri);
            Cache[type] = image;
        }
        return image;
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var type = value is BluetoothDeviceType t ? t : BluetoothDeviceType.Unknown;
        return (object?)AssetFor(type) ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();

    /// <summary>
    /// The PNG assets are drawn on canvases with lots of empty padding — the
    /// artwork occupies only a fraction of the canvas (e.g. headphone.png art
    /// is ~45% of the 500×500 box, mouse.png ~30%). An Image sized to the
    /// element box therefore renders the visible glyph much smaller than the
    /// box. The popup applies this zoom so the visible artwork fills the box
    /// edge-to-edge (the art then starts at the box's left edge — flush with
    /// the popup's left column, not drifting inward on the PNG's transparent
    /// padding). Factors = ~0.95 ÷ measured art coverage, per asset.
    /// 1.0 = no zoom (fallback glyph path).
    /// </summary>
    public static double ArtZoomFor(BluetoothDeviceType type)
        => type switch
        {
            // Each factor = ~0.95 ÷ the BINDING art fraction — the axis that
            // fills the 64 DIP box first (max of the art's width/height
            // coverage). A width-only zoom overflows tall art (phone 33%×63%:
            // a 2.9 zoom would make the art 117 DIP tall in the 64 box and it
            // clips top/bottom). Height-constrained factors keep every asset
            // fully inside the box.
            BluetoothDeviceType.Earbuds or BluetoothDeviceType.Headphones => 2.1,  // headphone.png art 45%×45%
            BluetoothDeviceType.Phone => 1.5,                                      // mobile.png art 33%×63% → height-bound
            BluetoothDeviceType.Keyboard => 1.6,                                   // keyboard.png art 60%×37% → width-bound
            BluetoothDeviceType.Mouse => 1.8,                                      // mouse.png art 30%×52% → height-bound
            BluetoothDeviceType.Tv => 1.6,                                         // tv.png art 59%×49% → width-bound
            BluetoothDeviceType.Printer => 2.0,                                    // printer.png art 43%×46%
            BluetoothDeviceType.Watch => 1.8,                                      // watch.png art 30%×52% → height-bound
            BluetoothDeviceType.Other => 1.6,                                      // microphone.png art 33%×60% → height-bound
            _ => 1.0
        };
}

/// <summary>
/// PASS 7: battery level (int?) → Fluent battery icon kind. Same thresholds as the
/// system-monitor battery readout. Null (no battery exposed by Windows) → Battery0;
/// the row itself is collapsed by NullToVisibilityConverter on Battery.DeviceLevel.
/// </summary>
public class BluetoothBatteryIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            int level when level > 90 => AppIconKind.Battery10,
            int level when level > 70 => AppIconKind.Battery9,
            int level when level > 50 => AppIconKind.Battery8,
            int level when level > 30 => AppIconKind.Battery7,
            _ => AppIconKind.Battery6
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>PASS 7: battery level (int?) → "45%" text. Null → "" (row is collapsed anyway).</summary>
public class BluetoothBatteryTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int level ? $"{level}%" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
