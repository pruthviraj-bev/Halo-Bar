using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DynamicIsland.Widgets;

/// <summary>
/// Flyout-hosted location settings: an Automatic toggle backed by LocationService,
/// plus a manual city search (Open-Meteo geocoding) that pins the selected city.
/// Errors are shown inline — never crash.
/// </summary>
public sealed partial class LocationSettingsPopup : UserControl
{
    /// <summary>Raised when the user picks a manual city; host closes the flyout.</summary>
    public event EventHandler? RequestClose;

    private bool _suppressToggle;

    public LocationSettingsPopup()
    {
        InitializeComponent();

        // Typing in the popup needs the owner window's WS_EX_NOACTIVATE lifted while
        // the search box holds focus; the popup window itself stays activatable.
        SearchBox.GotFocus += (_, _) => App.WindowService.SetTextInputActive(true);
        SearchBox.LostFocus += (_, _) => App.WindowService.SetTextInputActive(false);

        Loaded += (_, _) =>
        {
            CurrentLabel.Text = $"Current: {App.LocationService.LocationName}";
            _suppressToggle = true;
            AutoToggle.IsOn = !App.LocationService.IsManual; // ON = automatic active
            _suppressToggle = false;
        };
    }

    private async void AutoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;

        if (AutoToggle.IsOn)
        {
            await App.LocationService.ClearManualCityAsync();
            SearchPanel.Visibility = Visibility.Collapsed;
            SearchBox.Text = "";
            ResultsList.ItemsSource = null;
            StatusText.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            CurrentLabel.Text = $"Current: {App.LocationService.LocationName}";
        }
        else
        {
            SearchPanel.Visibility = Visibility.Visible;
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSearchAsync();
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await RunSearchAsync();
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        string query = SearchBox.Text?.Trim() ?? "";
        if (query.Length == 0) return;

        StatusText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ResultsList.ItemsSource = null;
        SearchButton.IsEnabled = false;

        try
        {
            var results = await SearchCitiesAsync(query);
            if (results.Count == 0)
            {
                StatusText.Text = $"No results for '{query}'";
                StatusText.Visibility = Visibility.Visible;
            }
            else
            {
                ResultsList.ItemsSource = results;
            }
        }
        catch
        {
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private async void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CityResult city) return;

        await App.LocationService.SaveManualCityAsync(city.DisplayName, city.Latitude, city.Longitude);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<List<CityResult>> SearchCitiesAsync(string query)
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

/// <summary>One tappable geocoding hit.</summary>
public record CityResult(string DisplayName, double Latitude, double Longitude);
