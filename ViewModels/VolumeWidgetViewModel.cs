using CommunityToolkit.Mvvm.ComponentModel;
using DynamicIsland.Controls;
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
    public partial AppIconKind VolumeIconKind { get; set; } = AppIconKind.Speaker1;

    public VolumeWidgetViewModel(VolumeState state)
    {
        VolumePercent = state.VolumePercent;
        IsMuted = state.IsMuted;

        StatusText = IsMuted ? "Muted" : "Volume";
        VolumeIconKind = IsMuted
            ? AppIconKind.SpeakerMute
            : VolumePercent switch
            {
                <= 25 => AppIconKind.Speaker0,
                <= 70 => AppIconKind.Speaker1,
                _ => AppIconKind.Speaker2,
            };
    }

    public string VolumePercentText => $"{VolumePercent}%";
    public string VolumeDetailText => IsMuted ? "Sound output muted" : $"{VolumePercent}% output level";
}

