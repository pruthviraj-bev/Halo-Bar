using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicIsland.Controls;
using DynamicIsland.Services;

namespace DynamicIsland.ViewModels;

/// <summary>
/// ViewModel for the Clipboard Widget.
/// Exposes display properties and action commands.
/// Dismissal is handled by calling IslandController.DismissClipboard() —
/// the ViewModel does not manage its own lifecycle.
/// </summary>
public partial class ClipboardWidgetViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial AppIconKind TypeIconKind { get; set; } = AppIconKind.Clipboard;

    [ObservableProperty]
    public partial BitmapImage? ImagePreview { get; set; }

    [ObservableProperty]
    public partial string RawContent { get; set; } = "";

    public ClipboardWidgetViewModel(ClipboardItem item)
    {
        _ = ApplyItemAsync(item);
    }

    private async Task ApplyItemAsync(ClipboardItem item)
    {
        Title = item.Title;
        Detail = item.Detail;
        RawContent = item.RawText ?? item.Detail;

        switch (item.Type)
        {
            case ClipboardItemType.Text:
                TypeIconKind = AppIconKind.Document;
                ImagePreview = null;
                break;

            case ClipboardItemType.Files:
                TypeIconKind = AppIconKind.Folder;
                ImagePreview = null;
                break;

            case ClipboardItemType.Image:
                TypeIconKind = AppIconKind.Image;
                if (item.ImageStreamRef != null)
                {
                    try
                    {
                        var bitmap = new BitmapImage { DecodePixelWidth = 256 };
                        using var stream = await item.ImageStreamRef.OpenReadAsync();
                        await bitmap.SetSourceAsync(stream);
                        ImagePreview = bitmap;
                    }
                    catch (Exception ex)
                    {
                        Helpers.Logger.Error("ClipboardWidgetViewModel: failed to load image preview", ex);
                        ImagePreview = null;
                    }
                }
                break;
        }
    }

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void ReCopy()
    {
        if (App.ClipboardService.CurrentItem != null)
            App.ClipboardService.ReCopy(App.ClipboardService.CurrentItem);
        App.IslandController.ShowCopiedFeedback();
    }

    [RelayCommand]
    private void Delete()
    {
        if (App.ClipboardService.CurrentItem != null)
            App.ClipboardService.RemoveFromHistory(App.ClipboardService.CurrentItem);
        App.IslandController.DismissClipboard();
    }

    [RelayCommand]
    private void Dismiss()
    {
        App.IslandController.DismissClipboard();
    }
}
