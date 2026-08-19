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
    private long _thumbGen; // bumped on every LoadThumbnailAsync — stale decodes are dropped
    private const double ThumbDecodeCooldownSeconds = 10;
    private const int ThumbDecodePixelWidth = 160;
    private const double ThumbDecodeMaxWaitMs = 5000;

    public MediaWidgetViewModel()
    {
        // Populate initial state immediately from the cached MediaService source of truth
        var state = App.MediaService.CurrentState;
        if (state != null)
        {
            Title = string.IsNullOrWhiteSpace(state.Title) ? "No Track" : state.Title;
            Artist = string.IsNullOrWhiteSpace(state.Artist) ? "Unknown Artist" : state.Artist;
            _ = LoadThumbnailAsync(state.Thumbnail, state.SourceAppUserModelId, state.SourceName);
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
            await LoadThumbnailAsync(state.Thumbnail, state.SourceAppUserModelId, state.SourceName);
        });
    }

    private async Task LoadThumbnailAsync(IRandomAccessStreamReference? thumbRef, string sourceAumid, string sourceName)
    {
        // Generation guard: fast source switches fire many state updates, each
        // starting an async decode. Without this, a SLOW read for an OLD source
        // can finish AFTER a fast new source's art and overwrite it — the
        // "thumbnail doesn't appear after rapid switching" bug. Each call bumps
        // the generation; a decode only applies when its generation is still the
        // latest when it completes.
        long gen = ++_thumbGen;

        if (thumbRef == null)
        {
            Helpers.Logger.Info("[DEBUG] MediaWidgetViewModel: Thumbnail reference is null");
            _lastThumbKey = string.Empty;
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _thumbGen) return;
                Thumbnail = null;
            });
            return;
        }

        // Same track from the SAME source, recently decoded — skip the redundant
        // re-decode. The key includes the source AUMID so switching between two
        // players playing the same track always re-decodes (their art can differ,
        // e.g. Spotify vs YouTube Music). The key is only reserved AFTER a
        // successful decode, so a failed or stale stream never poisons the
        // throttle and every subsequent media event gets a fresh retry chance.
        // When the thumbnail is currently blank we always retry, never skip.
        string key = $"{sourceAumid}|{Title}|{Artist}";
        if (Thumbnail != null
            && key == _lastThumbKey
            && (DateTime.UtcNow - _lastThumbDecodeUtc).TotalSeconds < ThumbDecodeCooldownSeconds)
        {
            return;
        }

        try
        {
            Helpers.Logger.Info("[DEBUG] MediaWidgetViewModel: Opening thumbnail read stream...");
            // Bound the stream-open wait. During a source switch the old session's
            // in-flight stream can hang; a bounded wait lets the new session's art
            // win instead of being blocked behind a stale read.
            var openTask = thumbRef.OpenReadAsync().AsTask();
            if (await Task.WhenAny(openTask, Task.Delay(TimeSpan.FromMilliseconds(ThumbDecodeMaxWaitMs))) != openTask)
            {
                Helpers.Logger.Info("[DEBUG] MediaWidgetViewModel: Thumbnail stream open timed out");
                return;
            }
            var stream = await openTask;
            Helpers.Logger.Info($"[DEBUG] MediaWidgetViewModel: Stream opened successfully. Size: {stream.Size} bytes");

            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using (stream)
                    {
                        var bitmap = new BitmapImage { DecodePixelWidth = ThumbDecodePixelWidth };
                        await bitmap.SetSourceAsync(stream);
                        // A newer decode has already started — this result is stale.
                        if (gen != _thumbGen) return;
                        // Only reserve the throttle key on success, so a failed
                        // decode stays retryable instead of blocking for 10s.
                        _lastThumbKey = key;
                        _lastThumbDecodeUtc = DateTime.UtcNow;
                        Thumbnail = bitmap;
                        Helpers.Logger.Info("[DEBUG] MediaWidgetViewModel: Thumbnail BitmapImage created and set successfully");
                    }
                }
                catch (Exception ex)
                {
                    Helpers.Logger.Error("[DEBUG] MediaWidgetViewModel: Failed to decode stream on UI thread", ex);
                    // A newer decode is already in flight — leave the current art alone.
                    if (gen != _thumbGen) return;
                    // Keep any thumbnail that is still correct for the current
                    // track; only clear when the art genuinely changed. A source
                    // switch can transiently fail to decode while the old art is
                    // still the right thing to show.
                    if (_lastThumbKey != key)
                    {
                        Thumbnail = null;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[DEBUG] MediaWidgetViewModel: Failed to load thumbnail stream in view-model", ex);
            // Same rule: don't blank art that still matches the current track.
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (gen == _thumbGen && _lastThumbKey != key)
                {
                    Thumbnail = null;
                }
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
