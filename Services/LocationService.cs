using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using Microsoft.UI.Dispatching;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Resolves the user's location through a strict chain — Windows Geolocation, then
/// IP geolocation (ipwho.is, no key), then last-known persisted location. Never
/// invents coordinates: if no live source succeeds and no last-known exists, the
/// service reports unavailable and keeps previous values.
/// Persists a successful live resolution to %LOCALAPPDATA%\DynamicIsland\location.json
/// so a restart can serve a value before the first network round-trip.
/// </summary>
public sealed class LocationService
{
    private const double MeaningfulChangeDegrees = 0.01;
    private const int ResolveIntervalMinutes = 30;

    private static readonly string LocationFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland",
        "location.json"
    );

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private DispatcherQueueTimer? _pollTimer;
    private bool _isResolving;

    public string LocationName { get; private set; } = "";
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public bool IsResolving => _isResolving;
    public bool IsAvailable { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }

    /// <summary>Raised when coordinates meaningfully change (or become available).</summary>
    public event EventHandler? LocationChanged;

    public void Initialize()
    {
        LoadLastKnown();

        _ = ResolveAsync();

        _pollTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMinutes(ResolveIntervalMinutes);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += (_, _) => _ = ResolveAsync();
        _pollTimer.Start();
    }

    private async Task ResolveAsync()
    {
        lock (_gate)
        {
            if (_isResolving) return;
            _isResolving = true;
        }

        try
        {
            if (await TryWindowsGeolocationAsync()) return;
            if (await TryIpGeolocationAsync()) return;

            // Both live sources failed — fall through to the last-known location
            // seeded at Initialize. If none exists, remain unavailable; never
            // invent coordinates.
            Logger.Info("[LOCATION] live resolution failed; using last known");
        }
        finally
        {
            lock (_gate)
            {
                _isResolving = false;
            }
        }
    }

    private async Task<bool> TryWindowsGeolocationAsync()
    {
        try
        {
            var geolocator = new Geolocator();
            geolocator.DesiredAccuracy = PositionAccuracy.Default;
            var op = geolocator.GetGeopositionAsync();
            var positionTask = op.AsTask();
            var completed = await Task.WhenAny(positionTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != positionTask) return false; // no fix in time — move on

            var position = op.GetResults();
            ApplyLocation("", position.Coordinate.Latitude, position.Coordinate.Longitude);
            return true;
        }
        catch
        {
            // Location capability/consent is finicky for unpackaged apps — fall through.
            return false;
        }
    }

    private async Task<bool> TryIpGeolocationAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var response = await client.GetAsync("https://ipwho.is/");
            if (!response.IsSuccessStatusCode) return false;

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var success) && !success.GetBoolean()) return false;
            if (!root.TryGetProperty("latitude", out var latEl) || !root.TryGetProperty("longitude", out var lonEl)) return false;

            double lat = latEl.GetDouble();
            double lon = lonEl.GetDouble();
            if (lat == 0 && lon == 0) return false;

            ApplyLocation(BuildDisplayName(root), lat, lon);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyLocation(string name, double lat, double lon)
    {
        bool meaningfulChange = !IsAvailable
            || Math.Abs(lat - Latitude) > MeaningfulChangeDegrees
            || Math.Abs(lon - Longitude) > MeaningfulChangeDegrees;

        Latitude = lat;
        Longitude = lon;
        if (!string.IsNullOrWhiteSpace(name)) LocationName = name;
        IsAvailable = true;
        LastUpdated = DateTimeOffset.Now;

        Persist();

        if (meaningfulChange)
        {
            Logger.Info($"[LOCATION] resolved {LocationName} ({lat:F4}, {lon:F4})");
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LoadLastKnown()
    {
        try
        {
            if (!File.Exists(LocationFile)) return;

            var json = File.ReadAllText(LocationFile);
            var stored = JsonSerializer.Deserialize<StoredLocation>(json, SerializerOptions);
            if (stored == null) return;

            Latitude = stored.Latitude;
            Longitude = stored.Longitude;
            LocationName = stored.Name ?? "";
            LastUpdated = stored.UpdatedAt;
            IsAvailable = true;

            Logger.Info($"[LOCATION] last known loaded: {LocationName} ({stored.Latitude:F4}, {stored.Longitude:F4})");
        }
        catch (Exception ex)
        {
            Logger.Error("LocationService: failed to load last-known location", ex);
        }
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(LocationFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var stored = new StoredLocation(Latitude, Longitude, LocationName, LastUpdated);
            File.WriteAllText(LocationFile, JsonSerializer.Serialize(stored, SerializerOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("LocationService: failed to persist location", ex);
        }
    }

    private static string BuildDisplayName(JsonElement root)
    {
        string city = GetStringProp(root, "city");
        string region = GetStringProp(root, "region");
        string country = GetStringProp(root, "country");

        if (!string.IsNullOrWhiteSpace(city))
        {
            if (!string.IsNullOrWhiteSpace(region)) return $"{city}, {region}";
            if (!string.IsNullOrWhiteSpace(country)) return $"{city}, {country}";
            return city;
        }
        if (!string.IsNullOrWhiteSpace(region)) return region;
        return country;
    }

    private static string GetStringProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return "";
        return prop.GetString() ?? "";
    }
}

/// <summary>Persisted snapshot of a successful live location resolution.</summary>
public record StoredLocation(double Latitude, double Longitude, string Name, DateTimeOffset UpdatedAt);
