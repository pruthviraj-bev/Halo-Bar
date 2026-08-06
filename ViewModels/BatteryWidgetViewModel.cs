using CommunityToolkit.Mvvm.ComponentModel;
using DynamicIsland.Controls;
using DynamicIsland.Helpers;

namespace DynamicIsland.ViewModels;

/// <summary>
/// ViewModel for the Battery Widget.
/// Receives a BatteryState snapshot at construction; IslandController creates
/// a new widget instance per notification so there is no live update path here.
/// </summary>
public partial class BatteryWidgetViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int ChargePercent { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial AppIconKind BatteryIconKind { get; set; } = AppIconKind.Battery10;

    [ObservableProperty]
    public partial bool IsCharging { get; set; }

    [ObservableProperty]
    public partial bool IsCritical { get; set; }

    public BatteryWidgetViewModel(BatteryState state)
    {
        ChargePercent = state.ChargePercent;
        IsCritical    = state.IsCritical;
        IsCharging    = state.IsCharging;

        StatusText = state switch
        {
            { IsCritical: true } => "Critical Battery",
            { IsLow: true }      => "Low Battery",
            { IsCharging: true } => "Charging",
            _                    => "On Battery",
        };

        // Fluent System Icons: charging vs. battery level icons
        BatteryIconKind = state.IsCharging
            ? AppIconKind.BatteryCharge
            : state.ChargePercent switch
            {
                >= 80 => AppIconKind.Battery10,
                >= 60 => AppIconKind.Battery6,
                >= 40 => AppIconKind.Battery4,
                >= 20 => AppIconKind.Battery2,
                _     => AppIconKind.Battery0,
            };
    }

    /// <summary>Formatted percentage text for collapsed view.</summary>
    public string ChargePercentText => $"{ChargePercent}%";

    /// <summary>Formatted detail text for expanded view.</summary>
    public string ChargeDetailText => $"{ChargePercent}% charged";

    public Microsoft.UI.Xaml.Visibility IsChargingVisibility => IsCharging ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}
