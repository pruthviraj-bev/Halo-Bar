using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DynamicIsland.Helpers;

/// <summary>One tappable geocoding hit.</summary>
public record CityResult(string DisplayName, double Latitude, double Longitude);

/// <summary>
/// Open-Meteo geocoding search used by the weather location UI (PASS 21).
/// Extracted from the retired LocationSettingsPopup so the Settings page can
/// offer the same manual city override. Never throws; callers handle failures.
/// </summary>
public static class CityGeocoder
{
    /// <summary>
    /// Searches Open-Meteo for up to 5 cities matching <paramref name="query"/>.
    /// Throws when the request fails (the caller decides how to surface it).
    /// </summary>
    public static async Task<List<CityResult>> SearchAsync(string query)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=5&language=en&format=json";
        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("geocoding search failed");
        }

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var resultsEl))
        {
            return new List<CityResult>();
        }

        var list = new List<CityResult>();
        foreach (var item in resultsEl.EnumerateArray())
        {
            if (!item.TryGetProperty("latitude", out var latEl) || !item.TryGetProperty("longitude", out var lonEl))
            {
                continue;
            }

            string name = GetString(item, "name");
            string admin1 = GetString(item, "admin1");
            string country = GetString(item, "country");
            string display = string.Join(", ", new[] { name, admin1, country }.Where(s => !string.IsNullOrWhiteSpace(s)));

            list.Add(new CityResult(display, latEl.GetDouble(), lonEl.GetDouble()));
        }
        return list;
    }

    private static string GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return "";
        }
        return prop.GetString() ?? "";
    }
}
