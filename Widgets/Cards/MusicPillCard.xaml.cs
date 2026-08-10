using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;

namespace DynamicIsland.Widgets.Cards;

public sealed partial class MusicPillCard : UserControl, IPillCard, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherTimer? _visualizerTimer;
    private bool _visualizerRunning;
    private int _tickCount;

    // ── IPillCard ────────────────────────────────────────────────────────────
    private bool _shouldShow;
    public bool ShouldShow
    {
        get => _shouldShow;
        private set
        {
            if (_shouldShow == value) return;
            _shouldShow = value;
            OnPropertyChanged();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double CardWidth { get; } = 320;
    public UserControl View => this;
    public event EventHandler? StateChanged;

    // ── INotifyPropertyChanged ───────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Bindable properties ──────────────────────────────────────────────────
    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        private set { if (_title == value) return; _title = value; OnPropertyChanged(); }
    }

    private string _artist = string.Empty;
    public string Artist
    {
        get => _artist;
        private set { if (_artist == value) return; _artist = value; OnPropertyChanged(); }
    }

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set { if (ReferenceEquals(_thumbnail, value)) return; _thumbnail = value; OnPropertyChanged(); }
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged();

            // The visualizer only runs while a track is playing. Previously the
            // timer ran forever at 8.3 Hz — allocating a brush and re-painting
            // 5 bars every tick even when the card was idle or hidden.
            if (value) StartVisualizer();
            else StopVisualizerAndPaintMuted();
        }
    }

    // ── Visualizer ───────────────────────────────────────────────────────────

    // Static idle brush — never re-allocated per tick.
    private static readonly SolidColorBrush MutedBarBrush = new(Windows.UI.Color.FromArgb(30, 255, 255, 255));

    // Thumbnail decode throttle: SMTC raises TimelinePropertiesChanged ~1 Hz
    // during playback and every event carries the SAME album art; decoding it
    // each time is needless UI-thread image work. Re-decode only when the track
    // changes or the previous decode is older than the cooldown.
    private string _lastThumbKey = string.Empty;
    private DateTime _lastThumbDecodeUtc = DateTime.MinValue;
    private const double ThumbDecodeCooldownSeconds = 10;
    private const int ThumbDecodePixelWidth = 160;

    // ── Construction ─────────────────────────────────────────────────────────
    public MusicPillCard()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ApplyState(App.MediaService.CurrentState);
        App.MediaService.MediaStateChanged += OnMediaStateChanged;
    }

    private void OnMediaStateChanged(object? sender, MediaState state)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            ApplyState(state);
            await LoadThumbnailAsync(state.Thumbnail);
        });
    }

    private void ApplyState(MediaState state)
    {
        ShouldShow = !string.IsNullOrWhiteSpace(state.Title);
        Title = string.IsNullOrWhiteSpace(state.Title) ? string.Empty : state.Title;
        Artist = state.Artist ?? string.Empty;
        IsPlaying = state.IsPlaying;
        // Thumbnail is loaded separately via LoadThumbnailAsync
    }

    private async System.Threading.Tasks.Task LoadThumbnailAsync(
        IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef == null)
        {
            _lastThumbKey = string.Empty;
            Thumbnail = null;
            return;
        }

        string key = $"{Title}|{Artist}";
        if (key == _lastThumbKey
            && (DateTime.UtcNow - _lastThumbDecodeUtc).TotalSeconds < ThumbDecodeCooldownSeconds)
        {
            return; // Same track, recently decoded — skip the redundant re-decode.
        }
        _lastThumbKey = key;
        _lastThumbDecodeUtc = DateTime.UtcNow;

        try
        {
            var stream = await thumbRef.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = ThumbDecodePixelWidth };
            using (stream)
                await bitmap.SetSourceAsync(stream);
            Thumbnail = bitmap;
        }
        catch
        {
            Thumbnail = null;
        }
    }

    // ── Visualizer ───────────────────────────────────────────────────────────
    private void StartVisualizer()
    {
        if (_visualizerRunning) return;
        _visualizerRunning = true;
        _visualizerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _visualizerTimer.Tick += OnVisualizerTick;
        _visualizerTimer.Start();
    }

    private void StopVisualizerAndPaintMuted()
    {
        _visualizerRunning = false;
        _visualizerTimer?.Stop();

        // Paint the idle state once on pause — previously re-applied every tick.
        if (WBar0 != null) WBar0.Background = MutedBarBrush;
        if (WBar1 != null) WBar1.Background = MutedBarBrush;
        if (WBar2 != null) WBar2.Background = MutedBarBrush;
        if (WBar3 != null) WBar3.Background = MutedBarBrush;
        if (WBar4 != null) WBar4.Background = MutedBarBrush;
    }

    private void OnVisualizerTick(object? sender, object e)
    {
        if (!IsPlaying)
        {
            StopVisualizerAndPaintMuted(); // safety — never animate while paused
            return;
        }

        _tickCount++;
        if (WBar0 != null) WBar0.Height = 3 + 8  * Math.Abs(Math.Sin(_tickCount * 0.40 + 0));
        if (WBar1 != null) WBar1.Height = 3 + 10 * Math.Abs(Math.Sin(_tickCount * 0.30 + 1));
        if (WBar2 != null) WBar2.Height = 3 + 6  * Math.Abs(Math.Sin(_tickCount * 0.50 + 2));
        if (WBar3 != null) WBar3.Height = 3 + 11 * Math.Abs(Math.Sin(_tickCount * 0.35 + 3));
        if (WBar4 != null) WBar4.Height = 3 + 8  * Math.Abs(Math.Sin(_tickCount * 0.45 + 4));

        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        if (WBar0 != null) WBar0.Background = accent;
        if (WBar1 != null) WBar1.Background = accent;
        if (WBar2 != null) WBar2.Background = accent;
        if (WBar3 != null) WBar3.Background = accent;
        if (WBar4 != null) WBar4.Background = accent;
    }

    // ── Playback controls ────────────────────────────────────────────────────
    private async void OnPreviousClick(object sender, RoutedEventArgs e)
        => await App.MediaService.SkipPreviousAsync();

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (IsPlaying)
            await App.MediaService.PauseAsync();
        else
            await App.MediaService.PlayAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
        => await App.MediaService.SkipNextAsync();
}
