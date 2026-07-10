using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>
/// Service responsible for monitoring and interacting with the system clipboard.
/// Exposes a cached CurrentItem and fires ClipboardChanged when new content is detected.
/// Duplicate-suppression prevents re-firing for the same copied data.
/// </summary>
public class ClipboardService
{
    public ClipboardItem? CurrentItem { get; private set; }

    public event EventHandler<ClipboardItem?>? ClipboardChanged;

    public void Initialize()
    {
        try
        {
            Clipboard.ContentChanged += OnContentChanged;
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to subscribe to ContentChanged", ex);
        }
    }

    private void OnContentChanged(object? sender, object e)
    {
        _ = QueryAsync();
    }

    private async Task QueryAsync()
    {
        try
        {
            var data = Clipboard.GetContent();
            if (data == null) return;

            ClipboardItem? item = null;

            if (data.Contains(StandardDataFormats.Text))
            {
                string text = await data.GetTextAsync();
                if (string.IsNullOrEmpty(text)) return;

                // Suppress duplicate text copies
                if (CurrentItem?.Type == ClipboardItemType.Text && CurrentItem.RawText == text) return;

                item = new ClipboardItem
                {
                    Type = ClipboardItemType.Text,
                    RawText = text,
                    Title = text.Length > 40 ? text[..40].TrimEnd() + "…" : text,
                    Detail = $"{text.Length} characters"
                };
            }
            else if (data.Contains(StandardDataFormats.StorageItems))
            {
                var files = await data.GetStorageItemsAsync();
                if (files == null || files.Count == 0) return;

                string names = string.Join(", ", files.Select(f => f.Name));
                if (CurrentItem?.Type == ClipboardItemType.Files && CurrentItem.Detail == names) return;

                string title = files.Count == 1 ? files[0].Name : $"{files.Count} files";
                item = new ClipboardItem
                {
                    Type = ClipboardItemType.Files,
                    Title = title,
                    Detail = names
                };
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                var bmpRef = await data.GetBitmapAsync();
                if (bmpRef == null) return;

                item = new ClipboardItem
                {
                    Type = ClipboardItemType.Image,
                    Title = "Image",
                    Detail = "Bitmap image",
                    ImageStreamRef = bmpRef
                };
            }

            if (item != null)
            {
                CurrentItem = item;
                ClipboardChanged?.Invoke(this, item);
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: error reading clipboard", ex);
        }
    }

    public void Clear()
    {
        try
        {
            Clipboard.Clear();
            CurrentItem = null;
            ClipboardChanged?.Invoke(this, null);
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to clear clipboard", ex);
        }
    }

    public void ReCopy(ClipboardItem item)
    {
        if (item.Type != ClipboardItemType.Text || string.IsNullOrEmpty(item.RawText)) return;
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(item.RawText);
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to re-copy item", ex);
        }
    }

    public void Dispose()
    {
        Clipboard.ContentChanged -= OnContentChanged;
    }
}

public enum ClipboardItemType { Text, Image, Files }

/// <summary>
/// Immutable-style model representing a captured clipboard snapshot.
/// </summary>
public class ClipboardItem
{
    public ClipboardItemType Type { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? RawText { get; set; }
    public IRandomAccessStreamReference? ImageStreamRef { get; set; }
}
