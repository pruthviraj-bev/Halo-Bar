using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;
using DynamicIsland.Controls;

namespace DynamicIsland.Helpers;

/// <summary>
/// PASS 17.1: Meteocons weather icon resolver.
///
/// Replaces the Fluent weather glyphs with the Meteocons SVG icon set
/// (https://meteocons.com — MIT, Bas Milius; see Assets/Meteocons/LICENSE).
/// The WeatherService data layer is untouched: it still produces the existing
/// condition state (AppIconKind + Condition text), and THIS class is the only
/// place that decides which visual icon that state maps to.
///
/// - Day/night variants (clear-day/night, partly-cloudy-day/night, fog-day/night)
///   are chosen from local time — the only day/night signal available to the
///   existing system (the Open-Meteo response has no is_day flag in the
///   current request, and the pass forbids changing the API/data logic).
/// - SvgImageSource instances are cached per asset so weather updates never
///   re-decode or re-allocate images.
/// </summary>
public static class MeteoconWeatherIcons
{
    // AppIconKind (the existing condition state) → Meteocon asset (no ext).
    private static string ResolveKind(AppIconKind kind, bool isDay) => kind switch
    {
        AppIconKind.WeatherSunny => isDay ? "clear-day" : "clear-night",
        AppIconKind.WeatherPartlyCloudy => isDay ? "partly-cloudy-day" : "partly-cloudy-night",
        AppIconKind.WeatherCloudy => "cloudy",
        AppIconKind.WeatherRain => "rain",
        // Showers (WMO 80-82, downpour) → the Meteocons heavy-rain equivalent.
        AppIconKind.WeatherShowers => "extreme-rain",
        AppIconKind.WeatherSnow => "snow",
        AppIconKind.WeatherThunderstorm => "thunderstorms",
        AppIconKind.WeatherFog => isDay ? "fog-day" : "fog-night",
        AppIconKind.WeatherDrizzle => "drizzle",
        _ => "cloudy"
    };

    // Condition-text fallbacks for states the current WMO mapping does not
    // produce yet (sleet/wind/mist) — presentation-only, so the resolver
    // already supports the full pass icon list if detection ever extends.
    private static string ResolveCondition(string condition, bool isDay) => condition switch
    {
        "Sleet" => "sleet",
        "Wind" => "wind",
        "Mist" or "Haze" => isDay ? "fog-day" : "fog-night",
        _ => string.Empty
    };

    /// <summary>
    /// Per-asset zoom: the Meteocons artwork does NOT fill its 128×128 viewBox
    /// (rain/cloudy/snow art is ~80×58, clear-night only 63×63), so rendering
    /// the SVG at element size leaves the visible art surprisingly small. Each
    /// factor scales the artwork up to fill the icon box. Derived from each
    /// asset's actual path bounding box (artSize/128) so every condition
    /// renders at the same visual footprint.
    /// </summary>
    private static readonly Dictionary<string, double> _zoom = new()
    {
        { "clear-day", 1.33 },        // art 96/128
        { "clear-night", 1.9 },       // art 63/128
        { "cloudy", 1.6 },            // art 80/128
        { "drizzle", 1.6 },
        { "extreme-rain", 1.52 },     // art 84/128
        { "fog-day", 1.6 },           // art 64/128 → full height
        { "fog-night", 1.9 },
        { "partly-cloudy-day", 1.49 },// art 86/128
        { "partly-cloudy-night", 1.6 },
        { "rain", 1.6 },
        { "sleet", 1.6 },
        { "snow", 1.6 },
        { "thunderstorms", 1.6 },
        { "wind", 1.58 }
    };

    /// <summary>Uniform zoom for an asset so its artwork fills the icon box.</summary>
    public static double GetZoom(string asset)
        => _zoom.TryGetValue(asset, out double z) ? z : 1.6;

    /// <summary>Resolve the Meteocon asset name (no extension) for a state.</summary>
    public static string ResolveAsset(AppIconKind kind, string condition)
    {
        bool isDay = IsDayTime();
        string byCondition = ResolveCondition(condition, isDay);
        return byCondition.Length > 0 ? byCondition : ResolveKind(kind, isDay);
    }

    /// <summary>Local-time day heuristic — 06:00–17:59 is day.</summary>
    public static bool IsDayTime()
    {
        int hour = DateTime.Now.Hour;
        return hour >= 6 && hour < 18;
    }

    // Cached per-asset sources — never re-decoded on weather updates.
    private static readonly Dictionary<string, SvgImageSource> _cache = new();

    /// <summary>Get (and cache) the SvgImageSource for a weather state.</summary>
    public static SvgImageSource GetSource(AppIconKind kind, string condition)
    {
        string asset = ResolveAsset(kind, condition);
        if (!_cache.TryGetValue(asset, out var source))
        {
            source = new SvgImageSource(new Uri($"ms-appx:///Assets/Meteocons/{asset}.svg"));
            // SvgImageSource fails silently — surface any parse failure so a
            // broken asset is visible in the log instead of a missing icon.
            source.OpenFailed += (_, args) =>
                Logger.Error($"Meteocons: failed to load {asset}.svg — {args.Status}");
            _cache[asset] = source;
        }
        return source;
    }
}
