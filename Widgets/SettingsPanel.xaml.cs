using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using DynamicIsland.Helpers;
using DynamicIsland.Models;
using Windows.UI;

namespace DynamicIsland.Widgets;

/// <summary>
/// Halo Bar Settings (PASS 21): a settings page hosted inside the expanded
/// dashboard (replacing the earlier separate Settings window). Left nav
/// (General, Appearance, Weather, System Monitor, Clipboard, Bluetooth,
/// Privacy) + right content area. Every mutable setting persists through the
/// central <see cref="AppSettings"/> model and applies live (no restart).
/// The dashboard's gear button shows this panel; Back returns to the dashboard.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    private static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Azure", "#FF5B9CFF"),
        ("Purple", "#FF8B5CF6"),
        ("Green", "#FF4ADE80"),
        ("Orange", "#FFFFB900"),
        ("Pink", "#FFFF5FA2"),
        ("Teal", "#FF2EC4B6"),
        ("Red", "#FFFF5F52"),
    };

    private bool _suppressToggle;
    private bool _suppressCombo;

    public SettingsPanel()
    {
        InitializeComponent();

        // The dashboard lives in a WS_EX_NOACTIVATE window, which blocks keyboard
        // focus — the city-search field needs the same temporary-activation trick
        // the clipboard/focus fields use.
        WeatherSearchBox.GotFocus += (_, _) => SetTextInputActive(true);
        WeatherSearchBox.LostFocus += (_, _) => SetTextInputActive(false);

        ApplyCurrentSettings();
        BuildAccentSwatches();
        PopulateDrives();
        PopulateAutoDeleteOptions();
        SelectNav("general");
    }

    /// <summary>Raised when the user taps Back — the dashboard hides this page.</summary>
    public event EventHandler? BackRequested;

    private static void SetTextInputActive(bool active)
    {
        App.WindowService.SetTextInputActive(active);
        if (active) App.Window.Activate();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);

    // ── Nav ─────────────────────────────────────────────────────────────────

    private void SelectNav(string tag)
    {
        GeneralSection.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        WeatherSection.Visibility = tag == "weather" ? Visibility.Visible : Visibility.Collapsed;
        SystemMonitorSection.Visibility = tag == "systemmonitor" ? Visibility.Visible : Visibility.Collapsed;
        ClipboardSection.Visibility = tag == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        BluetoothSection.Visibility = tag == "bluetooth" ? Visibility.Visible : Visibility.Collapsed;
        PrivacySection.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;

        SetNavActive(NavGeneral, tag == "general");
        SetNavActive(NavAppearance, tag == "appearance");
        SetNavActive(NavWeather, tag == "weather");
        SetNavActive(NavSystemMonitor, tag == "systemmonitor");
        SetNavActive(NavClipboard, tag == "clipboard");
        SetNavActive(NavBluetooth, tag == "bluetooth");
        SetNavActive(NavPrivacy, tag == "privacy");
    }

    private static void SetNavActive(Button button, bool active)
    {
        // Subtle selected pill (5% white) + accent text — same language as the
        // dashboard filter pills, instead of a loud solid-accent fill.
        button.Background = active
            ? Application.Current.Resources["Semantic.Surface.ClipItem"] as Brush
            : null;
        button.Foreground = active
            ? Application.Current.Resources["AccentBrush"] as Brush
            : Application.Current.Resources["TextSecondaryBrush"] as Brush;
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
        => SelectNav((string)((Button)sender).Tag);

    // ── Apply persisted state to the controls ───────────────────────────────

    private void ApplyCurrentSettings()
    {
        _suppressToggle = true;
        StartWithWindowsToggle.IsOn = AppSettings.StartWithWindows;
        ShowWeatherToggle.IsOn = AppSettings.ShowWeather;
        ShowCpuToggle.IsOn = AppSettings.ShowCpu;
        ShowDiskToggle.IsOn = AppSettings.ShowDisk;
        ShowRamToggle.IsOn = AppSettings.ShowRam;
        ShowNetworkToggle.IsOn = AppSettings.ShowNetworkSpeed;
        BluetoothPopupToggle.IsOn = AppSettings.ShowBluetoothConnectionPopup;
        _suppressToggle = false;

        // Weather location
        WeatherCurrentLabel.Text = $"Current: {App.LocationService.LocationName}";
        _suppressToggle = true;
        WeatherAutoToggle.IsOn = !App.LocationService.IsManual; // ON = automatic active
        _suppressToggle = false;
        if (!WeatherAutoToggle.IsOn)
        {
            WeatherSearchPanel.Visibility = Visibility.Visible;
        }
    }

    // ── Accent swatches ─────────────────────────────────────────────────────

    private readonly List<(Border Ring, Button Swatch)> _swatches = new();

    private void BuildAccentSwatches()
    {
        AccentSwatchRow.Children.Clear();
        _swatches.Clear();

        foreach (var preset in AccentPresets)
        {
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Child = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush(AccentManager.ParseHex(preset.Hex)),
                },
            };

            var button = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Tag = preset.Hex,
            };
            ToolTipService.SetToolTip(button, preset.Name);
            button.Click += (_, _) => SelectAccent(preset.Hex);

            var cell = new Grid();
            cell.Children.Add(ring);
            cell.Children.Add(button);
            AccentSwatchRow.Children.Add(cell);
            _swatches.Add((ring, button));
        }

        HighlightSelectedAccent(AppSettings.AccentColor);
    }

    private void SelectAccent(string hex)
    {
        AppSettings.SetAccentColor(hex);
        HighlightSelectedAccent(hex);
    }

    private void HighlightSelectedAccent(string hex)
    {
        foreach (var (ring, swatch) in _swatches)
        {
            bool selected = string.Equals((string)swatch.Tag, hex, StringComparison.OrdinalIgnoreCase);
            ring.BorderBrush = selected
                ? Application.Current.Resources["AccentBrush"] as Brush
                : new SolidColorBrush(Colors.Transparent);
        }
    }

    // ── Drive picker ────────────────────────────────────────────────────────

    private void PopulateDrives()
    {
        _suppressCombo = true;
        DriveCombo.Items.Clear();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string letter = drive.Name.TrimEnd('\\', ':');
            var item = new ComboBoxItem
            {
                Content = $"{drive.Name}  {DriveLabel(drive)}",
                Tag = letter,
            };
            DriveCombo.Items.Add(item);
            if (string.Equals(letter, AppSettings.SelectedDrive, StringComparison.OrdinalIgnoreCase))
            {
                DriveCombo.SelectedItem = item;
            }
        }

        if (DriveCombo.SelectedItem == null && DriveCombo.Items.Count > 0)
        {
            DriveCombo.SelectedItem = DriveCombo.Items[0];
        }
        _suppressCombo = false;
    }

    private static string DriveLabel(DriveInfo drive)
    {
        try
        {
            var total = drive.TotalSize / (1024.0 * 1024 * 1024);
            return total >= 1024 ? $"{total / 1024.0:F0} TB" : $"{total:F0} GB";
        }
        catch
        {
            return "";
        }
    }

    private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || DriveCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string letter)
        {
            return;
        }
        AppSettings.SetSelectedDrive(letter);
    }

    // ── Clipboard auto delete ───────────────────────────────────────────────

    private void PopulateAutoDeleteOptions()
    {
        _suppressCombo = true;
        AutoDeleteCombo.Items.Clear();

        (int Days, string Label)[] options =
        {
            (0, "Keep forever"),
            (7, "7 days"),
            (15, "15 days"),
            (30, "30 days"),
            (90, "90 days"),
        };

        // Read the live retention value from the service (single source of truth,
        // mirrors the dashboard's retention dropdown). AppSettings.ClipboardAutoDelete
        // is kept in sync by SetClipboardAutoDelete.
        int current = App.ClipboardService.RetentionDays;
        foreach (var opt in options)
        {
            var item = new ComboBoxItem
            {
                Content = opt.Label,
                Tag = opt.Days,
            };
            AutoDeleteCombo.Items.Add(item);
            if (opt.Days == current)
            {
                AutoDeleteCombo.SelectedItem = item;
            }
        }

        if (AutoDeleteCombo.SelectedItem == null)
        {
            AutoDeleteCombo.SelectedItem = AutoDeleteCombo.Items[0];
        }
        _suppressCombo = false;
    }

    private void AutoDeleteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || AutoDeleteCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int days)
        {
            return;
        }
        AppSettings.SetClipboardAutoDelete(days);
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete all clipboard history?",
            Content = "Every saved item and image will be permanently removed. This cannot be undone.",
            PrimaryButtonText = "Delete All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        dialog.PrimaryButtonStyle = new Style(typeof(Button))
        {
            Setters =
            {
                new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.White)),
                new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x5F, 0x52))),
            },
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            App.ClipboardService.DeleteAllHistory();
        }
    }

    // ── Weather location (automatic + manual city search) ───────────────────

    private async void WeatherAutoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;

        if (WeatherAutoToggle.IsOn)
        {
            await App.LocationService.ClearManualCityAsync();
            WeatherSearchPanel.Visibility = Visibility.Collapsed;
            WeatherSearchBox.Text = "";
            WeatherResultsList.ItemsSource = null;
            WeatherStatusText.Visibility = Visibility.Collapsed;
            WeatherRetryButton.Visibility = Visibility.Collapsed;
            WeatherCurrentLabel.Text = $"Current: {App.LocationService.LocationName}";
        }
        else
        {
            WeatherSearchPanel.Visibility = Visibility.Visible;
        }
    }

    private async void WeatherSearchButton_Click(object sender, RoutedEventArgs e)
        => await RunWeatherSearchAsync();

    private async void WeatherSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await RunWeatherSearchAsync();
        }
    }

    private async void WeatherRetryButton_Click(object sender, RoutedEventArgs e)
        => await RunWeatherSearchAsync();

    private async Task RunWeatherSearchAsync()
    {
        string query = WeatherSearchBox.Text?.Trim() ?? "";
        if (query.Length == 0) return;

        WeatherStatusText.Visibility = Visibility.Collapsed;
        WeatherRetryButton.Visibility = Visibility.Collapsed;
        WeatherResultsList.ItemsSource = null;
        WeatherSearchButton.IsEnabled = false;

        try
        {
            var results = await CityGeocoder.SearchAsync(query);
            if (results.Count == 0)
            {
                WeatherStatusText.Text = $"No results for '{query}'";
                WeatherStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                WeatherResultsList.ItemsSource = results;
            }
        }
        catch
        {
            WeatherRetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            WeatherSearchButton.IsEnabled = true;
        }
    }

    private async void WeatherResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CityResult city) return;

        await App.LocationService.SaveManualCityAsync(city.DisplayName, city.Latitude, city.Longitude);
        WeatherAutoToggle.IsOn = false; // manual override active
        WeatherSearchPanel.Visibility = Visibility.Visible;
        WeatherSearchBox.Text = "";
        WeatherResultsList.ItemsSource = null;
        WeatherCurrentLabel.Text = $"Current: {App.LocationService.LocationName}";
    }

    // ── Toggle handlers ─────────────────────────────────────────────────────

    private void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppSettings.SetStartWithWindows(StartWithWindowsToggle.IsOn);
    }

    private void ShowWeatherToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppSettings.SetShowWeather(ShowWeatherToggle.IsOn);
    }

    private void ShowCpuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppSettings.SetShowCpu(ShowCpuToggle.IsOn);
    }

    private void ShowDiskToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppSettings.SetShowDisk(ShowDiskToggle.IsOn);
    }

    private void ShowRamToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppSettings.SetShowRam(ShowRamToggle.IsOn);
    }

    private void ShowNetworkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppSettings.SetShowNetworkSpeed(ShowNetworkToggle.IsOn);
    }

    private void BluetoothPopupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppSettings.SetShowBluetoothConnectionPopup(BluetoothPopupToggle.IsOn);
    }
}