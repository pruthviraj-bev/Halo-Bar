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
    public static CompactLayoutController CompactLayoutController { get; private set; } = null!;
    public static MediaService MediaService { get; } = new();
    public static ClipboardService ClipboardService { get; } = new();
    public static BatteryService BatteryService { get; } = new();
    public static VolumeService VolumeService { get; } = new();
    public static LocationService LocationService { get; } = new();
    public static WeatherService WeatherService { get; } = new();
    public static BluetoothService BluetoothService { get; } = new();
    public static FileShelfStore FileShelfStore { get; } = new();
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
        LocationService.Initialize();
        WeatherService.Initialize();
        BluetoothService.Initialize();

        // IslandController must be created BEFORE MainWindow so that
        // MainWindowViewModel can subscribe to ActiveControlChanged on construction.
        IslandController = new IslandController(DispatcherQueue);

        // Create window and wire up WindowService.
        // CompactLayoutController is the sole authority for compact geometry and
        // is created first so WindowService can consume it passively.
        Window = new MainWindow();
        CompactLayoutController = new CompactLayoutController(Window);
        WindowService = new WindowService(Window, CompactLayoutController);

        // Any mouse press outside the dock collapses the expanded island immediately.
        // Guarded inside NotifyFocusLost by the awake-hold, so open settings surfaces
        // (gear flyout, Focus settings) are never clobbered.
        WindowService.MouseClickedOutside += (_, _) => IslandController.NotifyFocusLost();

        // Apply all DWM/borderless/toolwindow/owner styling while the window is
        // still HIDDEN so its very first present (triggered by InitializeWindow's
        // MoveAndResize / Window.Activate) is already styled. Previously this ran
        // AFTER Activate(), causing a default-styled opaque first frame (black flash).
        WindowService.ApplyDwmAttributes(DispatcherQueue);

        // Measure the taskbar BEFORE the first placement so the window is created
        // anchored in the free zone (right of Start/Search) instead of at x=0.
        CompactLayoutController.Start();

        WindowService.InitializeWindow(CompactLayoutController.CompactIdealWidth, 40);

        WindowService.FullscreenStateChanged += (s, isFullscreen) =>
        {
            Window.DispatcherQueue.TryEnqueue(() =>
            {
                Window.SetFullscreenSuppressed(isFullscreen);
            });
        };

        Window.Activate();
    }
}
