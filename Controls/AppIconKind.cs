namespace DynamicIsland.Controls;

/// <summary>
/// Logical icon names used across the whole application.
///
/// Pages reference these names (never vendor-specific Fluent icon names) so the
/// underlying icon library can be swapped by editing <see cref="AppIcons"/> only.
/// </summary>
public enum AppIconKind
{
    /// <summary>Unset / renders nothing.</summary>
    None,

    Home,
    Add,
    Target,
    Play,
    Pause,
    Reset,
    Settings,
    Dismiss,
    Save,
    Checkmark,
    MusicNote,
    Music,
    ChevronDown,
    ChevronLeft,
    ChevronRight,
    Previous,
    Next,
    Repeat,
    SpeakerMute,
    Speaker0,
    Speaker1,
    Speaker2,
    Delete,
    More,
    Pin,
    Document,
    Folder,
    Image,
    Clipboard,
    Copy,
    Link,
    OpenInNew,
    DragOut,

    Battery0,
    Battery1,
    Battery2,
    Battery3,
    Battery4,
    Battery5,
    Battery6,
    Battery7,
    Battery8,
    Battery9,
    Battery10,
    BatteryCharge,
    Flash,

    WeatherSunny,
    WeatherPartlyCloudy,
    WeatherFog,
    WeatherDrizzle,
    WeatherRain,
    WeatherSnow,
    WeatherShowers,
    WeatherThunderstorm,
    WeatherCloudy,

    Bluetooth,
    Headphone,
    Keyboard,
    Mouse,

    // PASS 8: footer system-monitor metrics
    Cpu,
    Ram,
    Disk,

    // PASS 20: footer live network throughput
    Download,
    Upload,
}
