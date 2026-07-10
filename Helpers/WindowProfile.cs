namespace DynamicIsland.Helpers;

/// <summary>
/// Predefined window size profiles.
/// WindowService.SetProfile() is the only site that maps these to raw pixels.
/// No widget or ViewModel may call StartSizeAnimation() with raw dimensions.
/// </summary>
public enum WindowProfile
{
    /// <summary>220 × 40 — idle, no active widget.</summary>
    Collapsed,

    /// <summary>360 × 96 — standard expanded view for all current widgets.</summary>
    Expanded,
}

public static class WindowProfileExtensions
{
    /// <summary>Returns the logical DIP dimensions (width, height) for a profile.</summary>
    public static (int Width, int Height) ToDimensions(this WindowProfile profile) => profile switch
    {
        WindowProfile.Expanded => (360, 96),
        _                      => (220, 40),
    };
}
