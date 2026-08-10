using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;

namespace DynamicIsland.Widgets.Cards;

public sealed partial class MusicPillCard : UserControl, IPillCard, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherTimer? _visualizerTimer;
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
        private set { _title = value; OnPropertyChanged(); }
    }

    private string _artist = string.Empty;
    public string Artist
    {
        get => _artist;
        private set { _artist = value; OnPropertyChanged(); }
    }

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; OnPropertyChanged(); }
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set { _isPlaying = value; OnPropertyChanged(); }
    }

    // ── Construction ─────────────────────────────────────────────────────────
    public MusicPillCard()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ApplyState(App.MediaService.CurrentState);
        App.MediaService.MediaStateChanged += OnMediaStateChanged;
        StartVisualizer();
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
            Thumbnail = null;
            return;
        }
        try
        {
            var stream = await thumbRef.OpenReadAsync();
            var bitmap = new BitmapImage();
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
        _visualizerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _visualizerTimer.Tick += OnVisualizerTick;
        _visualizerTimer.Start();
    }

    private void OnVisualizerTick(object? sender, object e)
    {
        if (IsPlaying)
        {
            _tickCount++;
            if (WBar0 != null) WBar0.Height = 3 + 8  * Math.Abs(Math.Sin(_tickCount * 0.40 + 0));
            if (WBar1 != null) WBar1.Height = 3 + 10 * Math.Abs(Math.Sin(_tickCount * 0.30 + 1));
            if (WBar2 != null) WBar2.Height = 3 + 6  * Math.Abs(Math.Sin(_tickCount * 0.50 + 2));
            if (WBar3 != null) WBar3.Height = 3 + 11 * Math.Abs(Math.Sin(_tickCount * 0.35 + 3));
            if (WBar4 != null) WBar4.Height = 3 + 8  * Math.Abs(Math.Sin(_tickCount * 0.45 + 4));
        }

        var accent = (Microsoft.UI.Xaml.Media.Brush)
            Application.Current.Resources["AccentBrush"];
        var muted = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(30, 255, 255, 255));

        if (WBar0 != null) WBar0.Background = IsPlaying ? accent : muted;
        if (WBar1 != null) WBar1.Background = IsPlaying ? accent : muted;
        if (WBar2 != null) WBar2.Background = IsPlaying ? accent : muted;
        if (WBar3 != null) WBar3.Background = IsPlaying ? accent : muted;
        if (WBar4 != null) WBar4.Background = IsPlaying ? accent : muted;
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