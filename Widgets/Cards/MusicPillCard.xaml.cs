using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
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

    /// <summary>PASS 9: content area height, set by parent (PillDashboard)
    /// after layout so album art + controls scale to fill the pill instead of
    /// measuring at their tiny desired size inside a StackPanel.
    /// Album art = largest square that fits (content height − 2 DIP each side).</summary>
    public double ContentAreaHeight
    {
        set
        {
            if (ContentGrid != null) ContentGrid.Height = value;
            if (AlbumArt != null)
            {
                double sz = Math.Max(value - 2, 24);
                AlbumArt.Width = sz;
                AlbumArt.Height = sz;
            }
            if (AlbumFallback != null)
            {
                double fsz = Math.Max(Math.Min(value - 2, 16), 10);
                AlbumFallback.Width = fsz;
                AlbumFallback.Height = fsz;
            }
        }
    }
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

    // Static idle brush — never re-allocated per tick. ~25% white: clearly
    // visible against the capsule (the old 12% read as faint dashes), still
    // quiet compared to the Azure active state.
    private static readonly SolidColorBrush MutedBarBrush = new(Windows.UI.Color.FromArgb(64, 255, 255, 255));

    // Thumbnail decode throttle: SMTC raises TimelinePropertiesChanged ~1 Hz
    // during playback and every event carries the SAME album art; decoding it
    // each time is needless UI-thread image work. Re-decode only when the track
    // changes or the previous decode is older than the cooldown.
    private string _lastThumbKey = string.Empty;
    private DateTime _lastThumbDecodeUtc = DateTime.MinValue;
    private long _thumbGen; // bumped on every LoadThumbnailAsync — stale decodes are dropped
    private const double ThumbDecodeCooldownSeconds = 10;
    private const int ThumbDecodePixelWidth = 160;
    private const double ThumbDecodeMaxWaitMs = 5000;

    // ── Construction ─────────────────────────────────────────────────────────
    public MusicPillCard()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ApplyState(App.MediaService.CurrentState);
        App.MediaService.MediaStateChanged += OnMediaStateChanged;
        Loaded += OnLoadedOnce;
    }

    // PASS 9 (final): TEMPORARY runtime-bounds diagnostic — dumps the actual
    // rendered sizes once, ~500 ms after load (after layout settles), so the
    // pill geometry can be verified against the reference instead of guessed.
    // Output: %LOCALAPPDATA%\DynamicIsland\logs\app.log  (remove after verification)
    private bool _boundsLogged;
    private void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        if (_boundsLogged) return;
        _boundsLogged = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try
            {
                Logger.Info(
                    $"[PASS9-DEBUG] taskbar={App.WindowService.TaskbarHeightDips} " +
                    $"capsule={RootCapsule.ActualWidth:F0}x{RootCapsule.ActualHeight:F0} " +
                    $"content={ContentGrid.ActualWidth:F0}x{ContentGrid.ActualHeight:F0} " +
                    $"album={AlbumArt.ActualWidth:F0}x{AlbumArt.ActualHeight:F0} " +
                    $"title={TitleText.ActualWidth:F0}x{TitleText.ActualHeight:F0} " +
                    $"viz={VisualizerPanel.ActualWidth:F0}x{VisualizerPanel.ActualHeight:F0}");
            }
            catch (Exception ex)
            {
                Logger.Error("[PASS9-DEBUG] bounds dump failed", ex);
            }
        };
        t.Start();
    }

    private void OnMediaStateChanged(object? sender, MediaState state)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            ApplyState(state);
            await LoadThumbnailAsync(state.Thumbnail, state.SourceAppUserModelId, state.SourceName);
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
        IRandomAccessStreamReference? thumbRef, string sourceAumid, string sourceName)
    {
        // Generation guard: fast source switches start many async decodes; a slow
        // read for an OLD source must not overwrite the new source's art when it
        // finishes late. Only the latest generation may apply its result.
        long gen = ++_thumbGen;

        if (thumbRef == null)
        {
            _lastThumbKey = string.Empty;
            if (gen == _thumbGen) Thumbnail = null;
            return;
        }

        // Key includes the source AUMID so switching between two players on the
        // same track always re-decodes. Reserved only on success; blank art always
        // retries.
        string key = $"{sourceAumid}|{Title}|{Artist}";
        if (Thumbnail != null
            && key == _lastThumbKey
            && (DateTime.UtcNow - _lastThumbDecodeUtc).TotalSeconds < ThumbDecodeCooldownSeconds)
        {
            return; // Same track from the same source, recently decoded.
        }

        try
        {
            // Bound the stream-open wait: during a source switch the old session's
            // in-flight stream can hang; a bounded wait lets the new art win.
            var openTask = thumbRef.OpenReadAsync().AsTask();
            if (await Task.WhenAny(openTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(ThumbDecodeMaxWaitMs))) != openTask)
            {
                return;
            }
            var stream = await openTask;
            var bitmap = new BitmapImage { DecodePixelWidth = ThumbDecodePixelWidth };
            using (stream)
                await bitmap.SetSourceAsync(stream);
            // A newer decode has started — this result is stale.
            if (gen != _thumbGen) return;
            // Reserve the throttle key only on success so failed decodes stay retryable.
            _lastThumbKey = key;
            _lastThumbDecodeUtc = DateTime.UtcNow;
            Thumbnail = bitmap;
        }
        catch
        {
            // A newer decode is in flight — leave the current art alone.
            if (gen != _thumbGen) return;
            // Keep art that still matches the current track; only clear on a real change.
            if (_lastThumbKey != key)
            {
                Thumbnail = null;
            }
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
    }

    private void OnVisualizerTick(object? sender, object e)
    {
        if (!IsPlaying)
        {
            StopVisualizerAndPaintMuted(); // safety — never animate while paused
            return;
        }

        _tickCount++;
        // PASS 9 (shape): 4 bars (matches the reference), heights scaled for the
        // 24-DIP container so the bars are clearly visible, not faint dashes.
        if (WBar0 != null) WBar0.Height = 4 + 12 * Math.Abs(Math.Sin(_tickCount * 0.40 + 0));
        if (WBar1 != null) WBar1.Height = 4 + 16 * Math.Abs(Math.Sin(_tickCount * 0.30 + 1));
        if (WBar2 != null) WBar2.Height = 4 + 10 * Math.Abs(Math.Sin(_tickCount * 0.50 + 2));
        if (WBar3 != null) WBar3.Height = 4 + 18 * Math.Abs(Math.Sin(_tickCount * 0.35 + 3));

        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        if (WBar0 != null) WBar0.Background = accent;
        if (WBar1 != null) WBar1.Background = accent;
        if (WBar2 != null) WBar2.Background = accent;
        if (WBar3 != null) WBar3.Background = accent;
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
