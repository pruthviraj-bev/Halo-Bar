using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Controls;
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

    private string _conditionText = "  Unknown";
    public string ConditionText
    {
        get => _conditionText;
        private set { _conditionText = value; OnPropertyChanged(); }
    }

    private AppIconKind _iconKind = AppIconKind.WeatherPartlyCloudy;
    public AppIconKind IconKind
    {
        get => _iconKind;
        private set { _iconKind = value; OnPropertyChanged(); }
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
            ConditionText = "  " + service.Condition;
            IconKind = service.IconKind;
        }
        else
        {
            TempText = "—";
            ConditionText = "  Unavailable";
            IconKind = AppIconKind.WeatherPartlyCloudy;
        }
    }
}