using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using DynamicIsland.Services;

namespace DynamicIsland.Helpers;

/// <summary>
/// PASS 6 (V1 REDESIGN): display converters for the redesigned clipboard rows.
/// The ClipboardItem data model is frozen — these derive the new visual hierarchy
/// (type label / content preview / relative timestamp) from the existing fields.
/// </summary>

/// <summary>
/// Primary type label for a row: "TEXT" / "FILES", or the file extension
/// ("JPEG", "PNG", …) for image copies.
/// </summary>
public class ClipboardTypeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is ClipboardItem item)
        {
            switch (item.Type)
            {
                case ClipboardItemType.Text:
                    return "TEXT";
                case ClipboardItemType.Files:
                    return "FILES";
                case ClipboardItemType.Image:
                    if (!string.IsNullOrEmpty(item.ImageFilePath))
                    {
                        var ext = System.IO.Path.GetExtension(item.ImageFilePath).TrimStart('.');
                        return string.IsNullOrEmpty(ext) ? "IMAGE" : ext.ToUpperInvariant();
                    }
                    return "IMAGE";
            }
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// Secondary content preview: the copied text (whitespace-normalized, capped) for
/// text rows, the file-name summary for file rows, empty for image rows (the image
/// preview occupies that slot instead).
/// </summary>
public class ClipboardPreviewConverter : IValueConverter
{
    private const int MaxPreviewLength = 120;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is ClipboardItem item)
        {
            switch (item.Type)
            {
                case ClipboardItemType.Text when !string.IsNullOrWhiteSpace(item.RawText):
                {
                    var preview = Regex.Replace(item.RawText, @"\s+", " ").Trim();
                    return preview.Length > MaxPreviewLength ? preview[..MaxPreviewLength] + "…" : preview;
                }
                case ClipboardItemType.Files:
                    return item.Detail;
            }
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// Tertiary timestamp in relative terms ("just now", "3 min ago", "2 hours ago",
/// "3 days ago", "2 weeks ago", then a short date).
/// </summary>
public class ClipboardTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset ts) return "";

        var diff = DateTimeOffset.Now - ts;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60)
        {
            int m = (int)diff.TotalMinutes;
            return m == 1 ? "1 min ago" : $"{m} min ago";
        }
        if (diff.TotalHours < 24)
        {
            int h = (int)diff.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }
        if (diff.TotalDays < 7)
        {
            int d = (int)diff.TotalDays;
            return d == 1 ? "yesterday" : $"{d} days ago";
        }
        if (diff.TotalDays < 30)
        {
            int w = (int)(diff.TotalDays / 7);
            return w == 1 ? "1 week ago" : $"{w} weeks ago";
        }
        return ts.ToString("MMM d");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// Row visibility by item type. ConverterParameter:
///   "Tile"    → the 64 DIP thumbnail/icon tile (hidden for text rows — the type
///               label is the text row's visual anchor);
///   anything else → the content-preview line (hidden for image rows — the image
///               preview occupies that slot).
/// </summary>
public class ClipboardItemVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var type = value switch
        {
            ClipboardItem item => item.Type,
            ClipboardItemType t => t,
            _ => ClipboardItemType.Text
        };

        if (parameter as string == "Tile")
        {
            return type == ClipboardItemType.Text ? Visibility.Collapsed : Visibility.Visible;
        }
        return type == ClipboardItemType.Image ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}
