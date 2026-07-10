using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Manages window sizing, positioning, and taskbar-docking behavior.
///
/// Design contract:
///  - Window is permanently docked bottom-left, flush with the taskbar.
///  - No drag, no free positioning, no inertia.
///  - Z-order is actively maintained by a lightweight guard timer so the
///    widget always stays visually above the taskbar regardless of shell focus.
///  - WS_EX_TOOLWINDOW ensures the widget never appears in Alt+Tab or the taskbar.
/// </summary>
public class WindowService
{
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;

    private double _cachedScale = 1.0;
    private DisplayArea? _cachedDisplayArea;

    // Flush left, flush bottom — no margins against the screen edges.
    private const double LeftMarginDips = 0.0;

    // Actual taskbar height (DIPs), detected from DisplayArea on init.
    private int _taskbarHeightDips = 48;

    // Spring simulations for smooth size animation.
    private readonly MotionConfig _motionConfig = new();
    private readonly SpringSimulation _widthSpring;
    private readonly SpringSimulation _heightSpring;
    private readonly SpringSimulation _xSpring;
    private readonly SpringSimulation _ySpring;

    private bool _isAnimating = false;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    // Repeating timer that re-asserts HWND_TOPMOST every 150 ms.
    // This is the most reliable way to stay above the taskbar in WinUI 3.
    private DispatcherQueueTimer? _zOrderTimer;

    public PointInt32 HomePosition { get; set; }

    // ── P/Invoke ───────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    private const int ABM_GETTASKBARPOS = 5;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ── Constants ──────────────────────────────────────────────────────────

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE   = 0x0002;
    private const uint SWP_NOSIZE   = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010; // Don't steal focus when repositioning

    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TOOLWINDOW  = 0x00000080; // Hides from Alt+Tab & taskbar button
    private const int WS_EX_NOACTIVATE  = 0x08000000; // Prevents focus steal on click

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND  = 2;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE   = -2;

    // Suppress DWM drop shadow
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;

    // ── Constructor ────────────────────────────────────────────────────────

    public WindowService(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _hwnd   = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        
        _appWindow.Changed += AppWindow_Changed;

        _cachedScale       = GetScaleFactor();
        _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);

        _widthSpring  = new SpringSimulation(160, _motionConfig);
        _heightSpring = new SpringSimulation(48,  _motionConfig);

        int rawW = (int)Math.Round(160 * _cachedScale);
        int rawH = (int)Math.Round(48  * _cachedScale);
        var start = GetAnchoredPosition(rawW, rawH);
        HomePosition = start;

        _xSpring = new SpringSimulation(start.X, _motionConfig);
        _ySpring = new SpringSimulation(start.Y, _motionConfig);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            _cachedScale = GetScaleFactor();
            _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            _taskbarHeightDips = ComputeTaskbarHeightDips();
        }
    }

    // ── Initialization ─────────────────────────────────────────────────────

    /// <summary>
    /// Configures the window as a borderless, permanently docked taskbar widget.
    /// The height parameter is ignored — the real taskbar height is auto-detected.
    /// </summary>
    public void InitializeWindow(int width, int _ignoredHeight)
    {
        _cachedScale       = GetScaleFactor();
        _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);

        _taskbarHeightDips = ComputeTaskbarHeightDips();

        _widthSpring.Current  = width;
        _widthSpring.Target   = width;
        _heightSpring.Current = _taskbarHeightDips;
        _heightSpring.Target  = _taskbarHeightDips;

        int rawWidth  = (int)Math.Round(width              * _cachedScale);
        int rawHeight = (int)Math.Round(_taskbarHeightDips * _cachedScale);
        var start     = GetAnchoredPosition(rawWidth, rawHeight);
        HomePosition  = start;

        _xSpring.Current = start.X; _xSpring.Target = start.X;
        _ySpring.Current = start.Y; _ySpring.Target = start.Y;

        ConfigureBorderless();
        _window.ExtendsContentIntoTitleBar = true;
        SetAlwaysOnTop(true);
        _appWindow.MoveAndResize(new RectInt32(start.X, start.Y, rawWidth, rawHeight));

        Logger.Info($"InitializeWindow: taskbarHeight={_taskbarHeightDips} dips, anchor=({start.X},{start.Y})");
    }

    private void ConfigureBorderless()
    {
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable   = false;
            p.IsMinimizable = false;
            p.IsMaximizable = false;
        }
    }

    /// <summary>
    /// Applies all DWM and Win32 window behavior attributes.
    /// Must be called AFTER Window.Activate().
    /// </summary>
    /// <param name="dispatcherQueue">UI dispatcher used to run the z-order guard timer.</param>
    public void ApplyDwmAttributes(DispatcherQueue dispatcherQueue)
    {
        ApplyRoundedCorners();
        SuppressBorder();
        SuppressShadow();
        ApplyToolWindowStyle();
        StartZOrderGuard(dispatcherQueue);
        ForceAboveTaskbar(); // Immediate push to top of TOPMOST z-order.
    }

    private void ApplyRoundedCorners()
    {
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private void SuppressBorder()
    {
        int none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    private void SuppressShadow()
    {
        // Disable the DWM non-client rendering so no floating drop-shadow appears.
        // The widget sits inside the taskbar and should have no shadow of its own.
        int policy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
    }

    /// <summary>
    /// Applies WS_EX_TOOLWINDOW (hide from Alt+Tab and taskbar button strip)
    /// and WS_EX_NOACTIVATE (never steal keyboard focus from the active app).
    /// </summary>
    private void ApplyToolWindowStyle()
    {
        // Strip WS_OVERLAPPEDWINDOW and add WS_POPUP to completely disable any OS window frames/outlines
        int style = GetWindowLong(_hwnd, -16); // GWL_STYLE = -16
        int newStyle = (style & ~0x00CF0000) | unchecked((int)0x80000000); // 0x80000000 is WS_POPUP
        SetWindowLong(_hwnd, -16, newStyle);

        int current = GetWindowLong(_hwnd, GWL_EXSTYLE);
        int updated = current | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, updated);
        Logger.Info($"ApplyToolWindowStyle: style 0x{style:X} → 0x{newStyle:X}, exStyle 0x{current:X} → 0x{updated:X}");
    }

    /// <summary>
    /// Starts a 150 ms repeating timer that re-asserts HWND_TOPMOST.
    /// This is the most reliable mechanism to stay above the Windows taskbar
    /// in WinUI 3, where WinEventHook delivery to the XAML dispatcher is not
    /// guaranteed. The overhead is one Win32 call per tick (~1 µs each).
    /// </summary>
    private void StartZOrderGuard(DispatcherQueue dispatcherQueue)
    {
        _zOrderTimer?.Stop();
        _zOrderTimer = dispatcherQueue.CreateTimer();
        _zOrderTimer.Interval = TimeSpan.FromMilliseconds(150);
        _zOrderTimer.IsRepeating = true;
        _zOrderTimer.Tick += (_, _) => ForceAboveTaskbar();
        _zOrderTimer.Start();
        Logger.Info("Z-order guard timer started (150 ms).");
    }

    private void ForceAboveTaskbar()
    {
        // SWP_NOACTIVATE prevents this SetWindowPos call from stealing focus.
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        if (_appWindow.Presenter is OverlappedPresenter p)
            p.IsAlwaysOnTop = alwaysOnTop;
    }

    // ── Taskbar height detection ───────────────────────────────────────────

    private int ComputeTaskbarHeightDips()
    {
        var data = new APPBARDATA();
        data.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);

        int taskbarPhysical = data.rc.Bottom - data.rc.Top;

        if (taskbarPhysical <= 0)
        {
            var da = _cachedDisplayArea ?? DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            taskbarPhysical = da.OuterBounds.Height - (da.WorkArea.Y + da.WorkArea.Height);
        }

        if (taskbarPhysical <= 0)
        {
            Logger.Info("ComputeTaskbarHeightDips: fallback to 48 DIPs.");
            return 48;
        }

        int dips = (int)Math.Round(taskbarPhysical / _cachedScale);
        Logger.Info($"ComputeTaskbarHeightDips (P/Invoke): {taskbarPhysical}px → {dips} DIPs.");
        return Math.Max(32, dips);
    }

    // ── Drag stubs (permanently disabled) ─────────────────────────────────

    public void StartDrag() { }
    public void UpdateDrag() { }
    public void EndDrag()   { }

    // ── Animation ──────────────────────────────────────────────────────────

    private void StopAnimations()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }
    }

    /// <summary>
    /// Animates the window to a new size.
    /// Position is derived from height — the bottom edge never moves.
    /// </summary>
    public void StartSizeAnimation(int targetWidth, int targetHeight)
    {
        _widthSpring.Target  = targetWidth;
        _heightSpring.Target = targetHeight;

        if (!_isAnimating)
        {
            Logger.Info($"Animation started → {targetWidth}×{targetHeight}");
            _isAnimating = true;
            _stopwatch.Restart();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        }
    }

    /// <summary>
    /// Animates to a predefined WindowProfile using real taskbar-relative dimensions.
    /// IslandController is the only permitted caller.
    /// </summary>
    public void SetProfile(WindowProfile profile)
    {
        int width, height;
        switch (profile)
        {
            case WindowProfile.Collapsed:
                // Collapsed dimensions matching the taskbar height (usually 48 DIPs) and 320 width when playing
                bool isMediaActive = App.MediaService.CurrentState != null && 
                                     !string.IsNullOrEmpty(App.MediaService.CurrentState.Title);
                width  = isMediaActive ? 320 : 160;
                height = _taskbarHeightDips;
                break;

            case WindowProfile.Expanded:
                // Premium wide grid dashboard flyout
                width  = 800;
                height = _taskbarHeightDips + 480;
                break;

            default:
                (width, height) = profile.ToDimensions();
                break;
        }

        StartSizeAnimation(width, height);
    }

    // ── Rendering loop ─────────────────────────────────────────────────────

    private void OnRendering(object? sender, object e)
    {
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();

        if (dt > 0.03) dt = 0.03;
        if (dt <= 0) return;

        _widthSpring.Update(dt);
        _heightSpring.Update(dt);

        int rawW = (int)Math.Round(_widthSpring.Current  * _cachedScale);
        int rawH = (int)Math.Round(_heightSpring.Current * _cachedScale);
        var pos  = GetAnchoredPosition(rawW, rawH);

        if (_widthSpring.IsSettled() && _heightSpring.IsSettled())
        {
            _widthSpring.SnapToTarget();
            _heightSpring.SnapToTarget();

            int finalW   = (int)Math.Round(_widthSpring.Target  * _cachedScale);
            int finalH   = (int)Math.Round(_heightSpring.Target * _cachedScale);
            var finalPos = GetAnchoredPosition(finalW, finalH);

            _xSpring.Current = finalPos.X; _xSpring.Velocity = 0;
            _ySpring.Current = finalPos.Y; _ySpring.Velocity = 0;
            HomePosition = finalPos;

            _appWindow.MoveAndResize(new RectInt32(finalPos.X, finalPos.Y, finalW, finalH));
            _isAnimating = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            Logger.Info("Animation settled.");
            return;
        }

        _appWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, rawW, rawH));
    }

    // ── Anchoring ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pins the window's bottom edge to the screen bottom and left edge to the screen left.
    /// As height grows (expansion), the window rises upward — the dock strip stays fixed.
    /// </summary>
    private PointInt32 GetAnchoredPosition(int rawWidthPhysical, int rawHeightPhysical)
    {
        var da = _cachedDisplayArea ?? DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);

        int x = da.WorkArea.X + (int)Math.Round(LeftMarginDips * _cachedScale);
        int screenBottom = da.OuterBounds.Y + da.OuterBounds.Height;
        int y = screenBottom - rawHeightPhysical;

        return new PointInt32(x, y);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private double GetScaleFactor()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi / 96.0;
    }
}
