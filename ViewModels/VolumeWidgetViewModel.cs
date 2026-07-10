using CommunityToolkit.Mvvm.ComponentModel;
using DynamicIsland.Helpers;

namespace DynamicIsland.ViewModels;

/// <summary>
/// ViewModel for the Volume widget.
/// Receives a snapshot state per notification.
/// </summary>
public partial class VolumeWidgetViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int VolumePercent { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string VolumeGlyph { get; set; } = "\uE994";

    public VolumeWidgetViewModel(VolumeState state)
    {
        VolumePercent = state.VolumePercent;
        IsMuted = state.IsMuted;

        StatusText = IsMuted ? "Muted" : "Volume";
        VolumeGlyph = IsMuted
            ? "\uE992"
            : VolumePercent switch
            {
                <= 25 => "\uE993",
                <= 70 => "\uE994",
                _ => "\uE995",
            };
    }

    public string VolumePercentText => $"{VolumePercent}%";
    public string VolumeDetailText => IsMuted ? "Sound output muted" : $"{VolumePercent}% output level";
}

