using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using DynamicIsland.Services;
using DynamicIsland.Views;

namespace DynamicIsland;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;
    public static WindowService WindowService { get; private set; } = null!;
    public static MediaService MediaService { get; } = new();
    public static ClipboardService ClipboardService { get; } = new();
    public static BatteryService BatteryService { get; } = new();
    public static VolumeService VolumeService { get; } = new();
    public static WeatherService WeatherService { get; } = new();
    public static BluetoothService BluetoothService { get; } = new();
    public static IslandController IslandController { get; private set; } = null!;
    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Helpers.Logger.Error($"[CRASH] AppDomain.UnhandledException: {ex?.GetType().FullName}: {ex?.Message}", ex);
        };

        this.UnhandledException += (s, e) =>
        {
            Helpers.Logger.Error($"[CRASH] Application.UnhandledException: {e.Exception?.GetType().FullName}: {e.Message}", e.Exception);
            e.Handled = true; // Attempt to recover
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Helpers.Logger.Error($"[CRASH] TaskScheduler.UnobservedTaskException: {e.Exception?.GetType().FullName}: {e.Exception?.Message}", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Helpers.Logger.Info("DynamicIsland application starting up...");

        // Capture UI dispatcher before any async work
        DispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Initialize services
        _ = MediaService.InitializeAsync();
        ClipboardService.Initialize();
        BatteryService.Initialize();
        VolumeService.Initialize();
        WeatherService.Initialize();
        BluetoothService.Initialize();

        // IslandController must be created BEFORE MainWindow so that
        // MainWindowViewModel can subscribe to ActiveControlChanged on construction.
        IslandController = new IslandController(DispatcherQueue);

        // Create window and wire up WindowService
        Window = new MainWindow();
        WindowService = new WindowService(Window);
        WindowService.InitializeWindow(220, 40);

        Window.Activate();
        WindowService.ApplyDwmAttributes(DispatcherQueue);
    }
}
