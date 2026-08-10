using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private DispatcherTimer? _visualizerTimer;
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
        set { _currentPlaybackTimeText = value; OnPropertyChanged(); }
    }

    private string _totalPlaybackTimeText = "0:00";
    public string TotalPlaybackTimeText
    {
        get => _totalPlaybackTimeText;
        set { _totalPlaybackTimeText = value; OnPropertyChanged(); }
    }

    private string _liveClipboardTitle = "main.cpp";
    public string LiveClipboardTitle
    {
        get => _liveClipboardTitle;
        set { _liveClipboardTitle = value; OnPropertyChanged(); }
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

    private string _ramUsedText = "9.6 GB";
    public string RamUsedText
    {
        get => _ramUsedText;
        set { _ramUsedText = value; OnPropertyChanged(); }
    }

    private string _ramTotalText = "16.0 GB";
    public string RamTotalText
    {
        get => _ramTotalText;
        set { _ramTotalText = value; OnPropertyChanged(); }
    }

    private int _ramPercent = 0;
    public int RamPercent
    {
        get => _ramPercent;
        set { _ramPercent = value; OnPropertyChanged(); }
    }

    private string _ramPercentText = "—";
    public string RamPercentText
    {
        get => _ramPercentText;
        set { _ramPercentText = value; OnPropertyChanged(); }
    }

    private string _cpuPercentText = "—";
    public string CpuPercentText
    {
        get => _cpuPercentText;
        set { _cpuPercentText = value; OnPropertyChanged(); }
    }

    private string _batteryPercentText = "—";
    public string BatteryPercentText
    {
        get => _batteryPercentText;
        set { _batteryPercentText = value; OnPropertyChanged(); }
    }

    private string _batteryStatusText = "Charging";
    public string BatteryStatusText
    {
        get => _batteryStatusText;
        set { _batteryStatusText = value; OnPropertyChanged(); }
    }

    private Visibility _batteryChargingVisibility = Visibility.Collapsed;
    public Visibility BatteryChargingVisibility
    {
        get => _batteryChargingVisibility;
        set { _batteryChargingVisibility = value; OnPropertyChanged(); }
    }

    private AppIconKind _batteryIconKind = AppIconKind.Battery9;
    public AppIconKind BatteryIconKind
    {
        get => _batteryIconKind;
        set { _batteryIconKind = value; OnPropertyChanged(); }
    }

    private string _storageFreeText = "215 GB";
    public string StorageFreeText
    {
        get => _storageFreeText;
        set { _storageFreeText = value; OnPropertyChanged(); }
    }

    private string _storageTotalText = "512 GB";
    public string StorageTotalText
    {
        get => _storageTotalText;
        set { _storageTotalText = value; OnPropertyChanged(); }
    }

    private int _storagePercent = 0;
    public int StoragePercent
    {
        get => _storagePercent;
        set 
        { 
            _storagePercent = value; 
            OnPropertyChanged(); 
        }
    }

    private string _storagePercentText = "—";
    public string StoragePercentText
    {
        get => _storagePercentText;
        set { _storagePercentText = value; OnPropertyChanged(); }
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

    public double VolumePercentValue
    {
        get => App.VolumeService.ReadCurrentState().VolumePercent;
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

    /// <summary>
    /// Session length (seconds) for the currently selected focus session.
    /// </summary>
    private int FocusTotalSeconds => _focusSessions[_selectedFocusSessionIndex].DurationSeconds;

    public string CurrentSessionName => _focusSessions[_selectedFocusSessionIndex].Name;

    public string FocusTimerText => $"{_focusSecondsRemaining / 60:D2}:{_focusSecondsRemaining % 60:D2}";
    public AppIconKind FocusPlayPauseIconKind => _focusIsRunning ? AppIconKind.Pause : AppIconKind.Play;

    /// <summary>
    /// Focus session progress from 0.0 (session start) to 1.0 (time elapsed).
    /// The ring fills up clockwise as the session runs down.
    /// </summary>
    public double FocusProgressFraction => 1.0 - ((double)_focusSecondsRemaining / FocusTotalSeconds);

    // ── Focus Session ring drag state ──────────────────────────────────────

    private bool _isFocusRingDragging;
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

        // Initial query
        UpdateStats();

        // Initialize sample tasks
        Tasks.Add(new TaskItem { Text = "Review PR #42", IsCompleted = false });
        Tasks.Add(new TaskItem { Text = "Sync design tokens", IsCompleted = false });

        // Subscribe to real service updates
        App.WeatherService.WeatherUpdated += OnWeatherUpdated;

        // Force initial update calls to load values immediately
        OnWeatherUpdated(null, EventArgs.Empty);

        // Wire up the clipboard history list
        App.ClipboardService.History.CollectionChanged += OnClipboardHistoryChanged;
        UpdateFilterVisual();
        RefreshClipboardFilter();

        // Clipboard retention + search: reflect the persisted retention period and
        // enable typing in the search box (WS_EX_NOACTIVATE requires the temporary
        // flag-lift, same pattern as the Focus session text fields).
        SelectRetentionOption(App.ClipboardService.RetentionDays);
        AttachTextInputFocus(ClipboardSearchBox);
        AttachTextInputFocus(RetentionCombo);

        // Timer for system stats and play time updates (1s)
        _updateTimer = new DispatcherTimer();
        _updateTimer.Interval = TimeSpan.FromSeconds(1);
        _updateTimer.Tick += (s, e) => { App.MediaService.TickValidation(); UpdateStats(); };
        _updateTimer.Start();

        // Timer for visualizer spectrum animation (120ms)
        _visualizerTimer = new DispatcherTimer();
        _visualizerTimer.Interval = TimeSpan.FromMilliseconds(120);
        _visualizerTimer.Tick += (s, e) => UpdateVisualizer();
        _visualizerTimer.Start();
    }

    // ── Weather Service handler ────────────────────────────────────────────

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



    // ── Audio visualizer tick ──────────────────────────────────────────────

    private void UpdateVisualizer()
    {
        // Visualizer bars replaced with modern Fluent progress line
    }

    // ── Stats updates ──────────────────────────────────────────────────────

    private int _lastVol = -1;

    private void UpdateStats()
    {
        // 1. Focus Session Timer
        if (_focusIsRunning)
        {
            if (_focusSecondsRemaining > 0)
            {
                _focusSecondsRemaining--;
                if (_focusSecondsRemaining == 0)
                {
                    _focusIsRunning = false;          // stop counting; remain at 00:00
                }
            }
            OnPropertyChanged(nameof(FocusTimerText));
            OnPropertyChanged(nameof(FocusProgressFraction));
            OnPropertyChanged(nameof(FocusPlayPauseIconKind)); // icon returns to Play
            FocusPillRotate.Angle = FocusProgressFraction * 360;
        }

        // 2. Playback time (real timeline, interpolated live while playing)
        UpdatePlaybackDisplay();

        // 3. RAM
        if (GetPerformanceInfo(out var info, Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
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
            var drive = new System.IO.DriveInfo("C");
            double totalGB = drive.TotalSize / (1024.0 * 1024 * 1024);
            double freeGB = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            double usedGB = totalGB - freeGB;

            StorageTotalText = $"{totalGB:F0} GB";
            StorageFreeText = $"{freeGB:F0} GB";
            StoragePercent = (int)Math.Round((usedGB / totalGB) * 100);
            StoragePercentText = $"{StoragePercent}%";
        }
        catch { }

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

        // 7. Sync external volume changes
        int currentVol = App.VolumeService.ReadCurrentState().VolumePercent;
        if (currentVol != _lastVol)
        {
            _lastVol = currentVol;
            OnPropertyChanged(nameof(VolumePercentValue));
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
    private bool _syncingRetentionCombo;
    private string _searchText = "";
    private (ClipboardItem Item, TranslateTransform Transform, Button Strip)? _revealedItem;

    private void RefreshClipboardFilter()
    {
        _revealedItem = null;
        ClipboardItems.Clear();
        string query = _searchText;
        foreach (var item in App.ClipboardService.History)
        {
            if (_showPinnedOnly && !item.IsPinned) continue;
            if (!string.IsNullOrEmpty(query) && !MatchesSearch(item, query)) continue;
            ClipboardItems.Add(item);
        }

        if (!string.IsNullOrEmpty(query))
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

    // ── Clipboard retention ────────────────────────────────────────────────

    /// <summary>
    /// Preselects the persisted retention period without firing the change handler
    /// (the fallback index is visual only — the service value is never touched).
    /// </summary>
    private void SelectRetentionOption(int days)
    {
        _syncingRetentionCombo = true;
        try
        {
            for (int i = 0; i < RetentionCombo.Items.Count; i++)
            {
                if (RetentionCombo.Items[i] is ComboBoxItem { Tag: string tag }
                    && int.TryParse(tag, out int optionDays)
                    && optionDays == days)
                {
                    RetentionCombo.SelectedIndex = i;
                    return;
                }
            }
            RetentionCombo.SelectedIndex = 0;
        }
        finally
        {
            _syncingRetentionCombo = false;
        }
    }

    private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRetentionCombo) return;
        if (RetentionCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, out int days))
        {
            App.ClipboardService.SetRetentionDays(days);
        }
    }

    private void OnClipboardHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(RefreshClipboardFilter);
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
        AllFilterButton.Foreground = GetThemeBrush(_showPinnedOnly ? "TextSecondaryBrush" : "AccentBrush");
        PinnedFilterButton.Foreground = GetThemeBrush(_showPinnedOnly ? "AccentBrush" : "TextSecondaryBrush");
        AllFilterButton.FontWeight = _showPinnedOnly ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Bold;
        PinnedFilterButton.FontWeight = _showPinnedOnly ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
    }

    private static Brush GetThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
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
        if ((sender as FrameworkElement)?.DataContext is ClipboardItem item)
        {
            App.ClipboardService.TogglePin(item);
            RefreshClipboardFilter();
        }
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
        if ((sender as FrameworkElement)?.DataContext is ClipboardItem item)
        {
            App.ClipboardService.DeleteItem(item);
        }
        _revealedItem = null;
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

    // ── Mute toggle click ──────────────────────────────────────────────────

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        var current = App.VolumeService.ReadCurrentState();
        App.VolumeService.SetMute(!current.IsMuted);
    }

    // ── Footer settings gear (location) ────────────────────────────────────

    private void SettingsGear_Click(object sender, RoutedEventArgs e)
    {
        var popup = new LocationSettingsPopup();
        var flyout = new Flyout
        {
            Content = popup,
            Placement = FlyoutPlacementMode.Top
        };
        popup.RequestClose += (_, _) => flyout.Hide();
        App.IslandController.BeginAwake();
        flyout.Closed += (_, _) => App.IslandController.EndAwake();
        flyout.ShowAt((FrameworkElement)sender);
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
        }
        else
        {
            _focusSecondsRemaining = FocusTotalSeconds;    // Completed -> fresh session
            FocusPillRotate.Angle = 0;
            _focusIsRunning = true;
        }
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusProgressFraction));
        OnPropertyChanged(nameof(FocusPlayPauseIconKind));
    }

    private void FocusReset_Click(object sender, RoutedEventArgs e)
    {
        _focusIsRunning = false;
        _focusSecondsRemaining = FocusTotalSeconds; // Reset to the selected session's duration
        FocusPillRotate.Angle = 0;
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusPlayPauseIconKind));
        OnPropertyChanged(nameof(FocusProgressFraction));
    }

    // ── Focus Session ring drag handlers ───────────────────────────────────

    // ── Shared duration conversion core (single source of truth) ───────────
    // Used by both the ring (fraction ↔ angle) and the settings H/M/S boxes
    // (fraction ↔ decomposed duration). Both funnel into ApplyDurationSeconds.

    private const int FocusMaxDurationMinutes = 1440; // 1-1440 minutes (24 hours)

    private static double DurationToFraction(int seconds) =>
        Math.Clamp(((double)seconds / 60.0 - 1.0) / (FocusMaxDurationMinutes - 1), 0, 1);

    private static int FractionToDurationSeconds(double fraction) =>
        (1 + (int)Math.Round(fraction * (FocusMaxDurationMinutes - 1))) * 60;

    private void ApplyDurationSeconds(FocusSession session, int seconds)
    {
        session.DurationSeconds = seconds;
        _focusSecondsRemaining = seconds;
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusProgressFraction));
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
        double dx = p.X - 50;
        double dy = p.Y - 50;
        double angle = Math.Atan2(dx, -dy);
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

    private void FocusRing_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Drag is disabled entirely while running: no capture, no state, no response.
        if (_focusIsRunning) return;

        _dragAccumulatedFraction = CurrentDurationFraction;
        _dragLastAngle = AngleFromFocusRingPoint(e.GetCurrentPoint(FocusRingGrid).Position);
        FocusPillRotate.Angle = _dragAccumulatedFraction * 360;
        _isFocusRingDragging = true;
        FocusRingDragSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void FocusRing_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFocusRingDragging) return;

        double theta = AngleFromFocusRingPoint(e.GetCurrentPoint(FocusRingGrid).Position);

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

        // Persist the chosen duration once; the pill stays at the angle the drag left
        // it, representing the just-chosen duration, until the running tick moves it.
        FocusSessionStore.SaveAll(_focusSessions);
        OnPropertyChanged(nameof(FocusProgressFraction));
    }

    // ── Focus Session dot switcher ─────────────────────────────────────────

    private static readonly SolidColorBrush UnselectedDotBrush = new(Microsoft.UI.Colors.LightGray);

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
        UpdateFocusDotsVisual();
        OnPropertyChanged(nameof(CurrentSessionName));
        OnPropertyChanged(nameof(FocusTimerText));
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
                    : UnselectedDotBrush;
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

    private void FocusSettingsNumberBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        // Draft-only: update the local seconds and readout, never the live timer state.
        int h = double.IsNaN(FocusSettingsHoursBox.Value) ? 0 : (int)Math.Round(FocusSettingsHoursBox.Value);
        int m = double.IsNaN(FocusSettingsMinutesBox.Value) ? 0 : (int)Math.Round(FocusSettingsMinutesBox.Value);
        int s = double.IsNaN(FocusSettingsSecondsBox.Value) ? 0 : (int)Math.Round(FocusSettingsSecondsBox.Value);
        _focusSettingsDraftSeconds = h * 3600 + m * 60 + s;
        UpdateSettingsReadout();
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
        FocusSettingsNameBox.Text = _focusSettingsDraftName;
        FocusSettingsHoursBox.Value = _focusSettingsDraftSeconds / 3600;
        FocusSettingsMinutesBox.Value = (_focusSettingsDraftSeconds % 3600) / 60;
        FocusSettingsSecondsBox.Value = _focusSettingsDraftSeconds % 60;
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

