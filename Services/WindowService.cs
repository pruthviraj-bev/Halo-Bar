using System;
using System.Text;
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

    private DispatcherQueueTimer? _zOrderTimer;
    private int _lastCloakedState = -1;
    private WinEventDelegate? _winEventDelegate;
    private IntPtr _hWinEventHook = IntPtr.Zero;

    public event EventHandler<bool>? FullscreenStateChanged;
    private bool _lastFullscreenActive = false;
    private bool _isHiddenForFullscreen = false;

    // -1 = unchecked, 0 = full (320 DIPs), 1 = moderate (260 DIPs, artist hidden), 2 = heavy (180 DIPs, both hidden)
    private int _lastWidthTier = -1;

    /// <summary>
    /// The current layout tier resolved from taskbar available width.
    /// 0 = full (both text visible), 1 = moderate (artist hidden), 2 = heavy (both hidden).
    /// Set every 150ms by the z-order guard timer. MediaWidget reads this instead of
    /// re-deriving the tier from the oscillating spring width, which would cause flickering.
    /// </summary>
    public static int CurrentWidthTier { get; private set; } = 0;

#if DEBUG
    public static double AvailableWidthOverride { get; set; } = -1;
#endif

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
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

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
    private const int DWMWA_CLOAKED = 14;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

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
        Logger.Info($"[DEBUG_EVENT] AppWindow_Changed: PositionChanged={args.DidPositionChange}, SizeChanged={args.DidSizeChange}, VisibilityChanged={args.DidVisibilityChange} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        if ((args.DidPositionChange || args.DidSizeChange) && !_isAnimating)
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

        // Anchor to the taskbar window as its owner so we always render on top of it.
        IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, -8, taskbarHwnd); // GWLP_HWNDPARENT = -8
            Logger.Info($"SetWindowLongPtr owner to taskbar (0x{taskbarHwnd.ToInt64():X}) succeeded.");
        }

        // Initialize WinEvent hook for foreground window changes
        _winEventDelegate = new WinEventDelegate(WinEventProc);
        _hWinEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        Logger.Info("WinEventHook (EVENT_SYSTEM_FOREGROUND) registered successfully.");

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
        bool success = SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        
        // Query DWM cloaked attribute
        int hr = DwmGetWindowAttribute(_hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
        if (hr == 0)
        {
            if (cloaked != _lastCloakedState)
            {
                Logger.Info($"[DEBUG_EVENT] DWMWA_CLOAKED changed: {_lastCloakedState} → {cloaked} (1=App, 2=Shell, 4=Inherited) at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                _lastCloakedState = cloaked;
            }
        }
        else
        {
            Logger.Error($"[DEBUG_EVENT] DwmGetWindowAttribute failed with HRESULT 0x{hr:X8} (GetLastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        }

#if DEBUG
        // Check testing hotkeys in debug builds
        if ((GetAsyncKeyState(0x78) & 0x8000) != 0) // F9
        {
            if (AvailableWidthOverride != -1)
            {
                AvailableWidthOverride = -1;
                Logger.Info("[TEST_OVERRIDE] Available taskbar width reset to auto.");
                App.IslandController.ApplyWindowProfile();
            }
        }
        else if ((GetAsyncKeyState(0x79) & 0x8000) != 0) // F10
        {
            if (AvailableWidthOverride != 240)
            {
                AvailableWidthOverride = 240;
                Logger.Info("[TEST_OVERRIDE] Available taskbar width overridden to 240 DIPs (Moderate crowding).");
                App.IslandController.ApplyWindowProfile();
            }
        }
        else if ((GetAsyncKeyState(0x7A) & 0x8000) != 0) // F11
        {
            if (AvailableWidthOverride != 180)
            {
                AvailableWidthOverride = 180;
                Logger.Info("[TEST_OVERRIDE] Available taskbar width overridden to 180 DIPs (Heavy crowding).");
                App.IslandController.ApplyWindowProfile();
            }
        }
#endif

        // Evaluate fullscreen mode state changes — hide/show AppWindow directly so the acrylic surface is fully removed
        bool isFullscreen = IsFullscreenModeActive();
        if (isFullscreen != _lastFullscreenActive)
        {
            _lastFullscreenActive = isFullscreen;
            Logger.Info($"[DEBUG_EVENT] Fullscreen state changed: isFullscreen={isFullscreen} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            if (isFullscreen)
            {
                Logger.Info("[DEBUG_EVENT] Hiding AppWindow for fullscreen suppression.");
                _isHiddenForFullscreen = true;
                _appWindow.Hide();
            }
            else
            {
                Logger.Info("[DEBUG_EVENT] Showing AppWindow after fullscreen exit.");
                _isHiddenForFullscreen = false;
                _appWindow.Show();
                // Re-assert TOPMOST after Show(), since Show() may reset Z-order
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            FullscreenStateChanged?.Invoke(this, isFullscreen);
        }

        // Skip width tier check and SetWindowPos when hidden — window is not visible
        if (_isHiddenForFullscreen) return;

        // ── Continuous available-width tier tracking ──────────────────────
        // Run every 150ms tick so opening/closing taskbar apps is immediately reflected.
        if (App.MediaService.CurrentState != null &&
            !string.IsNullOrEmpty(App.MediaService.CurrentState.Title))
        {
            double availW = GetAvailableWidthDips();
            // Use hysteresis: separate up/down thresholds to prevent oscillation at boundaries.
            // Down-shift thresholds (tightening): 330 / 260
            // Up-shift thresholds  (loosening):  350 / 280  (20 DIP dead-band each side)
            int tier;
            if (_lastWidthTier <= 0)
                tier = availW < 330 ? 1 : 0;            // was full or unitialized → tighten at 330
            else if (_lastWidthTier == 1)
                tier = availW < 260 ? 2 : availW >= 350 ? 0 : 1;  // hysteresis both ways
            else
                tier = availW >= 280 ? 1 : 2;           // was heavy → loosen at 280

            if (tier != _lastWidthTier)
            {
                Logger.Info($"[WIDTH_TIER] availWidth={availW:F1} DIPs → tier={tier} ({(tier==0?"full":tier==1?"moderate":"heavy")}), prev={_lastWidthTier} — calling ApplyWindowProfile");
                _lastWidthTier = tier;
                CurrentWidthTier = tier;
                // Push text animation immediately, independently of window spring settling
                App.IslandController.SetMediaWidgetTier(tier);
                App.IslandController.ApplyWindowProfile();
            }
        }
        else
        {
            // No media active — reset tier so next media session starts fresh
            _lastWidthTier = -1;
            CurrentWidthTier = 0;
        }
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            string className = sb.ToString();
            Logger.Info($"[DEBUG_EVENT] Foreground window changed to: {className} (HWND: 0x{hwnd.ToInt64():X}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            
            ForceAboveTaskbar();
        }
    }

    ~WindowService()
    {
        if (_hWinEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_hWinEventHook);
        }
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
            _cachedScale = GetScaleFactor();
            _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            _taskbarHeightDips = ComputeTaskbarHeightDips();

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
                // Collapsed dimensions matching the taskbar height.
                // Each tier uses a discrete target width well inside the tier's range,
                // so the spring never oscillates across a tier boundary during settling.
                bool isMediaActive = App.MediaService.CurrentState != null && 
                                     !string.IsNullOrEmpty(App.MediaService.CurrentState.Title);
                if (isMediaActive)
                {
                    // Tier 0 (full): 320 DIPs — both text elements visible
                    // Tier 1 (moderate): 250 DIPs — artist hidden, title shown
                    // Tier 2 (heavy): 170 DIPs — both hidden
                    int tier = CurrentWidthTier;
                    width = tier == 0 ? 320 : tier == 1 ? 250 : 170;
                }
                else
                {
                    width = 160;
                }
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

    private bool IsFullscreenModeActive()
    {
        // 1. Check user notification state (direct system signal for gaming/presentations)
        int hr = SHQueryUserNotificationState(out int qState);
        if (hr == 0) // S_OK
        {
            if (qState == QUNS_RUNNING_D3D_FULL_SCREEN || qState == QUNS_PRESENTATION_MODE)
            {
                return true;
            }
        }

        // 2. Fallback check for borderless windowed fullscreen (e.g. browser video)
        IntPtr fgHwnd = GetForegroundWindow();
        if (fgHwnd != IntPtr.Zero)
        {
            // Do not treat taskbar or desktop as fullscreen
            StringBuilder sb = new StringBuilder(256);
            GetClassName(fgHwnd, sb, sb.Capacity);
            string className = sb.ToString();
            if (className == "Shell_TrayWnd" || className == "Progman" || className == "WorkerW")
            {
                return false;
            }

            IntPtr hMonitor = MonitorFromWindow(fgHwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    if (GetWindowRect(fgHwnd, out RECT fgRect))
                    {
                        bool matchOrExceed = fgRect.Left <= mi.rcMonitor.Left &&
                                             fgRect.Top <= mi.rcMonitor.Top &&
                                             fgRect.Right >= mi.rcMonitor.Right &&
                                             fgRect.Bottom >= mi.rcMonitor.Bottom;
                        if (matchOrExceed)
                        {
                            int style = GetWindowLong(fgHwnd, -16); // GWL_STYLE = -16
                            // WS_CAPTION = 0x00C00000. If WS_CAPTION is missing, it is borderless.
                            bool hasNoCaption = (style & 0x00C00000) == 0;
                            if (hasNoCaption)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }



    /// <summary>
    /// Returns the left edge of the Windows taskbar icon list (MSTaskListWClass) in DIPs.
    /// This represents the available width between the left screen edge (or start menu area)
    /// and the taskbar buttons. As more apps are opened, the task list shifts left, reducing this value.
    /// </summary>
    public double GetAvailableWidthDips()
    {
#if DEBUG
        if (AvailableWidthOverride > 0)
        {
            return AvailableWidthOverride;
        }
#endif

        IntPtr trayHwnd = FindWindow("Shell_TrayWnd", null);
        if (trayHwnd != IntPtr.Zero)
        {
            IntPtr targetHwnd = FindChildWindow(trayHwnd, "MSTaskListWClass");
            if (targetHwnd == IntPtr.Zero)
            {
                targetHwnd = FindChildWindow(trayHwnd, "MSTaskSwWClass");
            }

            if (targetHwnd != IntPtr.Zero && GetWindowRect(targetHwnd, out RECT rect))
            {
                double leftDips = rect.Left / _cachedScale;
                Logger.Info($"[WIDTH_DBG] Found task list: left={rect.Left}px ({leftDips:F1}dip)");
                if (leftDips > 50)
                {
                    return leftDips;
                }
            }
            else
            {
                Logger.Info("[WIDTH_DBG] Task list window not found under Shell_TrayWnd");
            }
        }
        else
        {
            Logger.Info("[WIDTH_DBG] Shell_TrayWnd not accessible — using WorkArea fallback");
        }

        // Fallback: return full work area width
        var da = _cachedDisplayArea ?? DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        return da.WorkArea.Width / _cachedScale;
    }

    private IntPtr FindChildWindow(IntPtr parent, string className)
    {
        string? nullStr = null;
        IntPtr child = FindWindowEx(parent, IntPtr.Zero, className, nullStr);
        if (child != IntPtr.Zero) return child;

        IntPtr current = IntPtr.Zero;
        while ((current = FindWindowEx(parent, current, nullStr, nullStr)) != IntPtr.Zero)
        {
            IntPtr found = FindChildWindow(current, className);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }
}
