using CommunityToolkit.Mvvm.ComponentModel;
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
    public partial string BatteryGlyph { get; set; } = "\uE83F";

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

        // Segoe MDL2 Assets: battery charging vs. level icons
        BatteryGlyph = state.IsCharging
            ? "\uE85A"                        // BatteryCharging9
            : state.ChargePercent switch
            {
                >= 80 => "\uE83F",            // Battery10 (full)
                >= 60 => "\uEBA9",            // Battery6
                >= 40 => "\uEBA8",            // Battery4
                >= 20 => "\uEBA7",            // Battery2
                _     => "\uEBA6",            // Battery0 / critical
            };
    }

    /// <summary>Formatted percentage text for collapsed view.</summary>
    public string ChargePercentText => $"{ChargePercent}%";

    /// <summary>Formatted detail text for expanded view.</summary>
    public string ChargeDetailText => $"{ChargePercent}% charged";

    public Microsoft.UI.Xaml.Visibility IsChargingVisibility => IsCharging ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}
