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

    /// <summary>Expanded dashboard — 620×640 surface + 3 DIP edge clearance (see HaloGeometry).</summary>
    Expanded,
}

public static class WindowProfileExtensions
{
    /// <summary>Returns the logical DIP dimensions (width, height) for a profile.</summary>
    public static (int Width, int Height) ToDimensions(this WindowProfile profile) => profile switch
    {
        // PASS 1 (V1 REDESIGN): the expanded envelope = the 620×640 dashboard
        // surface + 3 DIP edge clearance (left + bottom). The taskbar strip is
        // added by WindowService at init; the compact pill is unchanged.
        WindowProfile.Expanded => ((int)HaloGeometry.ExpandedEnvelopeWidthDip, (int)HaloGeometry.ExpandedEnvelopeHeightDip),
        _                      => (CompactLayoutController.CompactIdealWidth, 40),
    };
}
