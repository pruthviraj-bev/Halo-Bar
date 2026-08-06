using DynamicIsland.Services;

namespace DynamicIsland.Helpers;

/// <summary>
/// Predefined window size profiles.
/// WindowService.SetProfile() is the only site that maps these to raw pixels.
/// No widget or ViewModel may call StartSizeAnimation() with raw dimensions.
///
/// Compact geometry is owned by CompactLayoutController (adaptive width within
/// design bounds). A compact→compact change never resizes the window — only
/// content updates; width adaptation is announced by the controller.
/// </summary>
public enum WindowProfile
{
    /// <summary>Compact pill — controller-owned adaptive width × live taskbar height. Content-only changes.</summary>
    Collapsed,

    /// <summary>800 × 664 — the expanded dashboard flyout.</summary>
    Expanded,
}

public static class WindowProfileExtensions
{
    /// <summary>Returns the logical DIP dimensions (width, height) for a profile.</summary>
    public static (int Width, int Height) ToDimensions(this WindowProfile profile) => profile switch
    {
        WindowProfile.Expanded => (800, 664),
        _                      => (CompactLayoutController.CompactIdealWidth, 40),
    };
}
