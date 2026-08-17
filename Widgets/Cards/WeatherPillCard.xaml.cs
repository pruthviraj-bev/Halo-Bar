using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.Services;

namespace DynamicIsland.Widgets.Cards;

public sealed partial class WeatherPillCard : UserControl, IPillCard, INotifyPropertyChanged
{
    // ── IPillCard ────────────────────────────────────────────────────────────
    public bool ShouldShow { get; } = true;
    public double CardWidth { get; } = 170;
    public UserControl View => this;
    public event EventHandler? StateChanged;

    // ── INotifyPropertyChanged ───────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Bindable properties ──────────────────────────────────────────────────
    private string _tempText = "—";
    public string TempText
    {
        get => _tempText;
        private set { _tempText = value; OnPropertyChanged(); }
    }

    private string _conditionText = "Unknown";
    public string ConditionText
    {
        get => _conditionText;
        private set { _conditionText = value; OnPropertyChanged(); }
    }

    // ── Construction ─────────────────────────────────────────────────────────
    public WeatherPillCard()
    {
        InitializeComponent();
        Refresh(App.WeatherService);
        App.WeatherService.WeatherUpdated += OnWeatherUpdated;
    }

    private void OnWeatherUpdated(object? sender, EventArgs e)
    {
        // THREADING RULE: WeatherCollapsedWidget.OnWeatherUpdated calls Refresh
        // directly (no TryEnqueue) — phase 1 confirmed. Replicate exactly.
        Refresh(App.WeatherService);
    }

    private void Refresh(WeatherService service)
    {
        if (service.IsWeatherAvailable)
        {
            TempText = service.CurrentTemp;
            ConditionText = service.Condition;
            ApplyWeatherIcon(service.IconKind, service.Condition);
        }
        else
        {
            TempText = "—";
            ConditionText = "Unavailable";
            ApplyWeatherIcon(Controls.AppIconKind.WeatherPartlyCloudy, string.Empty);
        }

        // Content changed (new temp/condition) — the pill strip may need to
        // re-layout. Harmless if no subscriber (WeatherCard isn't subscribed
        // today), keeps the IPillCard contract honest for future consumers.
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── PASS 17.1: Meteocon icon + subtle per-condition animation ───────────
    private Storyboard? _activeAnimation;

    private void ApplyWeatherIcon(Controls.AppIconKind kind, string condition)
    {
        // Cached per-asset SvgImageSource — never re-decoded on updates.
        WeatherIconImage.Source = MeteoconWeatherIcons.GetSource(kind, condition);

        // Stop the previous animation; reset opacity so a new condition starts
        // at full brightness (Stop() alone leaves the animated value in place).
        _activeAnimation?.Stop();
        _activeAnimation = null;
        WeatherIconImage.Opacity = 1.0;

        string asset = MeteoconWeatherIcons.ResolveAsset(kind, condition);

        // Zoom the artwork to fill the icon box (per-asset factor — the
        // Meteocons art doesn't fill its viewBox, so without this the visible
        // icon is much smaller than the 34×34 element).
        double zoom = MeteoconWeatherIcons.GetZoom(asset);
        WeatherIconZoom.ScaleX = zoom;
        WeatherIconZoom.ScaleY = zoom;

        string? animKey = asset switch
        {
            "clear-day" => "SunAnim",                       // subtle glow
            "rain" or "drizzle" or "extreme-rain" => "RainAnim",
            "snow" or "sleet" => "SnowAnim",
            "cloudy" or "partly-cloudy-day" or "partly-cloudy-night" => "CloudAnim",
            "thunderstorms" => "StormAnim",                 // occasional flicker
            "fog-day" or "fog-night" => "FogAnim",
            _ => null                                       // clear-night, wind: still
        };

        if (animKey != null && Resources[animKey] is Storyboard storyboard)
        {
            _activeAnimation = storyboard;
            storyboard.Begin();
        }
    }
}
