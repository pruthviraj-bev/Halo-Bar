using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DynamicIsland.Helpers;

/// <summary>
/// Applies the user's accent color to every accent surface at runtime.
/// The app's accent is exposed through three resource families:
///   • AccentColor (a <see cref="Windows.UI.Color"/>) + AccentBrush (SolidColorBrush)
///   • Semantic.Accent.Primary / Hover / Pressed (SolidColorBrush, Tokens.xaml)
/// Since every consumer resolves the SAME SolidColorBrush instance from
/// Application.Resources, mutating that instance's Color repaints all live
/// {ThemeResource}/{StaticResource} references immediately — no theme swap,
/// no restart, no per-consumer notification.
/// </summary>
public static class AccentManager
{
    /// <summary>
    /// Applies the given #AARRGGBB hex accent to AccentColor, AccentBrush and the
    /// Semantic.Accent primary/hover/pressed brushes. Hover/pressed are derived by
    /// lightening/darkening the chosen color, matching the original Azure family's
    /// ratio. Never throws.
    /// </summary>
    public static void Apply(string hex)
    {
        try
        {
            var color = ParseHex(hex);

            if (Application.Current.Resources.TryGetValue("AccentColor", out var colorRes) && colorRes is Color)
                Application.Current.Resources["AccentColor"] = color;

            if (Application.Current.Resources.TryGetValue("AccentBrush", out var brushRes) && brushRes is SolidColorBrush accentBrush)
                accentBrush.Color = color;

            if (Application.Current.Resources.TryGetValue("Semantic.Accent.Primary", out var primaryRes) && primaryRes is SolidColorBrush primary)
                primary.Color = color;

            if (Application.Current.Resources.TryGetValue("Semantic.Accent.Hover", out var hoverRes) && hoverRes is SolidColorBrush hover)
                hover.Color = Lighten(color, 0.18f);

            if (Application.Current.Resources.TryGetValue("Semantic.Accent.Pressed", out var pressedRes) && pressedRes is SolidColorBrush pressed)
                pressed.Color = Darken(color, 0.12f);
        }
        catch (Exception ex)
        {
            Logger.Error("AccentManager: failed to apply accent", ex);
        }
    }

    /// <summary>Parses "#AARRGGBB" or "#RRGGBB" into a Windows.UI.Color.</summary>
    public static Color ParseHex(string hex)
    {
        string s = hex.TrimStart('#');
        byte a = 255;
        if (s.Length == 8)
        {
            a = Convert.ToByte(s.Substring(0, 2), 16);
            s = s.Substring(2);
        }
        return Color.FromArgb(
            a,
            Convert.ToByte(s.Substring(0, 2), 16),
            Convert.ToByte(s.Substring(2, 2), 16),
            Convert.ToByte(s.Substring(4, 2), 16));
    }

    /// <summary>Blends a color toward white by <paramref name="amount"/> (0..1).</summary>
    public static Color Lighten(Color c, float amount)
    {
        return Color.FromArgb(c.A,
            Blend(c.R, 255, amount),
            Blend(c.G, 255, amount),
            Blend(c.B, 255, amount));
    }

    /// <summary>Blends a color toward black by <paramref name="amount"/> (0..1).</summary>
    public static Color Darken(Color c, float amount)
    {
        return Color.FromArgb(c.A,
            Blend(c.R, 0, amount),
            Blend(c.G, 0, amount),
            Blend(c.B, 0, amount));
    }

    private static byte Blend(byte from, byte to, float amount)
        => (byte)Math.Clamp(Math.Round(from + (to - from) * amount), 0, 255);
}
