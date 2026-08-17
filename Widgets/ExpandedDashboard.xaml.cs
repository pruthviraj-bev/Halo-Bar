using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using DynamicIsland.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Models;
using DynamicIsland.ViewModels;
using DynamicIsland.Services;

namespace DynamicIsland.Widgets;

public sealed partial class ExpandedDashboard : UserControl, INotifyPropertyChanged
{
    private DispatcherTimer? _updateTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── Properties ─────────────────────────────────────────────────────────

    public MediaWidgetViewModel MediaViewModel { get; } = new();

    private string _currentPlaybackTimeText = "0:00";
    public string CurrentPlaybackTimeText
    {
        get => _currentPlaybackTimeText;
        set { if (_currentPlaybackTimeText == value) return; _currentPlaybackTimeText = value; OnPropertyChanged(); }
    }

    private string _totalPlaybackTimeText = "0:00";
    public string TotalPlaybackTimeText
    {
        get => _totalPlaybackTimeText;
        set { if (_totalPlaybackTimeText == value) return; _totalPlaybackTimeText = value; OnPropertyChanged(); }
    }

    private string _liveClipboardTitle = "main.cpp";
    public string LiveClipboardTitle
    {
        get => _liveClipboardTitle;
        set { if (_liveClipboardTitle == value) return; _liveClipboardTitle = value; OnPropertyChanged(); }
    }

    // ── Clipboard history list ─────────────────────────────────────────────

    /// <summary>
    /// Items currently shown by the clipboard list, filtered by the All/Pinned toggle.
    /// </summary>
    public ObservableCollection<ClipboardItem> ClipboardItems { get; } = new();

    private Visibility _clipboardEmptyVisibility = Visibility.Collapsed;
    public Visibility ClipboardEmptyVisibility
    {
        get => _clipboardEmptyVisibility;
        set { _clipboardEmptyVisibility = value; OnPropertyChanged(); }
    }

    // ── Bluetooth devices list ─────────────────────────────────────────────

    /// <summary>
    /// Devices currently shown by the Bluetooth card, mirrored from
    /// App.BluetoothService.Devices on every BluetoothUpdated.
    /// </summary>
    public ObservableCollection<BluetoothDeviceInfo> BluetoothItems { get; } = new();

    private Visibility _bluetoothEmptyVisibility = Visibility.Collapsed;
    public Visibility BluetoothEmptyVisibility
    {
        get => _bluetoothEmptyVisibility;
        set { _bluetoothEmptyVisibility = value; OnPropertyChanged(); }
    }

    private Visibility _bluetoothListVisibility = Visibility.Collapsed;
    public Visibility BluetoothListVisibility
    {
        get => _bluetoothListVisibility;
        set { _bluetoothListVisibility = value; OnPropertyChanged(); }
    }

    // PASS 7: CONNECTED/AVAILABLE radio filter. Defaults to connected-only, matching
    // the reference's Azure-active CONNECTED pill; AVAILABLE shows the rest.
    private bool _bluetoothShowConnectedOnly = true;

    private string _ramUsedText = "9.6 GB";
    public string RamUsedText
    {
        get => _ramUsedText;
        set { if (_ramUsedText == value) return; _ramUsedText = value; OnPropertyChanged(); }
    }
    private string _ramTotalText = "16.0 GB";
    public string RamTotalText
    {
        get => _ramTotalText;
        set { if (_ramTotalText == value) return; _ramTotalText = value; OnPropertyChanged(); }
    }

    private int _ramPercent = 0;
    public int RamPercent
    {
        get => _ramPercent;
        set { if (_ramPercent == value) return; _ramPercent = value; OnPropertyChanged(); }
    }

    private string _ramPercentText = "—";
    public string RamPercentText
    {
        get => _ramPercentText;
        set { if (_ramPercentText == value) return; _ramPercentText = value; OnPropertyChanged(); }
    }

    private string _cpuPercentText = "—";
    public string CpuPercentText
    {
        get => _cpuPercentText;
        set { if (_cpuPercentText == value) return; _cpuPercentText = value; OnPropertyChanged(); }
    }

    // PASS 20: footer live network throughput (bytes/s → MB/s text).
    private string _networkDownloadText = "0 MB/s";
    public string NetworkDownloadText
    {
        get => _networkDownloadText;
        set { if (_networkDownloadText == value) return; _networkDownloadText = value; OnPropertyChanged(); }
    }

    private string _networkUploadText = "0 MB/s";
    public string NetworkUploadText
    {
        get => _networkUploadText;
        set { if (_networkUploadText == value) return; _networkUploadText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Formats a throughput in bytes/s for the footer as MB/s (decimal megabytes,
    /// /1,000,000 — never bits). One decimal place below 100 MB/s keeps low
    /// activity visible (0.2 MB/s, 1.2 MB/s); a trailing ".0" is trimmed so
    /// whole values stay compact (10 MB/s, not 10.0 MB/s). GB/s is only used
    /// above 1,000 MB/s so normal network usage never switches units/jitters.
    /// </summary>
    public static string FormatNetworkRate(long bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000_000)
            return $"{FormatValue(bytesPerSecond / 1_000_000_000.0)} GB/s";
        return $"{FormatValue(bytesPerSecond / 1_000_000.0)} MB/s";
    }

    private static string FormatValue(double value)
    {
        string text = value.ToString("F1");
        return text.EndsWith(".0") ? text[..^2] : text;
    }

    private string _batteryPercentText = "—";
    public string BatteryPercentText
    {
        get => _batteryPercentText;
        set { if (_batteryPercentText == value) return; _batteryPercentText = value; OnPropertyChanged(); }
    }

    private string _batteryStatusText = "Charging";
    public string BatteryStatusText
    {
        get => _batteryStatusText;
        set { if (_batteryStatusText == value) return; _batteryStatusText = value; OnPropertyChanged(); }
    }

    private Visibility _batteryChargingVisibility = Visibility.Collapsed;
    public Visibility BatteryChargingVisibility
    {
        get => _batteryChargingVisibility;
        set { if (_batteryChargingVisibility == value) return; _batteryChargingVisibility = value; OnPropertyChanged(); }
    }

    private AppIconKind _batteryIconKind = AppIconKind.Battery9;
    public AppIconKind BatteryIconKind
    {
        get => _batteryIconKind;
        set { if (_batteryIconKind == value) return; _batteryIconKind = value; OnPropertyChanged(); }
    }

    private string _storageFreeText = "215 GB";
    public string StorageFreeText
    {
        get => _storageFreeText;
        set { if (_storageFreeText == value) return; _storageFreeText = value; OnPropertyChanged(); }
    }

    private string _storageTotalText = "512 GB";
    public string StorageTotalText
    {
        get => _storageTotalText;
        set { if (_storageTotalText == value) return; _storageTotalText = value; OnPropertyChanged(); }
    }

    private int _storagePercent = 0;
    public int StoragePercent
    {
        get => _storagePercent;
        set
        {
            if (_storagePercent == value) return;
            _storagePercent = value;
            OnPropertyChanged();
        }
    }

    private string _storagePercentText = "—";
    public string StoragePercentText
    {
        get => _storagePercentText;
        set { if (_storagePercentText == value) return; _storagePercentText = value; OnPropertyChanged(); }
    }

    // ── Volume Slider Integration ──────────────────────────────────────────

    public double PlaybackProgressValue
    {
        get
        {
            var vm = MediaViewModel;
            if (vm.Duration <= TimeSpan.Zero) return 0;
            TimeSpan pos = vm.Position;
            if (vm.IsPlaying)
            {
                var elapsed = DateTimeOffset.Now - vm.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero) pos += elapsed;
                if (pos > vm.Duration) pos = vm.Duration;
            }
            return (pos.TotalSeconds / vm.Duration.TotalSeconds) * 100.0;
        }
    }

    /// <summary>
    /// PASS 5.1: volume icon reflects the real mute state (mute icon only while
    /// muted; normal speaker icon otherwise). Windows endpoint mute is
    /// independent of the volume level, so unmuting always restores the
    /// previous volume automatically.
    /// </summary>
    public AppIconKind VolumeIconKind =>
        App.VolumeService.CurrentState.IsMuted ? AppIconKind.SpeakerMute : AppIconKind.Speaker1;

    public double VolumePercentValue
    {
        get => App.VolumeService.CurrentState.VolumePercent;
        set
        {
            App.VolumeService.SetVolume((int)value);
            OnPropertyChanged();
        }
    }

    // ── Real Weather Properties ────────────────────────────────────────────

    public Visibility WeatherAvailableVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility WeatherUnavailableVisibility { get; private set; } = Visibility.Visible;
    public string WeatherTemp { get; private set; } = "—";
    public string WeatherCondition { get; private set; } = "Retrieving Weather...";
    public AppIconKind WeatherIconKind { get; private set; } = AppIconKind.WeatherPartlyCloudy;

    public string ForecastDay1Text { get; private set; } = "—";
    public AppIconKind ForecastDay1IconKind { get; private set; } = AppIconKind.WeatherPartlyCloudy;
    public string ForecastDay1Temp { get; private set; } = "—";

    public string ForecastDay2Text { get; private set; } = "—";
    public AppIconKind ForecastDay2IconKind { get; private set; } = AppIconKind.WeatherPartlyCloudy;
    public string ForecastDay2Temp { get; private set; } = "—";

    public string ForecastDay3Text { get; private set; } = "—";
    public AppIconKind ForecastDay3IconKind { get; private set; } = AppIconKind.WeatherPartlyCloudy;
    public string ForecastDay3Temp { get; private set; } = "—";

    // ── Focus Session Properties ───────────────────────────────────────────

    private List<FocusSession> _focusSessions = new() { new() { Name = "Focus", DurationSeconds = 1500 } };
    private int _selectedFocusSessionIndex = 0;
    private int _focusSecondsRemaining = 0; // neutral; the constructor derives the real value from the active session before InitializeComponent

    /// <summary>
    /// Sessions shown by the Focus Session dot switcher (backed by _focusSessions).
    /// </summary>
    public List<FocusSession> FocusSessions => _focusSessions;
    private bool _focusIsRunning = false;

    // PASS 10: a session is "active" (shown as the pill ring card) from the
    // moment it starts counting until it completes or is reset — including
    // while PAUSED (running=false but a session is in progress). Set true on
    // start/resume, false on completion (remaining hits 0) and on reset. The
    // pill card must NOT vanish on pause.
    private bool _focusSessionActive = false;
    private bool _focusSessionCompleted = false;

    /// <summary>
    /// Session length (seconds) for the currently selected focus session.
    /// </summary>
    private int FocusTotalSeconds => _focusSessions[_selectedFocusSessionIndex].DurationSeconds;

    public string CurrentSessionName => _focusSessions[_selectedFocusSessionIndex].Name;

    public string FocusTimerText => $"{_focusSecondsRemaining / 60:D2}:{_focusSecondsRemaining % 60:D2}";

    /// <summary>
    /// Secondary readout under the timer: the selected session's total duration
    /// ("60 min", "1h 25m", "25:30" when sub-minute seconds are set).
    /// </summary>
    public string FocusDurationText
    {
        get
        {
            int total = _focusSessions[_selectedFocusSessionIndex].DurationSeconds;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            if (h > 0) return s > 0 ? $"{h}h {m:D2}m {s:D2}s" : $"{h}h {m:D2}m";
            if (s > 0) return $"{m}:{s:D2}";
            return $"{m} min";
        }
    }

    public AppIconKind FocusPlayPauseIconKind => _focusIsRunning ? AppIconKind.Pause : AppIconKind.Play;

    /// <summary>
    /// Focus session progress from 0.0 (session start) to 1.0 (time elapsed).
    /// The ring fills up clockwise as the session runs down.
    /// </summary>
    public double FocusProgressFraction => 1.0 - ((double)_focusSecondsRemaining / FocusTotalSeconds);

    /// <summary>
    /// Ring brush: Azure while a session is in progress; the Success green once
    /// it completes, so the ring itself signals "done".
    /// </summary>
    public Brush FocusRingBrush => _focusSessionCompleted
        ? GetThemeBrush("Semantic.State.Success")
        : GetThemeBrush("AccentBrush");

    private void SetFocusCompleted(bool completed)
    {
        if (_focusSessionCompleted == completed) return;
        _focusSessionCompleted = completed;
        OnPropertyChanged(nameof(FocusRingBrush));
    }

    /// <summary>
    /// Publishes the current focus state to <see cref="Services.FocusSessionBridge"/>
    /// so collapsed-pill consumers (the Pomodoro ring card) stay in sync with the
    /// dashboard's timer. Called from the 1 s tick and every state-change handler.
    /// </summary>
    private void PublishFocusState()
        => FocusSessionBridge.Publish(_focusIsRunning, _focusSessionActive || _focusIsRunning, FocusProgressFraction);

    // ── Focus Session ring geometry + drag state ───────────────────────────

    // PASS 4: 160 DIP ring, 14 DIP stroke → stroke-centerline radius 73. These
    // stay in sync with FocusProgressToArcConverter (Center/Radius) so the arc,
    // pointer, and drag/proximity math all share one geometry.
    private const double FocusRingCenter = 80;
    private const double FocusRingRadius = 73;

    private bool _isFocusRingDragging;
    private bool _isFocusPillHovered;
    private double _dragAccumulatedFraction;
    private double _dragLastAngle;

    // ── Quick Tasks Properties ─────────────────────────────────────────────

    public ObservableCollection<TaskItem> Tasks { get; } = new();

    // ── Win32 RAM structure ───────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public int cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public int HandleCount;
        public int ProcessCount;
        public int ThreadCount;
    }

    private static readonly int PerformanceInfoSize = Marshal.SizeOf<PERFORMANCE_INFORMATION>();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, int cb);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low, High; public long ToLong() => ((long)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    // ── Constructor ────────────────────────────────────────────────────────

    public ExpandedDashboard()
    {
        // Load persisted focus sessions BEFORE InitializeComponent: the dots'
        // ItemsControl binds ItemsSource with x:Bind (OneTime), which evaluates
        // during InitializeComponent, so _focusSessions must already hold the
        // disk-loaded list or the dots would show only the field-initializer default.
        _focusSessions = FocusSessionStore.LoadAll();

        // PR-1: first-render state must derive from the persisted session list, never from
        // mock literals. This runs before InitializeComponent so the OneTime x:Bind of
        // FocusSessions sees derived values on first render.
        _selectedFocusSessionIndex = 0;
        _focusSecondsRemaining = FocusTotalSeconds;
        _focusIsRunning = false;

        // Defensive clamp: the store guarantees >= 1 session, but never allow an
        // out-of-range index (belt-and-suspenders against internal inconsistency).
        if (_selectedFocusSessionIndex >= _focusSessions.Count)
        {
            _selectedFocusSessionIndex = 0;
        }

        InitializeComponent();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        PlaybackSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PlaybackSlider_PointerPressed), true);
        PlaybackSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PlaybackSlider_PointerReleased), true);
        PlaybackSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(PlaybackSlider_PointerReleased), true);
        PlaybackSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PlaybackSlider_PointerReleased), true);

        // Text fields live in a WS_EX_NOACTIVATE window, which blocks keyboard focus.
        // Temporarily lift it while any of them is focused so typing actually works.
        AttachTextInputFocus(FocusSettingsNameBox);
        AttachTextInputFocus(FocusSettingsHoursBox);
        AttachTextInputFocus(FocusSettingsMinutesBox);
        AttachTextInputFocus(FocusSettingsSecondsBox);

        UpdateRepeatVisual();
        MediaViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaViewModel.IsRepeatActive))
            {
                UpdateRepeatVisual();
            }
        };

        // Initial query. Pass 16 Mode C (HALO_P16_NODATA=1) skips ALL data
        // initialization (stats, tasks, weather, clipboard, bluetooth, timer)
        // while keeping the full visual tree — isolates data/binding cost from
        // visual/layout cost. Production behavior unchanged.
        if (!MotionDiagnostics.P16NoData)
        {
            UpdateStats();

            // Initialize sample tasks
            Tasks.Add(new TaskItem { Text = "Review PR #42", IsCompleted = false });
            Tasks.Add(new TaskItem { Text = "Sync design tokens", IsCompleted = false });
        }

        // Subscribe to real service updates
        App.WeatherService.WeatherUpdated += OnWeatherUpdated;

        // Force initial update calls to load values immediately
        if (!MotionDiagnostics.P16NoData)
            OnWeatherUpdated(null, EventArgs.Empty);

        // Wire up the clipboard history list
        App.ClipboardService.History.CollectionChanged += OnClipboardHistoryChanged;
        UpdateFilterVisual();
        if (!MotionDiagnostics.P16NoData)
            RefreshClipboardFilter();

        // Virtualization profiling + recycling hygiene: track how many row
        // containers are actually realized, and reset reveal-strip visuals when
        // a container is recycled so an open strip never leaks onto a different item.
        ClipboardRepeater.ElementPrepared += ClipboardRepeater_ElementPrepared;
        ClipboardRepeater.ElementClearing += ClipboardRepeater_ElementClearing;
        Loaded += ExpandedDashboard_Loaded;

        // Wire up the Bluetooth devices list
        App.BluetoothService.BluetoothUpdated += OnBluetoothUpdated;
        UpdateBluetoothFilterVisual();
        if (!MotionDiagnostics.P16NoData)
            RefreshBluetoothList();

        // Clipboard retention + search: enable typing in the search box
        // (WS_EX_NOACTIVATE requires the temporary flag-lift, same pattern as
        // the Focus session text fields). Retention moved to Settings (PASS 21).
        AttachTextInputFocus(ClipboardSearchBox);

        // PASS 21: live footer visibility + selected drive from central settings.
        // The Settings window mutates AppSettings; this dashboard reflects it
        // immediately without a restart.
        Models.AppSettings.Changed += ApplyFooterSettings;
        ApplyFooterSettings();

        // PASS 21: if the island collapses by any path (Home, hotkey, drag) while
        // the Settings page overlay is open, hide it and release the awake hold so
        // it doesn't linger over the dashboard on the next expansion.
        App.IslandController.IsExpandedChanged += OnIslandExpandedChanged;

        // Timer for system stats and play time updates (1s). Keeps ticking even
        // while the dashboard is collapsed (it stays in the tree after the first
        // expansion); UpdateStats gates the sampling work on visibility so a
        // hidden dashboard costs almost nothing.
        _updateTimer = new DispatcherTimer();
        _updateTimer.Interval = TimeSpan.FromSeconds(1);
        _updateTimer.Tick += (s, e) =>
        {
            App.MediaService.TickValidation();
            UpdateStats();
        };
        if (!MotionDiagnostics.P16NoData)
            _updateTimer.Start();

        // Pass 16: first real layout marker — the dashboard is constructed at
        // preload but never measured until first expand (collapsed subtree), so
        // the first non-zero SizeChanged pinpoints the first-layout instant on
        // the animation critical path.
        if (MotionDiagnostics.P16Enabled)
        {
            SizeChanged += (_, e) =>
            {
                if (!_p16LayoutLogged && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    _p16LayoutLogged = true;
                    MotionDiagnostics.P16Mark("DashboardFirstLayout", "dashboard",
                        $"size={(int)e.NewSize.Width}x{(int)e.NewSize.Height}");
                }
            };
        }
    }

    // ── Weather Service handler ────────────────────────────────────────────

    /// <summary>
    /// Subtle 0.5 s opacity/scale pulse on the footer temp when a weather update
    /// lands, so a refresh is visible without a jarring flash.
    /// </summary>
    private void PulseWeatherFooter()
    {
        if (WeatherFooterText == null || WeatherFooterText.Visibility != Visibility.Visible) return;
        if (WeatherFooterText.RenderTransform is not CompositeTransform) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var pulse = new Storyboard();

        var fade = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        fade.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.0, KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) });
        fade.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 0.35, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)) });
        fade.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.0, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), EasingFunction = ease });
        Storyboard.SetTarget(fade, WeatherFooterText);
        Storyboard.SetTargetProperty(fade, "Opacity");
        pulse.Children.Add(fade);

        var scaleX = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.0, KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) });
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.15, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), EasingFunction = ease });
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.0, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), EasingFunction = ease });
        Storyboard.SetTarget(scaleX, WeatherFooterText);
        Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
        pulse.Children.Add(scaleX);

        var scaleY = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.0, KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) });
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.15, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), EasingFunction = ease });
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame { Value = 1.0, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), EasingFunction = ease });
        Storyboard.SetTarget(scaleY, WeatherFooterText);
        Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
        pulse.Children.Add(scaleY);

        pulse.Begin();
    }

    private void OnWeatherUpdated(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var ws = App.WeatherService;
            WeatherAvailableVisibility = ws.IsWeatherAvailable ? Visibility.Visible : Visibility.Collapsed;
            WeatherUnavailableVisibility = ws.IsWeatherAvailable ? Visibility.Collapsed : Visibility.Visible;

            if (ws.IsWeatherAvailable)
            {
                WeatherTemp = ws.CurrentTemp;
                WeatherCondition = ws.Condition;
                WeatherIconKind = ws.IconKind;
                PulseWeatherFooter();

                if (ws.Forecast.Length >= 3)
                {
                    ForecastDay1Text = ws.Forecast[0].Day;
                    ForecastDay1IconKind = ws.Forecast[0].IconKind;
                    ForecastDay1Temp = ws.Forecast[0].TempRange;

                    ForecastDay2Text = ws.Forecast[1].Day;
                    ForecastDay2IconKind = ws.Forecast[1].IconKind;
                    ForecastDay2Temp = ws.Forecast[1].TempRange;

                    ForecastDay3Text = ws.Forecast[2].Day;
                    ForecastDay3IconKind = ws.Forecast[2].IconKind;
                    ForecastDay3Temp = ws.Forecast[2].TempRange;
                }
            }

            OnPropertyChanged(nameof(WeatherAvailableVisibility));
            OnPropertyChanged(nameof(WeatherUnavailableVisibility));
            OnPropertyChanged(nameof(WeatherTemp));
            OnPropertyChanged(nameof(WeatherCondition));
            OnPropertyChanged(nameof(WeatherIconKind));
            OnPropertyChanged(nameof(ForecastDay1Text));
            OnPropertyChanged(nameof(ForecastDay1IconKind));
            OnPropertyChanged(nameof(ForecastDay1Temp));
            OnPropertyChanged(nameof(ForecastDay2Text));
            OnPropertyChanged(nameof(ForecastDay2IconKind));
            OnPropertyChanged(nameof(ForecastDay2Temp));
            OnPropertyChanged(nameof(ForecastDay3Text));
            OnPropertyChanged(nameof(ForecastDay3IconKind));
            OnPropertyChanged(nameof(ForecastDay3Temp));
        });
    }



    // ── Stats updates ──────────────────────────────────────────────────────

    private int _lastVol = -1;
    private bool _lastMuted;
    private double _lastPlaybackProgress = double.NegativeInfinity;

    // The constructor's initial UpdateStats must always run (the element is not
    // measured yet, so ActualWidth is 0), so the dashboard opens with real stats
    // instead of placeholders. Only subsequent timer ticks honor the gate.
    private bool _initialStatsPending = true;

    // DriveInfo ctor is a Win32 probe; cache the instance and re-query its
    // properties (cheap) instead of constructing it every second.
    private System.IO.DriveInfo? _statsDrive;
    private string? _statsDriveName;

    private void UpdateStats()
    {
        // 1. Focus Session Timer — always runs: the session countdown is global
        // (it must keep ticking while the dashboard is collapsed).
        if (_focusIsRunning)
        {
            if (_focusSecondsRemaining > 0)
            {
                _focusSecondsRemaining--;
                if (_focusSecondsRemaining == 0)
                {
                    _focusIsRunning = false;          // stop counting; remain at 00:00
                    _focusSessionActive = false;      // session completed — pill ring hides
                    SetFocusCompleted(true);          // ring turns green — session done
                }
            }
            OnPropertyChanged(nameof(FocusTimerText));
            OnPropertyChanged(nameof(FocusProgressFraction));
            OnPropertyChanged(nameof(FocusPlayPauseIconKind)); // icon returns to Play
            FocusPillRotate.Angle = FocusProgressFraction * 360;
        }
        PublishFocusState();

        // Hidden while collapsed (MainWindow collapses the DashboardBorder
        // ANCESTOR — a collapsed subtree reports ActualWidth=0): skip all
        // sampling work. The dashboard's 1 s timer keeps ticking after the first
        // expansion, and re-sampling CPU/RAM/storage/volume/playback at 1 Hz
        // while hidden is pure waste.
        if (_initialStatsPending)
        {
            _initialStatsPending = false;
        }
        else if (Visibility != Visibility.Visible || ActualWidth <= 0)
        {
            return;
        }

        // 2. Playback time (real timeline, interpolated live while playing)
        UpdatePlaybackDisplay();

        // 3. RAM
        if (GetPerformanceInfo(out var info, PerformanceInfoSize))
        {
            ulong pageSize = info.PageSize.ToUInt64();
            double total = (info.PhysicalTotal.ToUInt64() * pageSize) / (1024.0 * 1024 * 1024);
            double avail = (info.PhysicalAvailable.ToUInt64() * pageSize) / (1024.0 * 1024 * 1024);
            double used = total - avail;

            RamTotalText = $"{total:F1} GB";
            RamUsedText = $"{used:F1} GB";
            RamPercent = (int)Math.Round((used / total) * 100);
            RamPercentText = $"{RamPercent}%";
        }

        // 4. CPU (real load)
        CpuPercentText = $"{MeasureCpuLoad():F0}%";

        // 6. Battery
        var bat = App.BatteryService.CurrentState;
        BatteryPercentText = $"{bat.ChargePercent}%";
        BatteryStatusText = bat.IsCharging ? "Charging" : "Discharging";
        BatteryChargingVisibility = bat.IsCharging ? Visibility.Visible : Visibility.Collapsed;
        
        // Fluent battery level icons based on percentage
        if (bat.ChargePercent > 90) BatteryIconKind = AppIconKind.Battery10;
        else if (bat.ChargePercent > 70) BatteryIconKind = AppIconKind.Battery9;
        else if (bat.ChargePercent > 50) BatteryIconKind = AppIconKind.Battery8;
        else if (bat.ChargePercent > 30) BatteryIconKind = AppIconKind.Battery7;
        else BatteryIconKind = AppIconKind.Battery6;


        // 7. Storage
        try
        {
            // PASS 21: the selected drive comes from the central AppSettings
            // (Settings → System Monitor → Select drive), defaulting to C. Rebuild
            // the cached DriveInfo only when the selection changes.
            string driveLetter = Models.AppSettings.SelectedDrive;
            if (_statsDrive == null || _statsDriveName != driveLetter)
            {
                _statsDrive = new System.IO.DriveInfo(driveLetter);
                _statsDriveName = driveLetter;
            }
            double totalGB = _statsDrive.TotalSize / (1024.0 * 1024 * 1024);
            double freeGB = _statsDrive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            double usedGB = totalGB - freeGB;

            StorageTotalText = $"{totalGB:F0} GB";
            StorageFreeText = $"{freeGB:F0} GB";
            StoragePercent = (int)Math.Round((usedGB / totalGB) * 100);
            StoragePercentText = $"{StoragePercent}%";
        }
        catch { }

        // PASS 20: network throughput — read the service's 1 s cached state
        // (the service keeps it fresh; no per-tick interface enumeration here).
        var net = App.NetworkService.CurrentState;
        NetworkDownloadText = FormatNetworkRate(net.DownloadBytesPerSecond);
        NetworkUploadText = FormatNetworkRate(net.UploadBytesPerSecond);

        // 6. Live clipboard
        var clip = App.ClipboardService.CurrentItem;
        if (clip != null)
        {
            LiveClipboardTitle = clip.Title;
        }
        else
        {
            LiveClipboardTitle = "—";
        }

        // 7. Sync external volume/mute changes (cached state — the 150 ms poll keeps
        // it fresh; avoid extra COM calls into the audio endpoint here).
        int currentVol = App.VolumeService.CurrentState.VolumePercent;
        if (currentVol != _lastVol)
        {
            _lastVol = currentVol;
            OnPropertyChanged(nameof(VolumePercentValue));
        }
        bool currentMuted = App.VolumeService.CurrentState.IsMuted;
        if (currentMuted != _lastMuted)
        {
            _lastMuted = currentMuted;
            OnPropertyChanged(nameof(VolumeIconKind));
        }
    }

    // ── Real playback timeline ─────────────────────────────────────────────

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int totalSeconds = (int)t.TotalSeconds;
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    private void UpdatePlaybackDisplay()
    {
        var vm = MediaViewModel;
        TimeSpan pos = vm.Position;
        if (vm.IsPlaying)
        {
            var elapsed = DateTimeOffset.Now - vm.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero) pos += elapsed;
            if (vm.Duration > TimeSpan.Zero && pos > vm.Duration) pos = vm.Duration;
        }

        CurrentPlaybackTimeText = FormatTime(pos);
        TotalPlaybackTimeText = FormatTime(vm.Duration);

        if (_isSeekDragging) return;

        // Raise only when the slider value actually moved (it is static while
        // paused) — identical per-second raises are wasted binding traffic.
        double progress = vm.Duration <= TimeSpan.Zero
            ? 0
            : (pos.TotalSeconds / vm.Duration.TotalSeconds) * 100.0;
        if (Math.Abs(progress - _lastPlaybackProgress) < 0.01) return;
        _lastPlaybackProgress = progress;
        OnPropertyChanged(nameof(PlaybackProgressValue));
    }

    // ── Seek slider drag handling ──────────────────────────────────────────

    private bool _isSeekDragging;
    private bool _seekNeedsSeek;

    private void PlaybackSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isSeekDragging = true;
    }

    private void PlaybackSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndSeekDrag();
    }

    private void EndSeekDrag()
    {
        if (_seekNeedsSeek && PlaybackSlider != null)
        {
            var vm = MediaViewModel;
            if (vm.Duration > TimeSpan.Zero)
            {
                var pos = TimeSpan.FromSeconds(PlaybackSlider.Value / 100.0 * vm.Duration.TotalSeconds);
                vm.SeekCommand.Execute(pos);
            }
        }
        _isSeekDragging = false;
        _seekNeedsSeek = false;
    }

    private void PlaybackSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var vm = MediaViewModel;
        if (vm.Duration > TimeSpan.Zero)
        {
            var pos = TimeSpan.FromSeconds(e.NewValue / 100.0 * vm.Duration.TotalSeconds);
            CurrentPlaybackTimeText = FormatTime(pos);
        }
        _seekNeedsSeek = true;

        // PASS 5: the visible Azure fill mirrors the slider (Value is always 0-100).
        // Fires for both programmatic position updates and user drags, so the bar
        // always matches the slider exactly.
        if (PlaybackFillScale != null)
        {
            PlaybackFillScale.ScaleX = Math.Clamp(e.NewValue / 100.0, 0, 1);
        }
    }

    private void UpdateRepeatVisual()
    {
        if (RepeatButton == null) return;
        string key = MediaViewModel.IsRepeatActive ? "AccentBrush" : "TextSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            RepeatButton.Foreground = brush;
        }
    }

    // ── Real CPU load (GetSystemTimes) ─────────────────────────────────────

    private long _prevCpuIdle, _prevCpuKernel, _prevCpuUser;
    private double _lastCpuLoad;

    private double MeasureCpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return _lastCpuLoad;
        long idleNow = idle.ToLong(), kernelNow = kernel.ToLong(), userNow = user.ToLong();
        if (_prevCpuIdle == 0)
        {
            _prevCpuIdle = idleNow; _prevCpuKernel = kernelNow; _prevCpuUser = userNow;
            return 0;
        }
        long totalDelta = (kernelNow - _prevCpuKernel) + (userNow - _prevCpuUser);
        long idleDelta = idleNow - _prevCpuIdle;
        _prevCpuIdle = idleNow; _prevCpuKernel = kernelNow; _prevCpuUser = userNow;
        _lastCpuLoad = totalDelta <= 0 ? _lastCpuLoad
                        : Math.Clamp(100.0 * (1.0 - (double)idleDelta / totalDelta), 0, 100);
        return _lastCpuLoad;
    }

    // ── Clipboard history UI ───────────────────────────────────────────────

    private bool _showPinnedOnly;
    private string _searchText = "";
    private (ClipboardItem Item, TranslateTransform Transform, Button Strip)? _revealedItem;

    // Number of clipboard row containers currently realized by the virtualizing
    // ItemsRepeater (Pass 5 profiling: 470 data items must NOT mean 470 UI
    // containers — the repeater realizes only visible rows).

    private void RefreshClipboardFilter()
    {
        Logger.Info($"[PROFILE] RefreshClipboardFilter start ms={Environment.TickCount64} items={App.ClipboardService.History.Count}");
        _revealedItem = null;
        ClipboardItems.Clear();
        string query = _searchText;
        foreach (var item in App.ClipboardService.History)
        {
            if (_showPinnedOnly && !item.IsPinned) continue;
            if (!string.IsNullOrEmpty(query) && !MatchesSearch(item, query)) continue;
            ClipboardItems.Add(item);
        }
        UpdateClipboardEmptyState();
        Logger.Info($"[PROFILE] RefreshClipboardFilter end ms={Environment.TickCount64} added={ClipboardItems.Count} realized={_realizedClipCount}");
    }

    // ── Clipboard list virtualization (Pass 5) ─────────────────────────────

    private int _realizedClipCount;

    // Pass 16: first non-zero layout observed (dashboard is constructed at
    // preload but only measured on first expand).
    private bool _p16LayoutLogged;

    private void ClipboardRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        _realizedClipCount++;

        // ItemsRepeater does NOT assign DataContext to realized template roots the
        // way ItemsControl does (its ContentPresenter wrapper set DataContext per
        // item). x:Bind still renders correctly because compiled bindings receive
        // the data item via the generated LoadData path — but handlers resolving
        // the item through sender.DataContext (pin / more / delete / row tap)
        // silently failed after the Pass 5 ItemsControl→ItemsRepeater migration.
        // Restore the item contract explicitly on prepare (recycled containers
        // re-fire this with their new index, so DataContext stays current).
        if (args.Element is FrameworkElement fe
            && args.Index >= 0 && args.Index < ClipboardItems.Count)
        {
            fe.DataContext = ClipboardItems[args.Index];
        }
    }

    /// <summary>
    /// A row container is being recycled (scrolled out of view). Reset its
    /// reveal-strip visuals so the recycled container never carries an open
    /// strip onto a different item, and drop reveal tracking if the cleared
    /// element was the revealed row.
    /// </summary>
    private void ClipboardRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (_realizedClipCount > 0) _realizedClipCount--;
        if (args.Element is not FrameworkElement root) return;

        if (root is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is Button strip) strip.Opacity = 0;
                if (child is Border { Tag: "ClipboardFrontCard" } front
                    && front.RenderTransform is TranslateTransform t)
                {
                    t.X = 0;
                    if (_revealedItem is { } revealed && ReferenceEquals(revealed.Transform, t))
                        _revealedItem = null;
                }
            }
        }
    }

    /// <summary>
    /// First layout-ready marker: after the dashboard's first real layout pass
    /// this reports how many of the clipboard rows actually got realized — the
    /// Pass 5 success metric (should be ~visible rows, not 470).
    /// </summary>
    private void ExpandedDashboard_Loaded(object sender, RoutedEventArgs e)
    {
        Logger.Info($"[PROFILE-CLIP] first layout ready: realized={_realizedClipCount} containers of {ClipboardItems.Count} items");
        MotionDiagnostics.P16Mark("DashboardLoaded", "dashboard", $"realized={_realizedClipCount} items={ClipboardItems.Count}");
    }

    /// <summary>
    /// Recomputes the empty-state text + visibility from the current filter, search,
    /// and live ClipboardItems count. Shared by full rebuilds and incremental mutations.
    /// </summary>
    private void UpdateClipboardEmptyState()
    {
        if (!string.IsNullOrEmpty(_searchText))
        {
            ClipboardEmptyText.Text = "No matching items";
        }
        else if (_showPinnedOnly)
        {
            ClipboardEmptyText.Text = App.ClipboardService.History.Count == 0 ? "Nothing copied yet" : "No pinned items";
        }
        else
        {
            ClipboardEmptyText.Text = "Nothing copied yet";
        }
        ClipboardEmptyVisibility = ClipboardItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Animates any open reveal strip closed and drops the tracking reference.
    /// Incremental mutations keep row containers alive, so the strip must be closed
    /// explicitly (a full rebuild discarded the old containers for free).
    /// </summary>
    private void CloseRevealedStrip()
    {
        if (_revealedItem is not { } revealed) return;
        AnimateFrontCard(revealed.Transform, revealed.Strip, 0, 0);
        _revealedItem = null;
    }

    /// <summary>
    /// 0-based index the given item would occupy in the filtered ClipboardItems view,
    /// derived from its History position and the active Pinned filter + search query.
    /// Returns -1 when the item does not belong in this view.
    /// </summary>
    private int FilteredPosition(ClipboardItem item)
    {
        if (_showPinnedOnly && !item.IsPinned) return -1;
        if (!string.IsNullOrEmpty(_searchText) && !MatchesSearch(item, _searchText)) return -1;

        int historyIndex = App.ClipboardService.History.IndexOf(item);
        if (historyIndex < 0) return -1;

        int position = 0;
        for (int i = 0; i < historyIndex; i++)
        {
            var candidate = App.ClipboardService.History[i];
            if (_showPinnedOnly && !candidate.IsPinned) continue;
            if (!string.IsNullOrEmpty(_searchText) && !MatchesSearch(candidate, _searchText)) continue;
            position++;
        }
        return position;
    }

    /// <summary>
    /// Inserts a single item into the filtered view at its correct position.
    /// No-op when the item is filtered out by the current Pinned/search state.
    /// </summary>
    private void InsertClipboardItemIncremental(ClipboardItem item)
    {
        int position = FilteredPosition(item);
        if (position < 0) return;

        CloseRevealedStrip();
        ClipboardItems.Insert(Math.Min(position, ClipboardItems.Count), item);
        UpdateClipboardEmptyState();
    }

    /// <summary>
    /// Removes a single item from the filtered view. No-op when not currently shown.
    /// </summary>
    private void RemoveClipboardItemIncremental(ClipboardItem item)
    {
        int index = ClipboardItems.IndexOf(item);
        if (index < 0) return;

        if (_revealedItem is { Item: { } revealed } && ReferenceEquals(revealed, item))
            _revealedItem = null;

        ClipboardItems.RemoveAt(index);
        UpdateClipboardEmptyState();
    }

    /// <summary>
    /// Repositions a single item after a History.Move (re-copy to top), mirroring
    /// the move in the filtered view without rebuilding the whole list.
    /// </summary>
    private void MoveClipboardItemIncremental(ClipboardItem item)
    {
        int currentIndex = ClipboardItems.IndexOf(item);
        if (currentIndex < 0)
        {
            InsertClipboardItemIncremental(item);
            return;
        }

        int target = FilteredPosition(item);
        if (target < 0)
        {
            RemoveClipboardItemIncremental(item);
            return;
        }

        if (target == currentIndex) return;

        CloseRevealedStrip();
        ClipboardItems.Move(currentIndex, Math.Min(target, ClipboardItems.Count - 1));
        UpdateClipboardEmptyState();
    }

    /// <summary>
    /// Applies a single History collection change to the dashboard list incrementally:
    /// Add → one insert, Remove → one removal, Move → one reposition. Full rebuilds
    /// (RefreshClipboardFilter) are reserved for filter/search changes and initial
    /// population, so ordinary clipboard mutations never tear down the realized list.
    ///
    /// Deltas resolve their positions against LIVE History/ClipboardItems state when
    /// they run (not the args snapshot), so a rapid capture→pin→delete batch enqueued
    /// before any delta executes still composes to the correct final list — each
    /// pending delta re-derives indices at dequeue time.
    /// </summary>
    private void ApplyHistoryChange(NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems?[0] is ClipboardItem added)
                    InsertClipboardItemIncremental(added);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems?[0] is ClipboardItem removed)
                    RemoveClipboardItemIncremental(removed);
                break;
            case NotifyCollectionChangedAction.Move:
                if (e.OldItems?[0] is ClipboardItem moved)
                    MoveClipboardItemIncremental(moved);
                break;
            default:
                RefreshClipboardFilter();
                break;
        }
    }

    /// <summary>
    /// Case-insensitive substring match across the item's title, full text content,
    /// and detail line (file names for file copies).
    /// </summary>
    private static bool MatchesSearch(ClipboardItem item, string query)
    {
        if (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(item.RawText) && item.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // ── Clipboard search ───────────────────────────────────────────────────

    private void ClipboardSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = ClipboardSearchBox.Text.Trim();
        ClipboardSearchClearButton.Visibility = string.IsNullOrEmpty(_searchText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        RefreshClipboardFilter();
    }

    private void ClipboardSearchClear_Click(object sender, RoutedEventArgs e)
    {
        ClipboardSearchBox.Text = "";
        ClipboardSearchBox.Focus(FocusState.Programmatic);
    }

    // ── Clipboard scrollbar thumb (PASS 6.1) ───────────────────────────────

    private SolidColorBrush? _clipScrollThumbRestBrush;
    private SolidColorBrush? _clipScrollThumbActiveBrush;

    /// <summary>
    /// PASS 6.1: Azure accent on the Halo scrollbar thumb while hovered/dragged.
    /// The thumb's Border binds its Background to Thumb.Background via
    /// TemplateBinding, so swapping the property re-tints the visual instantly.
    /// </summary>
    private void SetClipScrollThumbActive(object sender, bool active)
    {
        if (sender is not Thumb thumb) return;
        if (active)
        {
            _clipScrollThumbActiveBrush ??= Application.Current.Resources["AccentBrush"] as SolidColorBrush;
            if (_clipScrollThumbActiveBrush != null) thumb.Background = _clipScrollThumbActiveBrush;
        }
        else
        {
            _clipScrollThumbRestBrush ??= Application.Current.Resources["Semantic.State.Muted"] as SolidColorBrush;
            if (_clipScrollThumbRestBrush != null) thumb.Background = _clipScrollThumbRestBrush;
        }
    }

    private void ClipboardScrollThumb_PointerEntered(object sender, PointerRoutedEventArgs e) => SetClipScrollThumbActive(sender, true);
    private void ClipboardScrollThumb_PointerExited(object sender, PointerRoutedEventArgs e) => SetClipScrollThumbActive(sender, false);
    private void ClipboardScrollThumb_PointerPressed(object sender, PointerRoutedEventArgs e) => SetClipScrollThumbActive(sender, true);
    private void ClipboardScrollThumb_PointerReleased(object sender, PointerRoutedEventArgs e) => SetClipScrollThumbActive(sender, false);
    private void ClipboardScrollThumb_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => SetClipScrollThumbActive(sender, false);

    // ── Clipboard retention ────────────────────────────────────────────────

    /// <summary>
    /// PASS 21: reflects the persisted footer-metric visibility (CPU/RAM/DISK/
    /// network/weather) from the central AppSettings. Fires once at construction
    /// and on every AppSettings.Changed so the Settings page updates the footer
    /// live. Weather visibility only applies to the footer readout — the pill
    /// weather card is unaffected.
    /// </summary>
    private void ApplyFooterSettings()
    {
        if (RamFooterGroup == null || CpuFooterGroup == null || DiskFooterGroup == null || NetworkFooterGroup == null || WeatherFooterText == null)
        {
            return;
        }

        RamFooterGroup.Visibility = Models.AppSettings.ShowRam ? Visibility.Visible : Visibility.Collapsed;
        CpuFooterGroup.Visibility = Models.AppSettings.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
        DiskFooterGroup.Visibility = Models.AppSettings.ShowDisk ? Visibility.Visible : Visibility.Collapsed;
        NetworkFooterGroup.Visibility = Models.AppSettings.ShowNetworkSpeed ? Visibility.Visible : Visibility.Collapsed;
        WeatherFooterText.Visibility = Models.AppSettings.ShowWeather ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// PASS 21: the auto-delete retention dropdown moved to the Settings page
    /// (SettingsPanel AutoDeleteCombo → AppSettings.SetClipboardAutoDelete →
    /// ClipboardService.SetRetentionDays). The dashboard no longer exposes it.
    /// </summary>
    private void OnClipboardHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() => ApplyHistoryChange(e));
    }

    private void AllFilter_Click(object sender, RoutedEventArgs e)
    {
        _showPinnedOnly = false;
        UpdateFilterVisual();
        RefreshClipboardFilter();
    }

    private void PinnedFilter_Click(object sender, RoutedEventArgs e)
    {
        _showPinnedOnly = true;
        UpdateFilterVisual();
        RefreshClipboardFilter();
    }

    private void UpdateFilterVisual()
    {
        AnimatePillBrush(AllFilterButton, GetThemeBrush(_showPinnedOnly ? "TextSecondaryBrush" : "AccentBrush"),
            _showPinnedOnly ? null : GetAccentTintBrush());
        AnimatePillBrush(PinnedFilterButton, GetThemeBrush(_showPinnedOnly ? "AccentBrush" : "TextSecondaryBrush"),
            _showPinnedOnly ? GetAccentTintBrush() : null);
        AllFilterButton.FontWeight = _showPinnedOnly ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Bold;
        PinnedFilterButton.FontWeight = _showPinnedOnly ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
    }

    /// <summary>
    /// 120 ms foreground/background color transitions for the filter pills so
    /// the active state glides between pills instead of snapping.
    /// </summary>
    private void AnimatePillBrush(Button button, Brush? foreground, Brush? background)
    {
        var sb = new Storyboard();

        if (foreground is SolidColorBrush fg)
        {
            var anim = new ColorAnimation
            {
                To = fg.Color,
                Duration = new Duration(TimeSpan.FromMilliseconds(120))
            };
            Storyboard.SetTarget(anim, button);
            Storyboard.SetTargetProperty(anim, "(Control.Foreground).(SolidColorBrush.Color)");
            sb.Children.Add(anim);
        }
        else
        {
            button.Foreground = foreground;
        }

        if (background is SolidColorBrush bg)
        {
            var anim = new ColorAnimation
            {
                To = bg.Color,
                Duration = new Duration(TimeSpan.FromMilliseconds(120))
            };
            Storyboard.SetTarget(anim, button);
            Storyboard.SetTargetProperty(anim, "(Control.Background).(SolidColorBrush.Color)");
            sb.Children.Add(anim);
        }
        else
        {
            button.Background = background;
        }

        sb.Begin();
    }

    /// <summary>
    /// A soft accent-tinted chip background for the active filter pill
    /// (~12 % accent alpha over the 5 % chip surface).
    /// </summary>
    private static Brush? GetAccentTintBrush()
    {
        if (Application.Current.Resources.TryGetValue("AccentBrush", out var value) &&
            value is SolidColorBrush accent)
        {
            return new SolidColorBrush(Color.FromArgb(0x1F, accent.Color.R, accent.Color.G, accent.Color.B));
        }
        return null;
    }

    private static Brush GetThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    // ── Item-row hover (Bluetooth + clipboard rows) ─────────────────────────
    // Rows rest on Semantic.Surface.ClipItem (~5% white); hovering brightens
    // them to ClipItemHover (~6%) — a restrained +1% lift, no elevation edge.
    // The card containers themselves stay static (EnableHover=false).
    private void ItemRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = GetThemeBrush("Semantic.Surface.ClipItemHover");
    }

    private void ItemRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = GetThemeBrush("Semantic.Surface.ClipItem");
    }

    private void ClipboardItemCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_revealedItem is { } revealed)
        {
            AnimateFrontCard(revealed.Transform, revealed.Strip, 0, 0);
            _revealedItem = null;
        }

        if ((sender as FrameworkElement)?.DataContext is ClipboardItem item)
        {
            App.ClipboardService.ReCopy(item);
        }
    }

    private void ClipboardAction_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void ClipboardPin_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info($"[PROFILE] ClipboardPin_Click start ms={Environment.TickCount64}");
        try
        {
            if ((sender as FrameworkElement)?.DataContext is ClipboardItem item)
            {
                App.ClipboardService.TogglePin(item);

                // Pinned filter: membership follows pin state — move the single row in/out.
                // All filter: the row's x:Bind OneWay bindings on IsPinned update the icon
                // in place (INPC); no collection change is needed.
                if (_showPinnedOnly)
                {
                    if (item.IsPinned) InsertClipboardItemIncremental(item);
                    else RemoveClipboardItemIncremental(item);
                }
                else
                {
                    // Preserve legacy behavior: pinning while a reveal strip is open
                    // closes it (the old full rebuild reset _revealedItem).
                    CloseRevealedStrip();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardPin_Click exception", ex);
        }
        Logger.Info($"[PROFILE] ClipboardPin_Click end ms={Environment.TickCount64}");
    }

    private void ClipboardMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ClipboardItem item) return;
        if (GetRevealTargets(fe) is not { } targets) return;

        // Tapping "…" on the already-revealed item closes it.
        if (_revealedItem is { Item: { } revealedItem } && revealedItem == item)
        {
            AnimateFrontCard(targets.Transform, targets.Strip, 0, 0);
            _revealedItem = null;
            return;
        }

        // Close any other open card first (only one may be revealed at a time).
        if (_revealedItem is { } previous)
        {
            AnimateFrontCard(previous.Transform, previous.Strip, 0, 0);
        }

        AnimateFrontCard(targets.Transform, targets.Strip, -DeleteStripWidth, 1);
        _revealedItem = (item, targets.Transform, targets.Strip);
    }

    private void ClipboardRevealedDelete_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info($"[PROFILE] ClipboardRevealedDelete_Click start ms={Environment.TickCount64}");
        if ((sender as FrameworkElement)?.DataContext is ClipboardItem item)
        {
            App.ClipboardService.DeleteItem(item);
        }
        _revealedItem = null;
        Logger.Info($"[PROFILE] ClipboardRevealedDelete_Click end ms={Environment.TickCount64}");
    }

    private const double DeleteStripWidth = 56.0;

    private static (TranslateTransform Transform, Button Strip)? GetRevealTargets(FrameworkElement element)
    {
        DependencyObject current = element;
        while (current is not null)
        {
            if (current is Border border && border.Tag as string == "ClipboardFrontCard"
                && border.RenderTransform is TranslateTransform transform
                && VisualTreeHelper.GetParent(border) is Grid root)
            {
                // The delete strip is the only Button sibling of the front card Border in the template root.
                if (root.Children.OfType<Button>().FirstOrDefault() is Button strip)
                {
                    return (transform, strip);
                }
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void AnimateFrontCard(TranslateTransform transform, Button strip, double toX, double toOpacity)
    {
        var animation = new DoubleAnimation
        {
            To = toX,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");

        var stripAnimation = new DoubleAnimation
        {
            To = toOpacity,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(stripAnimation, strip);
        Storyboard.SetTargetProperty(stripAnimation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Children.Add(stripAnimation);
        storyboard.Begin();
    }

    // ── Bluetooth list refresh ─────────────────────────────────────────────

    private void OnBluetoothUpdated(object? sender, EventArgs e)
        => _dispatcherQueue.TryEnqueue(RefreshBluetoothList);

    /// <summary>
    /// Mirrors the service's device snapshot through the active CONNECTED/AVAILABLE
    /// filter and derives the honest empty-state message from the adapter status
    /// (no adapter / off / scanning / nothing matching the filter) instead of
    /// rendering a blank list.
    /// </summary>
    private void RefreshBluetoothList()
    {
        var service = App.BluetoothService;

        BluetoothItems.Clear();
        foreach (var device in service.Devices)
        {
            if (_bluetoothShowConnectedOnly && !device.IsConnected) continue;
            if (!_bluetoothShowConnectedOnly && device.IsConnected) continue;
            BluetoothItems.Add(device);
        }

        bool hasDevices = BluetoothItems.Count > 0;
        BluetoothListVisibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
        BluetoothEmptyVisibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;

        BluetoothEmptyText.Text = service.AdapterStatus switch
        {
            BluetoothAdapterStatus.NoAdapter => "No Bluetooth adapter",
            BluetoothAdapterStatus.Disabled => "Bluetooth is off",
            BluetoothAdapterStatus.Initializing => "Scanning…",
            // Adapter is Ready — an empty result here means nothing matched the filter.
            _ => _bluetoothShowConnectedOnly ? "No connected devices" : "No available devices",
        };
    }

    // ── Bluetooth CONNECTED/AVAILABLE filter (PASS 7) ───────────────────────

    private void BluetoothConnectedFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_bluetoothShowConnectedOnly) return;
        _bluetoothShowConnectedOnly = true;
        UpdateBluetoothFilterVisual();
        RefreshBluetoothList();
    }

    private void BluetoothAvailableFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!_bluetoothShowConnectedOnly) return;
        _bluetoothShowConnectedOnly = false;
        UpdateBluetoothFilterVisual();
        RefreshBluetoothList();
    }

    /// <summary>PASS 7: Azure + bold for the active filter pill (clipboard All/Pinned pattern).</summary>
    private void UpdateBluetoothFilterVisual()
    {
        BluetoothConnectedFilterButton.Foreground = GetThemeBrush(_bluetoothShowConnectedOnly ? "AccentBrush" : "TextSecondaryBrush");
        BluetoothAvailableFilterButton.Foreground = GetThemeBrush(_bluetoothShowConnectedOnly ? "TextSecondaryBrush" : "AccentBrush");
        BluetoothConnectedFilterButton.FontWeight = _bluetoothShowConnectedOnly ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        BluetoothAvailableFilterButton.FontWeight = _bluetoothShowConnectedOnly ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Bold;
    }

    // ── Mute toggle click ──────────────────────────────────────────────────

    private void MusicCard_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        int delta = e.GetCurrentPoint(MusicCardBorder).Properties.MouseWheelDelta;
        int current = App.VolumeService.CurrentState.VolumePercent;
        App.VolumeService.SetVolume(Math.Clamp(current + (delta > 0 ? 3 : -3), 0, 100));
        OnPropertyChanged(nameof(VolumePercentValue));
        OnPropertyChanged(nameof(VolumeIconKind));
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // PASS 5: the visible volume fill mirrors the volume slider (0-100), which
        // stays fully independent of playback progress.
        if (VolumeFillScale != null)
        {
            VolumeFillScale.ScaleX = Math.Clamp(e.NewValue / 100.0, 0, 1);
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        var current = App.VolumeService.ReadCurrentState();
        App.VolumeService.SetMute(!current.IsMuted);
        OnPropertyChanged(nameof(VolumeIconKind));
    }

    // ── Header Home (collapse back to the compact pill) ───────────────────

    private void HomeButton_Click(object sender, RoutedEventArgs e)
        => App.IslandController.CollapseToPill();

    // ── Footer settings gear (PASS 21: shows the Settings page overlay) ─────

    /// <summary>
    /// Hover raise for the gear chip: 100 ms background glide from the 2 % tint
    /// to the 8 % raised surface so the chip signals it's clickable.
    /// </summary>
    private void AnimateGearChip(Brush to)
    {
        if (SettingsGearChip.Background is not SolidColorBrush from)
        {
            SettingsGearChip.Background = to;
            return;
        }

        var anim = new ColorAnimation
        {
            To = ((SolidColorBrush)to).Color,
            Duration = new Duration(TimeSpan.FromMilliseconds(100))
        };
        Storyboard.SetTarget(anim, SettingsGearChip);
        Storyboard.SetTargetProperty(anim, "(Border.Background).(SolidColorBrush.Color)");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void SettingsGearChip_PointerEntered(object sender, PointerRoutedEventArgs e)
        => AnimateGearChip((Brush)Application.Current.Resources["Semantic.Surface.Raised"]);

    private void SettingsGearChip_PointerExited(object sender, PointerRoutedEventArgs e)
        => AnimateGearChip((Brush)Application.Current.Resources["Semantic.Surface.Raised05"]);

    private void SettingsGear_Click(object sender, RoutedEventArgs e)
    {
        // Keep the island awake while the Settings page is open so pointer
        // movement inside it doesn't auto-collapse the expanded dashboard.
        App.IslandController.BeginAwake();
        MainLayoutGrid.Visibility = Visibility.Collapsed;
        HeaderBar.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        AnimateSettingsPageIn();
    }

    /// <summary>
    /// Settings page opens with a 200 ms fade + 12 px slide-up so the overlay
    /// reads as a page gliding over the dashboard rather than a hard cut.
    /// </summary>
    private void AnimateSettingsPageIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new TranslateTransform { Y = 12 };
        SettingsPage.RenderTransform = slide;
        SettingsPage.Opacity = 0.0;

        var fade = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EnableDependentAnimation = true
        };
        fade.EasingFunction = ease;
        Storyboard.SetTarget(fade, SettingsPage);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var translate = new DoubleAnimation
        {
            From = 12,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EnableDependentAnimation = true
        };
        translate.EasingFunction = ease;
        Storyboard.SetTarget(translate, slide);
        Storyboard.SetTargetProperty(translate, "Y");

        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(translate);
        sb.Begin();
    }

    private void SettingsPage_BackRequested(object sender, EventArgs e)
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        MainLayoutGrid.Visibility = Visibility.Visible;
        HeaderBar.Visibility = Visibility.Visible;
        App.IslandController.EndAwake();
    }

    private void OnIslandExpandedChanged(object? sender, bool expanded)
    {
        if (!expanded && SettingsPage.Visibility == Visibility.Visible)
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            MainLayoutGrid.Visibility = Visibility.Visible;
            HeaderBar.Visibility = Visibility.Visible;
            App.IslandController.EndAwake();
        }
    }

    // ── Focus Session click handlers ───────────────────────────────────────

    private void FocusPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_focusIsRunning)
        {
            _focusIsRunning = false;                       // Pause
        }
        else if (_focusSecondsRemaining > 0)
        {
            _focusIsRunning = true;                        // Resume / Start
            _focusSessionActive = true;                    // pill ring stays/returns
            SetFocusCompleted(false);
        }
        else
        {
            _focusSecondsRemaining = FocusTotalSeconds;    // Completed -> fresh session
            FocusPillRotate.Angle = 0;
            _focusIsRunning = true;
            _focusSessionActive = true;
            SetFocusCompleted(false);
        }
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusProgressFraction));
        OnPropertyChanged(nameof(FocusPlayPauseIconKind));
        PublishFocusState();
    }

    private void FocusReset_Click(object sender, RoutedEventArgs e)
    {
        _focusIsRunning = false;
        _focusSessionActive = false; // reset cancels the session — pill ring hides
        SetFocusCompleted(false);
        _focusSecondsRemaining = FocusTotalSeconds; // Reset to the selected session's duration
        FocusPillRotate.Angle = 0;
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusPlayPauseIconKind));
        OnPropertyChanged(nameof(FocusProgressFraction));
        PublishFocusState();
    }

    // ── Focus Session ring drag handlers ───────────────────────────────────

    // ── Shared duration conversion core (single source of truth) ───────────
    // Used by both the ring (fraction ↔ angle) and the settings H/M/S boxes
    // (fraction ↔ decomposed duration). Both funnel into ApplyDurationSeconds.
    // PASS: the ring range is 15-120 minutes in 15-minute steps (15, 30, 45,
    // 60, 75, 90, 105, 120) — a full ring drag snaps through the eight presets
    // for a quick drag-and-start.
    private const int FocusMinDurationMinutes = 15;
    private const int FocusMaxDurationMinutes = 120;
    private const int FocusDurationStepMinutes = 15;

    private static double DurationToFraction(int seconds) =>
        Math.Clamp(((double)seconds / 60.0 - FocusMinDurationMinutes)
            / (FocusMaxDurationMinutes - FocusMinDurationMinutes), 0, 1);

    private static int FractionToDurationSeconds(double fraction)
    {
        int steps = (FocusMaxDurationMinutes - FocusMinDurationMinutes) / FocusDurationStepMinutes;
        int step = (int)Math.Round(Math.Clamp(fraction, 0, 1) * steps);
        return (FocusMinDurationMinutes + step * FocusDurationStepMinutes) * 60;
    }

    private void ApplyDurationSeconds(FocusSession session, int seconds)
    {
        session.DurationSeconds = seconds;
        _focusSecondsRemaining = seconds;
        SetFocusCompleted(false); // a new duration means a fresh (not-done) session
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusDurationText));
        OnPropertyChanged(nameof(FocusProgressFraction));
        PublishFocusState();
    }

    /// <summary>
    /// Position of the current session's duration within the 1-1440 minute range,
    /// as a 0-1 fraction. Used as the drag's starting point on press.
    /// </summary>
    private double CurrentDurationFraction => DurationToFraction(_focusSessions[_selectedFocusSessionIndex].DurationSeconds);

    /// <summary>
    /// Pointer angle in [0, 2π) measured clockwise from 12 o'clock, matching the
    /// arc converter's convention (fraction = angle / 2π).
    /// </summary>
    private static double AngleFromFocusRingPoint(Windows.Foundation.Point p)
    {
        double dx = p.X - FocusRingCenter;
        double dy = p.Y - FocusRingCenter;
        double angle = Math.Atan2(dx, -dy);
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

    /// <summary>
    /// Hover/drag emphasis for the pointer knob: it grows slightly when the cursor
    /// is near the arc tip (or while dragging) so it reads as attached to the arc
    /// end. Proximity is measured against the tip of the currently rendered arc.
    /// </summary>
    private void UpdateFocusPillEmphasis(Windows.Foundation.Point position)
    {
        double angle = FocusPillRotate.Angle * Math.PI / 180.0;
        double tipX = FocusRingCenter + FocusRingRadius * Math.Sin(angle);
        double tipY = FocusRingCenter - FocusRingRadius * Math.Cos(angle);
        double dx = position.X - tipX;
        double dy = position.Y - tipY;
        bool nearTip = (dx * dx + dy * dy) <= 24 * 24;
        if (nearTip == _isFocusPillHovered) return;

        _isFocusPillHovered = nearTip;
        double scale = nearTip || _isFocusRingDragging ? 1.15 : 1.0;
        FocusPillScale.ScaleX = scale;
        FocusPillScale.ScaleY = scale;
    }

    private void FocusRing_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Drag is disabled entirely while running: no capture, no state, no response.
        if (_focusIsRunning) return;

        _dragAccumulatedFraction = CurrentDurationFraction;
        _dragLastAngle = AngleFromFocusRingPoint(e.GetCurrentPoint(FocusRingGrid).Position);
        FocusPillRotate.Angle = _dragAccumulatedFraction * 360;
        _isFocusRingDragging = true;
        FocusPillScale.ScaleX = FocusPillScale.ScaleY = 1.15;
        FocusRingDragSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void FocusRing_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(FocusRingGrid).Position;
        if (!_isFocusRingDragging)
        {
            // Not dragging: only the knob's hover emphasis changes.
            UpdateFocusPillEmphasis(position);
            return;
        }

        double theta = AngleFromFocusRingPoint(position);

        // Shortest-path delta across the 0/2π seam: crossing the top yields a small
        // delta, never a ±2π jump that would teleport the handle to the far end.
        double rawDelta = theta - _dragLastAngle;
        double delta = rawDelta;
        if (rawDelta > Math.PI) delta = rawDelta - 2 * Math.PI;
        else if (rawDelta < -Math.PI) delta = rawDelta + 2 * Math.PI;

        _dragAccumulatedFraction = Math.Clamp(_dragAccumulatedFraction + delta / (2 * Math.PI), 0, 1);
        _dragLastAngle = theta;

        // Live preview while dragging (no disk I/O): ApplyDurationSeconds updates both
        // DurationSeconds and the remaining time so FocusTimerText reflects the chosen length.
        ApplyDurationSeconds(_focusSessions[_selectedFocusSessionIndex], FractionToDurationSeconds(_dragAccumulatedFraction));

        FocusPillRotate.Angle = _dragAccumulatedFraction * 360;
    }

    private void FocusRing_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Reset knob emphasis when the cursor leaves the ring while not dragging.
        if (_isFocusRingDragging) return;
        _isFocusPillHovered = false;
        FocusPillScale.ScaleX = FocusPillScale.ScaleY = 1.0;
    }

    private void FocusRing_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndFocusRingDrag();
    }

    private void FocusRing_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndFocusRingDrag();
    }

    private void FocusRing_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndFocusRingDrag();
    }

    private void EndFocusRingDrag()
    {
        if (!_isFocusRingDragging) return;
        _isFocusRingDragging = false;
        FocusRingDragSurface.ReleasePointerCaptures();

        // Knob returns to idle size; the next PointerMoved re-arms hover if the
        // cursor is still near the tip.
        _isFocusPillHovered = false;
        FocusPillScale.ScaleX = FocusPillScale.ScaleY = 1.0;

        // Persist the chosen duration once; the pill stays at the angle the drag left
        // it, representing the just-chosen duration, until the running tick moves it.
        FocusSessionStore.SaveAll(_focusSessions);
        SetFocusCompleted(false); // dragging off 00:00 returns the ring to Azure
        OnPropertyChanged(nameof(FocusProgressFraction));
    }

    // ── Focus Session dot switcher ─────────────────────────────────────────

    private void FocusSessionDot_Click(object sender, RoutedEventArgs e)
    {
        // Same gating as the ring drag: while running, tapping a dot does nothing.
        if (_focusIsRunning) return;
        if ((sender as FrameworkElement)?.DataContext is not FocusSession session) return;

        int index = _focusSessions.IndexOf(session);
        if (index < 0 || index == _selectedFocusSessionIndex) return;

        _selectedFocusSessionIndex = index;
        _focusSecondsRemaining = FocusTotalSeconds; // Reads the newly selected session's duration.
        FocusPillRotate.Angle = 0;
        SetFocusCompleted(false);
        UpdateFocusDotsVisual();
        OnPropertyChanged(nameof(CurrentSessionName));
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusDurationText));
        OnPropertyChanged(nameof(FocusProgressFraction));
    }

    private void FocusDotsControl_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateFocusDotsVisual();
    }

    private void UpdateFocusDotsVisual()
    {
        for (int i = 0; i < FocusDotsControl.Items.Count; i++)
        {
            if (FocusDotsControl.ContainerFromIndex(i) is not { } container) continue;
            if (FindDotEllipse(container) is { } ellipse)
            {
                ellipse.Fill = i == _selectedFocusSessionIndex
                    ? GetThemeBrush("AccentBrush")
                    : GetThemeBrush("TextSecondaryBrush");
            }
        }
    }

    private static Ellipse? FindDotEllipse(DependencyObject root)
    {
        for (int j = 0; j < VisualTreeHelper.GetChildrenCount(root); j++)
        {
            var child = VisualTreeHelper.GetChild(root, j);
            if (child is Ellipse ellipse) return ellipse;
            if (FindDotEllipse(child) is { } found) return found;
        }
        return null;
    }

    // ── Focus Session settings view ────────────────────────────────────────

    private FocusSession? _focusSettingsEditingSession = null; // null = Add mode
    private string _focusSettingsDraftName = "";
    private int _focusSettingsDraftSeconds = 1500;

    public string FocusSettingsHeaderText => _focusSettingsEditingSession != null ? "Edit Session" : "New Session";

    private void FocusSettings_Click(object sender, RoutedEventArgs e)
    {
        // Same gating as the ring drag and dot switching: blocked while running.
        if (_focusIsRunning) return;

        _focusSettingsEditingSession = _focusSessions[_selectedFocusSessionIndex];
        OnPropertyChanged(nameof(FocusSettingsHeaderText));
        _focusSettingsDraftName = _focusSettingsEditingSession.Name;
        _focusSettingsDraftSeconds = _focusSettingsEditingSession.DurationSeconds;
        OpenFocusSettings();
    }

    private void FocusSessionAdd_Click(object sender, RoutedEventArgs e)
    {
        // Same gating: blocked while running.
        if (_focusIsRunning) return;

        _focusSettingsEditingSession = null;
        OnPropertyChanged(nameof(FocusSettingsHeaderText));
        _focusSettingsDraftName = "Focus";
        _focusSettingsDraftSeconds = 1500;
        OpenFocusSettings();
    }

    private void FocusSettingsValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Draft-only: update the local seconds and readout, never the live timer state.
        // The H/M/S fields are plain digit TextBoxes (no native spinners) — parse and
        // clamp to the same bounds the old NumberBoxes enforced (H 0-23, M/S 0-59).
        // PASS: sessions cap at 120 minutes — the hours field clamps to 2 so the
        // settings view honors the same bound the ring drag enforces.
        int h = ParseHmsValue(FocusSettingsHoursBox.Text, FocusMaxDurationMinutes / 60);
        int m = ParseHmsValue(FocusSettingsMinutesBox.Text, 59);
        int s = ParseHmsValue(FocusSettingsSecondsBox.Text, 59);
        _focusSettingsDraftSeconds = h * 3600 + m * 60 + s;
        UpdateSettingsReadout();
    }

    private static int ParseHmsValue(string text, int max)
    {
        if (!int.TryParse(text, out int value)) return 0;
        return Math.Clamp(value, 0, max);
    }

    private void FocusSettingsHmsBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Digits only (plus navigation/editing keys) — no letters or symbols ever reach the field.
        if (e.Key >= Windows.System.VirtualKey.Number0 && e.Key <= Windows.System.VirtualKey.Number9) return;
        if (e.Key >= Windows.System.VirtualKey.NumberPad0 && e.Key <= Windows.System.VirtualKey.NumberPad9) return;
        if (e.Key is Windows.System.VirtualKey.Back or Windows.System.VirtualKey.Tab
            or Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Left
            or Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Home
            or Windows.System.VirtualKey.End) return;
        e.Handled = true;
    }

    private void UpdateSettingsReadout()
    {
        if (FocusSettingsReadout == null) return;
        int h = _focusSettingsDraftSeconds / 3600;
        int m = (_focusSettingsDraftSeconds % 3600) / 60;
        int s = _focusSettingsDraftSeconds % 60;
        FocusSettingsReadout.Text = h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
    }

    private static void AttachTextInputFocus(Control control)
    {
        control.GotFocus += (_, _) => SetTextInputActive(true);
        control.LostFocus += (_, _) => SetTextInputActive(false);
    }

    private static void SetTextInputActive(bool active)
    {
        App.WindowService.SetTextInputActive(active);
        if (active) App.Window.Activate();
    }

    private void OpenFocusSettings()
    {
        // The delete button only applies to an existing session — hidden while
        // creating a new one.
        FocusSettingsDeleteButton.Visibility = _focusSettingsEditingSession != null
            ? Visibility.Visible
            : Visibility.Collapsed;
        FocusSettingsNameBox.Text = _focusSettingsDraftName;
        FocusSettingsHoursBox.Text = (_focusSettingsDraftSeconds / 3600).ToString();
        FocusSettingsMinutesBox.Text = ((_focusSettingsDraftSeconds % 3600) / 60).ToString();
        FocusSettingsSecondsBox.Text = (_focusSettingsDraftSeconds % 60).ToString();
        UpdateSettingsReadout();
        FocusMainHeader.Visibility = Visibility.Collapsed;
        FocusMainBody.Visibility = Visibility.Collapsed;
        FocusMainFooter.Visibility = Visibility.Collapsed;
        FocusSettingsHeader.Visibility = Visibility.Visible;
        FocusSettingsBody.Visibility = Visibility.Visible;
        FocusSettingsFooter.Visibility = Visibility.Visible;
        App.IslandController.BeginAwake();
    }

    private void CloseFocusSettings()
    {
        FocusSettingsHeader.Visibility = Visibility.Collapsed;
        FocusSettingsBody.Visibility = Visibility.Collapsed;
        FocusSettingsFooter.Visibility = Visibility.Collapsed;
        FocusMainHeader.Visibility = Visibility.Visible;
        FocusMainBody.Visibility = Visibility.Visible;
        FocusMainFooter.Visibility = Visibility.Visible;
        App.IslandController.EndAwake();
    }

    private void FocusSettingsClose_Click(object sender, RoutedEventArgs e) => CloseFocusSettings();

    private void FocusSettingsDelete_Click(object sender, RoutedEventArgs e)
    {
        // Only an existing session can be deleted (the button is hidden in New
        // Session mode); never remove the last remaining session.
        if (_focusSettingsEditingSession == null || _focusSessions.Count <= 1) return;

        int index = _focusSessions.IndexOf(_focusSettingsEditingSession);
        _focusSessions.RemoveAt(index);

        // Select a neighbour so the ring/dots stay valid.
        _selectedFocusSessionIndex = Math.Clamp(index, 0, _focusSessions.Count - 1);
        _focusSecondsRemaining = FocusTotalSeconds;
        FocusPillRotate.Angle = 0;
        SetFocusCompleted(false);

        FocusSessionStore.SaveAll(_focusSessions);

        // Refresh the dots (x:Bind ItemsSource is OneTime — re-assign to re-pull).
        FocusDotsControl.ItemsSource = null;
        FocusDotsControl.ItemsSource = _focusSessions;
        OnPropertyChanged(nameof(CurrentSessionName));
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusProgressFraction));
        UpdateFocusDotsVisual();
        CloseFocusSettings();
    }

    private void FocusSettingsSave_Click(object sender, RoutedEventArgs e)
    {
        _focusSettingsDraftName = FocusSettingsNameBox.Text;

        if (_focusSettingsEditingSession != null)
        {
            _focusSettingsEditingSession.Name = _focusSettingsDraftName;
            ApplyDurationSeconds(_focusSettingsEditingSession, _focusSettingsDraftSeconds);
        }
        else
        {
            var newSession = new FocusSession { Name = _focusSettingsDraftName, DurationSeconds = _focusSettingsDraftSeconds };
            _focusSessions.Add(newSession);
            _selectedFocusSessionIndex = _focusSessions.Count - 1;
            OnPropertyChanged(nameof(FocusDurationText));

            // x:Bind ItemsSource is OneTime — reassigning the FocusSessions property does
            // nothing (it's get-only). Force the ItemsControl to re-pull the list directly:
            FocusDotsControl.ItemsSource = null;
            FocusDotsControl.ItemsSource = _focusSessions;
        }

        FocusSessionStore.SaveAll(_focusSessions);
        OnPropertyChanged(nameof(CurrentSessionName));
        UpdateFocusDotsVisual();
        FocusPillRotate.Angle = 0;
        CloseFocusSettings();
    }

    // ── Quick Tasks checklist handlers ─────────────────────────────────────

    private void AddTaskTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && sender is TextBox textBox)
        {
            string text = textBox.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                Tasks.Add(new TaskItem { Text = text, IsCompleted = false });
                textBox.Text = "";
            }
        }
    }

    private void Task_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TaskItem task)
        {
            task.IsCompleted = true;
        }
    }

    private void Task_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TaskItem task)
        {
            task.IsCompleted = false;
        }
    }
}

