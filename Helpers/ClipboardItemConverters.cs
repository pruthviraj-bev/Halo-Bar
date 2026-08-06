using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using DynamicIsland.Controls;
using DynamicIsland.Services;

namespace DynamicIsland.Helpers;

/// <summary>
/// Converts a saved clipboard image file path into an ImageSource. Returns null for
/// empty or missing files so the UI can fall back to a type glyph.
/// </summary>
public class ClipboardImagePathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null!;
        }

        try
        {
            return new BitmapImage(new Uri(path));
        }
        catch
        {
            return null!;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// Returns the application icon for a ClipboardItemType (Text/Image/Files).
/// </summary>
public class ClipboardTypeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            ClipboardItemType.Text => AppIconKind.Document,
            ClipboardItemType.Image => AppIconKind.Image,
            ClipboardItemType.Files => AppIconKind.Folder,
            _ => AppIconKind.Clipboard,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// Returns the Pin icon kind; the Filled variant is driven separately by the
/// bool IsPinned state through the AppIcon.Filled property.
/// </summary>
public class IsPinnedToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return AppIconKind.Pin;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// Returns the accent brush when pinned, secondary text brush otherwise.
/// </summary>
public class IsPinnedToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is true ? "AccentBrush" : "TextSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(key, out var res) && res is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}
