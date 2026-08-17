using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using DynamicIsland.Controls;

namespace DynamicIsland.Services;

public class WeatherService
{
    // Shared per-service client — avoids a socket/TLS handshake + port churn on
    // every 30-min fetch. Timeout set once (HttpClient.DefaultRequestTimeout).
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;
    
    public bool IsWeatherAvailable { get; private set; } = false;
    public string CurrentTemp { get; private set; } = "—";
    public string Condition { get; private set; } = "Unknown";
    public AppIconKind IconKind { get; private set; } = AppIconKind.WeatherPartlyCloudy;
    public int Humidity { get; private set; } = 0;
    public int WindSpeed { get; private set; } = 0;
    public int AQI { get; private set; } = 0;
    public string AQIDesc { get; private set; } = "Unknown";
    public string AQIColor { get; private set; } = "#FFFFFFFF";

    // 3-Day Forecast
    public ForecastDay[] Forecast { get; private set; } = Array.Empty<ForecastDay>();

    public event EventHandler? WeatherUpdated;

    public string LocationName => App.LocationService.LocationName;

    public WeatherService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public void Initialize()
    {
        // Re-fetch when the location materially changes (e.g. resolved first time).
        App.LocationService.LocationChanged += (_, _) => _ = FetchWeatherAsync();

        // Initial fetch
        _ = FetchWeatherAsync();

        // Poll every 30 minutes
        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMinutes(30);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += async (_, _) => await FetchWeatherAsync();
        _pollTimer.Start();
    }

    public async Task FetchWeatherAsync()
    {
        try
        {
            // WeatherService is a dumb Open-Meteo consumer — LocationService
            // decides where we are.
            if (!App.LocationService.IsAvailable)
            {
                IsWeatherAvailable = false;
                _dispatcherQueue.TryEnqueue(() => WeatherUpdated?.Invoke(this, EventArgs.Empty));
                return;
            }

            double latitude = App.LocationService.Latitude;
            double longitude = App.LocationService.Longitude;

            // Attempt to fetch weather from free Open-Meteo API
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,relative_humidity_2m,wind_speed_10m,weather_code&daily=temperature_2m_max,temperature_2m_min,weather_code&timezone=auto";
            
            var response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                IsWeatherAvailable = false;
                WeatherUpdated?.Invoke(this, EventArgs.Empty);
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("current", out var currentElement))
            {
                double temp = currentElement.GetProperty("temperature_2m").GetDouble();
                CurrentTemp = $"{Math.Round(temp)}°C";
                Humidity = (int)Math.Round(currentElement.GetProperty("relative_humidity_2m").GetDouble());
                WindSpeed = (int)Math.Round(currentElement.GetProperty("wind_speed_10m").GetDouble());

                int weatherCode = currentElement.GetProperty("weather_code").GetInt32();
                (Condition, IconKind) = MapWeatherCode(weatherCode);

                // Mock AQI based on wind speed/humidity for realistic live simulation
                AQI = Math.Clamp(50 - WindSpeed + (Humidity / 10), 10, 150);
                (AQIDesc, AQIColor) = MapAQI(AQI);
            }

            if (root.TryGetProperty("daily", out var dailyElement))
            {
                var maxTemps = dailyElement.GetProperty("temperature_2m_max");
                var minTemps = dailyElement.GetProperty("temperature_2m_min");
                var codes = dailyElement.GetProperty("weather_code");

                var list = new ForecastDay[3];
                for (int i = 0; i < 3; i++)
                {
                    double max = maxTemps[i].GetDouble();
                    double min = minTemps[i].GetDouble();
                    int code = codes[i].GetInt32();
                    string dayName = DateTime.Now.AddDays(i).DayOfWeek.ToString()[..3];
                    if (i == 0) dayName = "Today";

                    var (_, dayIconKind) = MapWeatherCode(code);

                    list[i] = new ForecastDay(
                        Day: dayName,
                        IconKind: dayIconKind,
                        TempRange: $"{Math.Round(max)}°/ {Math.Round(min)}°"
                    );
                }
                Forecast = list;
            }

            IsWeatherAvailable = true;
            _dispatcherQueue.TryEnqueue(() => WeatherUpdated?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("WeatherService: failed to fetch weather data", ex);
            IsWeatherAvailable = false;
            _dispatcherQueue.TryEnqueue(() => WeatherUpdated?.Invoke(this, EventArgs.Empty));
        }
    }

    private static (string Condition, AppIconKind IconKind) MapWeatherCode(int code)
    {
        return code switch
        {
            0 => ("Clear", AppIconKind.WeatherSunny),
            1 or 2 or 3 => ("Partly Cloudy", AppIconKind.WeatherPartlyCloudy),
            45 or 48 => ("Foggy", AppIconKind.WeatherFog),
            51 or 53 or 55 => ("Drizzle", AppIconKind.WeatherDrizzle),
            61 or 63 or 65 => ("Rainy", AppIconKind.WeatherRain),
            71 or 73 or 75 => ("Snowy", AppIconKind.WeatherSnow),
            80 or 81 or 82 => ("Showers", AppIconKind.WeatherShowers),
            95 or 96 or 99 => ("Thunderstorm", AppIconKind.WeatherThunderstorm),
            _ => ("Cloudy", AppIconKind.WeatherCloudy)
        };
    }

    private static (string Desc, string Color) MapAQI(int aqi)
    {
        return aqi switch
        {
            <= 50 => ("Good", "#FF10B981"),      // Green
            <= 100 => ("Moderate", "#FFF59E0B"), // Yellow
            _ => ("Poor", "#FFEF4444")          // Red
        };
    }
}

public record ForecastDay(string Day, AppIconKind IconKind, string TempRange);
