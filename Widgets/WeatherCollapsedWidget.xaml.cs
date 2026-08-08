using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.Services;

namespace DynamicIsland.Widgets;

public sealed partial class WeatherCollapsedWidget : UserControl, IIslandWidget, INotifyPropertyChanged
{
    public WidgetPriority Priority => WidgetPriority.Default;
    public bool AutoExpand => false;
    public WindowProfile PreferredProfile => WindowProfile.Collapsed;

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnSuspended() { }
    public void OnResumed() { }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

    public WeatherCollapsedWidget()
    {
        InitializeComponent();
        Refresh(App.WeatherService);
        App.WeatherService.WeatherUpdated += OnWeatherUpdated;
    }

    private void OnWeatherUpdated(object? sender, EventArgs e) => Refresh(App.WeatherService);

    private void Refresh(WeatherService weather)
    {
        if (weather.IsWeatherAvailable)
        {
            TempText = weather.CurrentTemp;
            ConditionText = "  " + weather.Condition;
            IconKind = weather.IconKind;
        }
        else
        {
            TempText = "—";
            ConditionText = "  Unavailable";
            IconKind = AppIconKind.WeatherPartlyCloudy;
        }
    }
}
