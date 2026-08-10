using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;

namespace DynamicIsland.ViewModels;

/// <summary>
/// ViewModel coordinating state and playback commands for the Media Widget.
/// </summary>
public partial class MediaWidgetViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty]
    public partial string Title { get; set; } = "No Track";

    [ObservableProperty]
    public partial string Artist { get; set; } = "Unknown Artist";

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial TimeSpan Position { get; set; }

    [ObservableProperty]
    public partial TimeSpan Duration { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset LastUpdatedTime { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    public partial bool IsRepeatActive { get; set; }

    [ObservableProperty]
    public partial string SourceName { get; set; } = "Unknown Source";

    [ObservableProperty]
    public partial bool HasMultipleSources { get; set; }

    // Thumbnail decode throttle: media events fire ~1 Hz during playback with the
    // SAME album art; re-decoding it on every event is needless UI-thread image
    // work. Re-decode only when the track changes or the previous decode is older
    // than the cooldown.
    private string _lastThumbKey = string.Empty;
    private DateTime _lastThumbDecodeUtc = DateTime.MinValue;
    private const double ThumbDecodeCooldownSeconds = 10;
    private const int ThumbDecodePixelWidth = 160;

    public MediaWidgetViewModel()
    {
        // Populate initial state immediately from the cached MediaService source of truth
        var state = App.MediaService.CurrentState;
        if (state != null)
        {
            Title = string.IsNullOrWhiteSpace(state.Title) ? "No Track" : state.Title;
            Artist = string.IsNullOrWhiteSpace(state.Artist) ? "Unknown Artist" : state.Artist;
            _ = LoadThumbnailAsync(state.Thumbnail);
            IsPlaying = state.IsPlaying;
            Position = state.Position;
            Duration = state.Duration;
            LastUpdatedTime = state.LastUpdatedTime;
            IsRepeatActive = state.AutoRepeatMode is not null and not MediaPlaybackAutoRepeatMode.None;
            SourceName = string.IsNullOrWhiteSpace(state.SourceName) ? "Unknown Source" : state.SourceName;
            HasMultipleSources = App.MediaService.HasMultipleSources;
        }

        // Subscribe to background system media state changes
        App.MediaService.MediaStateChanged += OnMediaStateChanged;
    }

    private void OnMediaStateChanged(object? sender, MediaState state)
    {
        // Dispatch UI updates back to the WinUI thread to avoid thread access crashes
        _dispatcherQueue.TryEnqueue(async () =>
        {
            Title = string.IsNullOrWhiteSpace(state.Title) ? "No Track" : state.Title;
            Artist = string.IsNullOrWhiteSpace(state.Artist) ? "Unknown Artist" : state.Artist;
            IsPlaying = state.IsPlaying;
            Position = state.Position;
            Duration = state.Duration;
            LastUpdatedTime = state.LastUpdatedTime;
            IsRepeatActive = state.AutoRepeatMode is not null and not MediaPlaybackAutoRepeatMode.None;
            SourceName = string.IsNullOrWhiteSpace(state.SourceName) ? "Unknown Source" : state.SourceName;
            HasMultipleSources = App.MediaService.HasMultipleSources;
            await LoadThumbnailAsync(state.Thumbnail);
        });
    }

    private async Task LoadThumbnailAsync(IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef == null)
        {
            Helpers.Logger.Info("[DEBUG] MediaWidgetViewModel: Thumbnail reference is null");
            _lastThumbKey = string.Empty;
            _dispatcherQueue.TryEnqueue(() =>
            {
                Thumbnail = null;
            });
            return;
        }

        // Same track, recently decoded — skip the redundant re-decode.
        string key = $"{Title}|{Artist}";
        if (key == _lastThumbKey
            && (DateTime.UtcNow - _lastThumbDecodeUtc).TotalSeconds < ThumbDecodeCooldownSeconds)
        {
            return;
        }
        _lastThumbKey = key;
        _lastThumbDecodeUtc = DateTime.UtcNow;

        try
        {
            Helpers.Logger.Info("[DEBUG] MediaWidgetViewModel: Opening thumbnail read stream...");
            var stream = await thumbRef.OpenReadAsync();
            Helpers.Logger.Info($"[DEBUG] MediaWidgetViewModel: Stream opened successfully. Size: {stream.Size} bytes");

            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using (stream)
                    {
                        var bitmap = new BitmapImage { DecodePixelWidth = ThumbDecodePixelWidth };
                        await bitmap.SetSourceAsync(stream);
                        Thumbnail = bitmap;
                        Helpers.Logger.Info("[DEBUG] MediaWidgetViewModel: Thumbnail BitmapImage created and set successfully");
                    }
                }
                catch (Exception ex)
                {
                    Helpers.Logger.Error("[DEBUG] MediaWidgetViewModel: Failed to decode stream on UI thread", ex);
                    Thumbnail = null;
                }
            });
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[DEBUG] MediaWidgetViewModel: Failed to load thumbnail stream in view-model", ex);
            _dispatcherQueue.TryEnqueue(() =>
            {
                Thumbnail = null;
            });
        }
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (IsPlaying)
        {
            await App.MediaService.PauseAsync();
        }
        else
        {
            await App.MediaService.PlayAsync();
        }
    }

    [RelayCommand]
    private async Task Previous()
    {
        await App.MediaService.SkipPreviousAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        await App.MediaService.SkipNextAsync();
    }

    [RelayCommand]
    private Task Seek(TimeSpan position)
    {
        return App.MediaService.SeekAsync(position);
    }

    [RelayCommand]
    private Task ToggleRepeat()
    {
        return App.MediaService.ToggleRepeatAsync();
    }

    [RelayCommand]
    private void PreviousSource()
    {
        App.MediaService.SelectPreviousSource();
    }

    [RelayCommand]
    private void NextSource()
    {
        App.MediaService.SelectNextSource();
    }
}
