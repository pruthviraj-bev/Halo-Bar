using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using DynamicIsland.Helpers;
using DynamicIsland.ViewModels;
using DynamicIsland.Services;

namespace DynamicIsland.Widgets;

public sealed partial class ExpandedDashboard : UserControl, INotifyPropertyChanged
{
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _visualizerTimer;
    private readonly Random _rand = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── Properties ─────────────────────────────────────────────────────────

    public MediaWidgetViewModel MediaViewModel { get; } = new();

    private string _currentPlaybackTimeText = "1:35";
    public string CurrentPlaybackTimeText
    {
        get => _currentPlaybackTimeText;
        set { _currentPlaybackTimeText = value; OnPropertyChanged(); }
    }

    private string _totalPlaybackTimeText = "3:46";
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

    private int _ramPercent = 60;
    public int RamPercent
    {
        get => _ramPercent;
        set { _ramPercent = value; OnPropertyChanged(); }
    }

    private string _ramPercentText = "60%";
    public string RamPercentText
    {
        get => _ramPercentText;
        set { _ramPercentText = value; OnPropertyChanged(); }
    }

    private string _cpuPercentText = "18%";
    public string CpuPercentText
    {
        get => _cpuPercentText;
        set { _cpuPercentText = value; OnPropertyChanged(); }
    }

    private string _gpuPercentText = "42%";
    public string GpuPercentText
    {
        get => _gpuPercentText;
        set { _gpuPercentText = value; OnPropertyChanged(); }
    }

    private string _batteryPercentText = "82%";
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

    private string _batteryGlyph = "\uE83E";
    public string BatteryGlyph
    {
        get => _batteryGlyph;
        set { _batteryGlyph = value; OnPropertyChanged(); }
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

    private int _storagePercent = 58;
    public int StoragePercent
    {
        get => _storagePercent;
        set 
        { 
            _storagePercent = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(StoragePercentText));
        }
    }

    public string StoragePercentText => $"{_storagePercent}%";

    // ── Volume Slider Integration ──────────────────────────────────────────

    public double PlaybackProgressValue => ((double)_playbackSecs / _totalSecs) * 100;

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
    public string WeatherGlyph { get; private set; } = "\uE706";

    public string ForecastDay1Text { get; private set; } = "—";
    public string ForecastDay1Glyph { get; private set; } = "\uE706";
    public string ForecastDay1Temp { get; private set; } = "—";

    public string ForecastDay2Text { get; private set; } = "—";
    public string ForecastDay2Glyph { get; private set; } = "\uE706";
    public string ForecastDay2Temp { get; private set; } = "—";

    public string ForecastDay3Text { get; private set; } = "—";
    public string ForecastDay3Glyph { get; private set; } = "\uE706";
    public string ForecastDay3Temp { get; private set; } = "—";

    // ── Focus Session Properties ───────────────────────────────────────────

    private int _focusSecondsRemaining = 1492; // 24:52 as shown in the mockup
    private bool _focusIsRunning = false;
    private int _focusRound = 2;
    private int _focusTotalRounds = 4;

    public string FocusTimerText => $"{_focusSecondsRemaining / 60:D2}:{_focusSecondsRemaining % 60:D2}";
    public string FocusRoundText => $"Round {_focusRound}/{_focusTotalRounds}";
    public string FocusPlayPauseGlyph => _focusIsRunning ? "\uE769" : "\uE768"; // Pause / Play glyphs

    // ── Quick Tasks Properties ─────────────────────────────────────────────

    public ObservableCollection<TaskItem> Tasks { get; } = new();

    // ── File Shelf Properties ──────────────────────────────────────────────

    public ObservableCollection<StashedFile> StashedFiles { get; } = new();

    public Visibility DropZoneVisibility => StashedFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StashedFilesVisibility => StashedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

    // ── Constructor ────────────────────────────────────────────────────────

    public ExpandedDashboard()
    {
        InitializeComponent();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        
        // Initial query
        UpdateStats();
        InitializeCpuGraph();
        InitializeGpuGraph();

        // Initialize sample tasks
        Tasks.Add(new TaskItem { Text = "Review PR #42", IsCompleted = false });
        Tasks.Add(new TaskItem { Text = "Sync design tokens", IsCompleted = false });

        // Initialize sample stashed files
        StashedFiles.Add(new StashedFile { Name = "hero_shot.png", Path = "mock_path_hero_shot.png" });

        // Subscribe to real service updates
        App.WeatherService.WeatherUpdated += OnWeatherUpdated;

        // Force initial update calls to load values immediately
        OnWeatherUpdated(null, EventArgs.Empty);


        // Timer for system stats and play time updates (1s)
        _updateTimer = new DispatcherTimer();
        _updateTimer.Interval = TimeSpan.FromSeconds(1);
        _updateTimer.Tick += (s, e) => UpdateStats();
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
                WeatherGlyph = ws.Glyph;

                if (ws.Forecast.Length >= 3)
                {
                    ForecastDay1Text = ws.Forecast[0].Day;
                    ForecastDay1Glyph = ws.Forecast[0].Glyph;
                    ForecastDay1Temp = ws.Forecast[0].TempRange;

                    ForecastDay2Text = ws.Forecast[1].Day;
                    ForecastDay2Glyph = ws.Forecast[1].Glyph;
                    ForecastDay2Temp = ws.Forecast[1].TempRange;

                    ForecastDay3Text = ws.Forecast[2].Day;
                    ForecastDay3Glyph = ws.Forecast[2].Glyph;
                    ForecastDay3Temp = ws.Forecast[2].TempRange;
                }
            }

            OnPropertyChanged(nameof(WeatherAvailableVisibility));
            OnPropertyChanged(nameof(WeatherUnavailableVisibility));
            OnPropertyChanged(nameof(WeatherTemp));
            OnPropertyChanged(nameof(WeatherCondition));
            OnPropertyChanged(nameof(WeatherGlyph));
            OnPropertyChanged(nameof(ForecastDay1Text));
            OnPropertyChanged(nameof(ForecastDay1Glyph));
            OnPropertyChanged(nameof(ForecastDay1Temp));
            OnPropertyChanged(nameof(ForecastDay2Text));
            OnPropertyChanged(nameof(ForecastDay2Glyph));
            OnPropertyChanged(nameof(ForecastDay2Temp));
            OnPropertyChanged(nameof(ForecastDay3Text));
            OnPropertyChanged(nameof(ForecastDay3Glyph));
            OnPropertyChanged(nameof(ForecastDay3Temp));
        });
    }



    // ── Audio visualizer tick ──────────────────────────────────────────────

    private void UpdateVisualizer()
    {
        // Visualizer bars replaced with modern Fluent progress line
    }

    // ── Stats updates ──────────────────────────────────────────────────────

    private int _playbackSecs = 95;
    private readonly int _totalSecs = 226;
    private int _cpuVal = 18;
    private int _lastVol = -1;

    private void UpdateStats()
    {
        // 1. Focus Session Timer
        if (_focusIsRunning)
        {
            if (_focusSecondsRemaining > 0)
            {
                _focusSecondsRemaining--;
            }
            else
            {
                _focusRound++;
                if (_focusRound > _focusTotalRounds)
                {
                    _focusRound = 1;
                }
                _focusSecondsRemaining = 1500; // Reset to 25 mins

            }
            OnPropertyChanged(nameof(FocusTimerText));
            OnPropertyChanged(nameof(FocusRoundText));
        }

        // 2. Playback time
        if (MediaViewModel.IsPlaying)
        {
            _playbackSecs++;
            if (_playbackSecs > _totalSecs)
                _playbackSecs = 0;
        }
        CurrentPlaybackTimeText = $"{_playbackSecs / 60}:{_playbackSecs % 60:D2}";
        TotalPlaybackTimeText = $"{_totalSecs / 60}:{_totalSecs % 60:D2}";
        OnPropertyChanged(nameof(PlaybackProgressValue));

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

        // 4. CPU
        _cpuVal = Math.Clamp(_cpuVal + _rand.Next(-4, 5), 5, 75);
        CpuPercentText = $"{_cpuVal}%";
        AddCpuDataPoint(_cpuVal);

        // 5. GPU (Simulated)
        _gpuVal = Math.Clamp(_gpuVal + _rand.Next(-5, 6), 5, 85);
        GpuPercentText = $"{_gpuVal}%";
        AddGpuDataPoint(_gpuVal);

        // 6. Battery
        var bat = App.BatteryService.CurrentState;
        BatteryPercentText = $"{bat.ChargePercent}%";
        BatteryStatusText = bat.IsCharging ? "Charging" : "Discharging";
        BatteryChargingVisibility = bat.IsCharging ? Visibility.Visible : Visibility.Collapsed;
        
        // Glyphs based on percentage
        if (bat.ChargePercent > 90) BatteryGlyph = "\uE83F";
        else if (bat.ChargePercent > 70) BatteryGlyph = "\uE83E";
        else if (bat.ChargePercent > 50) BatteryGlyph = "\uE83D";
        else if (bat.ChargePercent > 30) BatteryGlyph = "\uE83C";
        else BatteryGlyph = "\uE83B";


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

    // ── CPU Sparkline graph ────────────────────────────────────────────────

    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _gpuHistory = new();
    private double _gpuVal = 42;

    private void InitializeCpuGraph()
    {
        for (int i = 0; i < 30; i++)
            _cpuHistory.Enqueue(15);
        
        RedrawCpuGraph();
    }

    private void AddCpuDataPoint(double val)
    {
        _cpuHistory.Enqueue(val);
        if (_cpuHistory.Count > 30)
            _cpuHistory.Dequeue();

        RedrawCpuGraph();
    }

    private void RedrawCpuGraph()
    {
        CpuGraphLine.Points.Clear();
        int idx = 0;
        foreach (var val in _cpuHistory)
        {
            double x = idx * 5.1; // ~153px width
            double y = 22 - (val / 100.0 * 22);
            CpuGraphLine.Points.Add(new Windows.Foundation.Point(x, y));
            idx++;
        }
    }

    // ── Clipboard actions ──────────────────────────────────────────────────

    private void ClearAllClipboard_Click(object sender, RoutedEventArgs e)
    {
        App.ClipboardService.Clear();
    }

    private void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        var item = App.ClipboardService.CurrentItem;
        if (item != null)
        {
            App.ClipboardService.ReCopy(item);
        }
    }

    // ── Mute toggle click ──────────────────────────────────────────────────

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        var current = App.VolumeService.ReadCurrentState();
        App.VolumeService.SetMute(!current.IsMuted);
    }

    // ── GPU Sparkline graph ────────────────────────────────────────────────

    private void InitializeGpuGraph()
    {
        for (int i = 0; i < 30; i++)
            _gpuHistory.Enqueue(15);
        
        RedrawGpuGraph();
    }

    private void AddGpuDataPoint(double val)
    {
        _gpuHistory.Enqueue(val);
        if (_gpuHistory.Count > 30)
            _gpuHistory.Dequeue();

        RedrawGpuGraph();
    }

    private void RedrawGpuGraph()
    {
        if (GpuGraphLine != null)
        {
            GpuGraphLine.Points.Clear();
            int idx = 0;
            foreach (var val in _gpuHistory)
            {
                double x = idx * 5.1; // ~153px width
                double y = 22 - (val / 100.0 * 22);
                GpuGraphLine.Points.Add(new Windows.Foundation.Point(x, y));
                idx++;
            }
        }
    }

    // ── Focus Session click handlers ───────────────────────────────────────

    private void FocusPlayPause_Click(object sender, RoutedEventArgs e)
    {
        _focusIsRunning = !_focusIsRunning;
        OnPropertyChanged(nameof(FocusPlayPauseGlyph));
    }

    private void FocusReset_Click(object sender, RoutedEventArgs e)
    {
        _focusIsRunning = false;
        _focusSecondsRemaining = 1500; // Reset to 25 mins
        _focusRound = 1;
        OnPropertyChanged(nameof(FocusTimerText));
        OnPropertyChanged(nameof(FocusRoundText));
        OnPropertyChanged(nameof(FocusPlayPauseGlyph));
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

    // ── File Shelf drag-and-drop handlers ──────────────────────────────────

    private void FileDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Stash file";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void FileDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                StashedFiles.Add(new StashedFile 
                { 
                    Name = item.Name, 
                    Path = item.Path 
                });
            }
            OnPropertyChanged(nameof(DropZoneVisibility));
            OnPropertyChanged(nameof(StashedFilesVisibility));
        }
    }

    private async void StashedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            if (path == "mock_path_hero_shot.png") return; // ignore mock
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                Helpers.Logger.Error($"FileShelf: failed to open stashed file {path}", ex);
            }
        }
    }

    private void DeleteStashedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            var file = StashedFiles.FirstOrDefault(f => f.Path == path);
            if (file != null)
            {
                StashedFiles.Remove(file);
                OnPropertyChanged(nameof(DropZoneVisibility));
                OnPropertyChanged(nameof(StashedFilesVisibility));
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

