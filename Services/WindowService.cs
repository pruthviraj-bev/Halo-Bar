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
///  - Window is permanently docked at the start of the taskbar's free zone —
///    its X and width come from CompactLayoutController (<see cref="HaloX"/> and
///    <see cref="HaloWidth"/>), never from a screen-relative offset. The pill
///    always stays right of the reserved cluster (Start/Search) and left of the tray.
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

    // Sole authority for compact geometry. WindowService only consumes its
    // CurrentWidth and animates on WidthChanged — it never computes compact width.
    private readonly CompactLayoutController _compactLayout;

    // Last profile applied via SetProfile. Used to keep width adaptation from
    // touching the window while the expanded dashboard is showing.
    private WindowProfile _currentProfile = WindowProfile.Collapsed;

    private double _cachedScale = 1.0;
    private DisplayArea? _cachedDisplayArea;

    // X is owned by CompactLayoutController (HaloX); the window is never
    // positioned from a screen-relative constant.

    // Actual taskbar height (DIPs), detected from DisplayArea on init.
    private int _taskbarHeightDips = 48;

    // Last resolved profile target; SetProfile dedupes against this so state changes
    // that keep the same enum (e.g. media toggling while Collapsed) still re-apply.
    private (int Width, int Height)? _lastProfileTarget;

    // Optional width override for the collapsed pill, supplied by PillDashboard
    // (sum of visible card widths). When set, ResolveProfileSize caps the compact
    // width to this value but never exceeds the CompactLayoutController's width.
    private double? _overrideCollapsedWidth;

    /// <summary>
    /// Sets the collapsed pill width override on the fly, or null to restore the
    /// adaptive CompactLayoutController width. Must be called on the UI thread.
    /// </summary>
    public void SetOverrideCollapsedWidth(double? width)
        => _overrideCollapsedWidth = width;

    // Fixed-duration tween for smooth size animation. Position is NOT animated:
    // X is stateless, derived from CompactLayoutController.HaloX on every frame
    // via GetAnchoredPosition(), so no animation can ever restore a stale X.
    // The previous spring simulation settled nondeterministically (0.9–2.4 s,
    // 64–242 frames) and could stall for seconds when the deduped rect stopped
    // invalidating the compositor — replaced by a deterministic cubic ease-out
    // (~350 ms expand / ~300 ms collapse) driven by composition frames.
    private double _currentWidthDip;
    private double _currentHeightDip;
    private double _animFromWidth, _animFromHeight;
    private double _animTargetWidth, _animTargetHeight;
    private long _animStartMs;
    private const double ExpandAnimMs = 350;
    private const double CollapseAnimMs = 300;

    private bool _isAnimating = false;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    private DispatcherQueueTimer? _zOrderTimer;

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _hWinEventHook = IntPtr.Zero;

    // Keeps the managed callback referenced so the GC never collects it while hooked.
    private LowLevelMouseProc? _mouseHookDelegate;
    private IntPtr _mouseHook = IntPtr.Zero;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    /// <summary>Fires (on the UI thread) when a mouse-button press lands outside the dock window.</summary>
    public event EventHandler? MouseClickedOutside;

    public event EventHandler<bool>? FullscreenStateChanged;
    private bool _lastFullscreenActive = false;
    private bool _isHiddenForFullscreen = false;

    // Fullscreen signal debounce. Taskbar/Explorer churn can flap
    // IsFullscreenModeActive() for a few frames; acting on every flap causes a
    // visible hide/show blink. A candidate state must persist for
    // FullscreenConfirmMs before the window actually hides or shows.
    private bool? _pendingFullscreen;
    private double _pendingFullscreenSinceMs;
    private readonly System.Diagnostics.Stopwatch _fsClock = new();
    private static readonly long FullscreenConfirmMs = 200;

    // Last geometry handed to the OS — used to log only real moves, keeping the
    // [MOVE] instrumentation quiet while unchanged frames are re-applied.
    private RectInt32 _lastApplied;

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
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
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

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

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
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Low-level mouse hook: detect clicks landing outside the dock.
    private const int WH_MOUSE_LL   = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    // Suppress DWM drop shadow
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;

    // ── Constructor ────────────────────────────────────────────────────────

    public WindowService(Window window, CompactLayoutController compactLayout)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _compactLayout = compactLayout ?? throw new ArgumentNullException(nameof(compactLayout));
        _compactLayout.WidthChanged += OnCompactWidthChanged;
        _hwnd   = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        
        _appWindow.Changed += AppWindow_Changed;

        _cachedScale       = GetScaleFactor();
        _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        _compactLayout.Scale = _cachedScale;

        _currentWidthDip = _compactLayout.CurrentWidth;
        _currentHeightDip = 48;

        _fsClock.Start();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange) && !_isAnimating)
        {
            _cachedScale = GetScaleFactor();
            _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            _compactLayout.Scale = _cachedScale;
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
        _compactLayout.Scale = _cachedScale;

        _taskbarHeightDips = ComputeTaskbarHeightDips();

        _currentWidthDip = width;
        _currentHeightDip = _taskbarHeightDips;

        int rawWidth  = (int)Math.Round(width              * _cachedScale);
        int rawHeight = (int)Math.Round(_taskbarHeightDips * _cachedScale);
        var start     = GetAnchoredPosition(rawWidth, rawHeight);

        ConfigureBorderless();
        SetAlwaysOnTop(true);
        ApplyGeometry(start.X, start.Y, rawWidth, rawHeight, "InitializeWindow");

        Logger.Info($"InitializeWindow: taskbarHeight={_taskbarHeightDips} dips, anchor=({start.X},{start.Y})");
    }

    private void ConfigureBorderless()
    {
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable   = false;
            p.IsMinimizable = false;
            p.IsMaximizable = false;
        }
    }

    /// <summary>
    /// Applies all DWM and Win32 window behavior attributes.
    /// Must be called BEFORE the window is first shown (while hidden), so the
    /// first presented frame is already borderless, popup, toolwindow and owner-anchored.
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

        // Low-level mouse hook: the dock is WS_EX_NOACTIVATE so "click outside"
        // never changes the foreground window — the mouse hook is the reliable signal.
        _dispatcherQueue = dispatcherQueue;
        _mouseHookDelegate = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, IntPtr.Zero, 0);
        Logger.Info(_mouseHook == IntPtr.Zero
            ? "MouseHook (WH_MOUSE_LL) failed to register."
            : $"MouseHook (WH_MOUSE_LL) registered: 0x{_mouseHook.ToInt64():X}");

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
    /// Temporarily lifts WS_EX_NOACTIVATE while a text field has focus so the dock
    /// can receive keyboard input, then restores it when editing finishes. Restoring
    /// is a safe no-op if the flag is already in the requested state.
    /// </summary>
    public void SetTextInputActive(bool active)
    {
        if (_hwnd == IntPtr.Zero) return;

        int current = GetWindowLong(_hwnd, GWL_EXSTYLE);
        int updated = active
            ? (current & ~WS_EX_NOACTIVATE)
            : (current | WS_EX_NOACTIVATE);
        if (updated == current) return;

        SetWindowLong(_hwnd, GWL_EXSTYLE, updated);
        Logger.Info($"SetTextInputActive({active}): exStyle 0x{current:X} → 0x{updated:X}");
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
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Evaluate fullscreen mode state changes — hide/show AppWindow directly so the acrylic surface is fully removed.
        // Debounced: the state must persist for FullscreenConfirmMs before acting, so transient taskbar-churn flaps
        // (IsFullscreenModeActive() flickering for a few frames) never produce a hide/show blink.
        bool isFullscreen = IsFullscreenModeActive();
        if (isFullscreen != _lastFullscreenActive)
        {
            if (_pendingFullscreen != isFullscreen)
            {
                _pendingFullscreen = isFullscreen;
                _pendingFullscreenSinceMs = _fsClock.Elapsed.TotalMilliseconds;
            }
            else if (_fsClock.Elapsed.TotalMilliseconds - _pendingFullscreenSinceMs >= FullscreenConfirmMs)
            {
                _lastFullscreenActive = isFullscreen;
                _pendingFullscreen = null;
                if (isFullscreen)
                {
                    _isHiddenForFullscreen = true;
                    Logger.Info("[WINDOW] fullscreen confirmed — Hide().");
                    _appWindow.Hide();
                }
                else
                {
                    _isHiddenForFullscreen = false;
                    Logger.Info("[WINDOW] fullscreen exited — Show().");
                    _appWindow.Show();
                    // Re-assert TOPMOST after Show(), since Show() may reset Z-order
                    SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                FullscreenStateChanged?.Invoke(this, isFullscreen);
            }
        }
        else if (_pendingFullscreen != null)
        {
            // Signal returned to the committed state before the confirm window —
            // this was a transient flap. Drop the candidate without acting.
            _pendingFullscreen = null;
        }

        // Skip SetWindowPos when hidden — window is not visible
        if (_isHiddenForFullscreen) return;
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            ForceAboveTaskbar();
        }
    }

    /// <summary>
    /// Fires on every mouse-button press system-wide (low-level hook). When the
    /// press lands outside the dock window we raise <see cref="MouseClickedOutside"/>
    /// on the UI thread so the expanded island can collapse immediately.
    /// </summary>
    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                if (lParam != IntPtr.Zero)
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (GetWindowRect(_hwnd, out RECT rect)
                        && (data.pt.X < rect.Left || data.pt.X > rect.Right
                            || data.pt.Y < rect.Top || data.pt.Y > rect.Bottom))
                    {
                        _dispatcherQueue?.TryEnqueue(() => MouseClickedOutside?.Invoke(this, EventArgs.Empty));
                    }
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    ~WindowService()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
        }
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

    // ── Profile counters (temporary — remove after profiling).
    private int _profileFrameCount;
    private int _profileAppliedCount;
    private long _profileStartMs;
    private long _profileFirstFrameMs;

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
        // Robustness guard: if a previous tween is still flagged as running but
        // its fixed wall-clock duration has already elapsed, the composition
        // loop must have starved on fully-deduped frames before the settle
        // condition could run (the dedupe keep-alive is an indirect
        // invalidation and is not guaranteed). Snap the stale state to its own
        // target so this retarget starts from correct geometry instead of a
        // frozen mid-animation size.
        if (_isAnimating)
        {
            long staleElapsed = Environment.TickCount64 - _animStartMs;
            double staleDuration = _animTargetHeight >= _animFromHeight ? ExpandAnimMs : CollapseAnimMs;
            if (staleElapsed >= staleDuration)
            {
                _currentWidthDip = _animTargetWidth;
                _currentHeightDip = _animTargetHeight;
                _isAnimating = false;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            }
        }

        // Retarget from the CURRENT geometry: an interrupt mid-animation simply
        // re-bases the tween (no stacked/overlapping loops — the Rendering
        // subscription is created once and never duplicated).
        _animFromWidth = _currentWidthDip;
        _animFromHeight = _currentHeightDip;
        _animTargetWidth = targetWidth;
        _animTargetHeight = targetHeight;
        _animStartMs = Environment.TickCount64;

        // Profile counters reset per segment (a retarget starts a new segment)
        // so the settled report reflects this animation only.
        _profileFrameCount = 0;
        _profileAppliedCount = 0;
        _profileStartMs = _animStartMs;
        _profileFirstFrameMs = 0;

        Logger.Info($"[PROFILE] StartSizeAnimation → {targetWidth}×{targetHeight} ms={_profileStartMs}");

        if (!_isAnimating)
        {
            _cachedScale = GetScaleFactor();
            _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            _taskbarHeightDips = ComputeTaskbarHeightDips();

            _isAnimating = true;
            _stopwatch.Restart();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        }
    }

    /// <summary>
    /// Resolves a WindowProfile to its logical DIP target size at this moment.
    /// Compact geometry is stateless and owned by CompactLayoutController:
    /// width comes from its CurrentWidth, height matches the taskbar.
    /// It never depends on which widget is active, so compact→compact changes
    /// never produce a different target (SetProfile dedupes them into no-ops).
    /// </summary>
    public (int Width, int Height) ResolveProfileSize(WindowProfile profile)
    {
        switch (profile)
        {
            case WindowProfile.Collapsed:
                // Compact pill — adaptive width (controller) × taskbar height.
                double effective = _overrideCollapsedWidth.HasValue
                    ? Math.Min(_overrideCollapsedWidth.Value, _compactLayout.CurrentWidth)
                    : _compactLayout.CurrentWidth;
                return ((int)Math.Round(effective), _taskbarHeightDips);

            case WindowProfile.Expanded:
                // Premium wide grid dashboard flyout
                return (800, 664);

            default:
                return profile.ToDimensions();
        }
    }

    /// <summary>
    /// Current compact pill dimensions in DIPs (adaptive width from the
    /// controller × live taskbar height). Exposed for the legacy ClipboardWidget
    /// preview collapse; the only caller.
    /// </summary>
    public (int Width, int Height) CompactSize => ResolveProfileSize(WindowProfile.Collapsed);

    /// <summary>
    /// Animates to a predefined WindowProfile using real taskbar-relative dimensions.
    /// IslandController is the only permitted caller.
    /// Deduplicates on the resolved target size, so repeated calls with the same
    /// profile (e.g. a compact→compact content change) are no-ops — the window
    /// geometry never changes for compact widget switches.
    /// </summary>
    public void SetProfile(WindowProfile profile)
    {
        _currentProfile = profile;
        var (width, height) = ResolveProfileSize(profile);

        if (_lastProfileTarget is (int lastW, int lastH) && lastW == width && lastH == height)
            return;

        _lastProfileTarget = (width, height);
        Logger.Info($"[PROFILE] SetProfile({profile}) → ({width}×{height}) ms={Environment.TickCount64}");
        StartSizeAnimation(width, height);
    }

    /// <summary>
    /// Consumes the controller's width announcement. Applies the new compact
    /// width only while the window is in the compact profile — an expanded
    /// dashboard keeps its size, and the new width is picked up on collapse.
    /// Animate once and keep the dedupe target in sync so later SetProfile
    /// calls don't re-animate to a stale width.
    /// </summary>
    private void OnCompactWidthChanged(object? sender, double newWidth)
    {
        if (_currentProfile != WindowProfile.Collapsed) return;

        int width = (int)Math.Round(newWidth);
        StartSizeAnimation(width, _taskbarHeightDips);
        _lastProfileTarget = (width, _taskbarHeightDips);
    }

    // ── Rendering loop ─────────────────────────────────────────────────────

    private void OnRendering(object? sender, object e)
    {
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();

        if (dt > 0.03) dt = 0.03;
        if (dt <= 0) return;

        _profileFrameCount++;
        long now = Environment.TickCount64;

        if (_profileFirstFrameMs == 0)
        {
            _profileFirstFrameMs = now;
            Logger.Info($"[PROFILE] Rendering first frame: {now} gapFromStart={now - _profileStartMs}ms dt={dt * 1000:F1}ms");
        }

        // Deterministic fixed-duration cubic ease-out, evaluated against
        // wall-clock elapsed time since StartSizeAnimation (or the last retarget).
        // The animation therefore always ends at the target (~350 ms expand /
        // ~300 ms collapse) — it cannot linger for seconds due to spring convergence.
        double elapsed = now - _animStartMs;
        double duration = _animTargetHeight >= _animFromHeight ? ExpandAnimMs : CollapseAnimMs;
        double t = Math.Clamp(elapsed / duration, 0.0, 1.0);
        double eased = 1.0 - Math.Pow(1.0 - t, 3.0);

        _currentWidthDip = _animFromWidth + (_animTargetWidth - _animFromWidth) * eased;
        _currentHeightDip = _animFromHeight + (_animTargetHeight - _animFromHeight) * eased;

        int rawW = (int)Math.Round(_currentWidthDip * _cachedScale);
        int rawH = (int)Math.Round(_currentHeightDip * _cachedScale);
        int finalW = (int)Math.Round(_animTargetWidth * _cachedScale);
        int finalH = (int)Math.Round(_animTargetHeight * _cachedScale);

        // Settle when the tween is complete OR the rounded rect has visually
        // reached the target. Ease-out flattens the tail, so waiting for
        // sub-pixel convergence is what let the old spring stall for seconds.
        // Settling early also guarantees the settle frame itself applies
        // geometry, keeping the compositor producing frames until we unsubscribe.
        if (t >= 1.0 || (rawW == finalW && rawH == finalH))
        {
            _currentWidthDip = _animTargetWidth;
            _currentHeightDip = _animTargetHeight;
            var finalPos = GetAnchoredPosition(finalW, finalH);

            ApplyGeometry(finalPos.X, finalPos.Y, finalW, finalH, "Settle");
            _isAnimating = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            Logger.Info($"[PROFILE] Animation settled: frames={_profileFrameCount} applied={_profileAppliedCount} duration={now - _profileStartMs}ms");
            return;
        }

        if (_profileFrameCount % 10 == 0)
            Logger.Info($"[PROFILE] Rendering frame {_profileFrameCount}: ms={now} dt={dt * 1000:F1}ms");

        var pos = GetAnchoredPosition(rawW, rawH);
        ApplyGeometry(pos.X, pos.Y, rawW, rawH, "Frame");
    }

    // ── Anchoring ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pins the window's bottom edge to the screen bottom or taskbar top depending on profile state.
    /// As height grows (expansion), the window rises upward and clears the taskbar.
    /// X is owned by CompactLayoutController (<see cref="HaloX"/>), so the pill
    /// always starts in the free zone, right of the reserved cluster.
    /// </summary>
    private PointInt32 GetAnchoredPosition(int rawWidthPhysical, int rawHeightPhysical)
    {
        var da = _cachedDisplayArea ?? DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        double scale = _cachedScale;

        double currentHeightDips = rawHeightPhysical / scale;
        double collapsedHeight = _taskbarHeightDips;
        double expandedHeight = 664;

        double progress = 0;
        if (expandedHeight > collapsedHeight)
        {
            progress = (currentHeightDips - collapsedHeight) / (expandedHeight - collapsedHeight);
            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;
        }

        double bottomOffsetDips = progress * _taskbarHeightDips;

        int x = da.WorkArea.X + (int)Math.Round(_compactLayout.HaloX * scale);
        int screenBottom = da.OuterBounds.Y + da.OuterBounds.Height;
        int y = screenBottom - rawHeightPhysical - (int)Math.Round(bottomOffsetDips * scale);

        return new PointInt32(x, y);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// The single funnel for every window geometry write. Position and size are
    /// always handed to the OS together in one MoveAndResize, so X and width can
    /// never diverge. X always comes from the controller's HaloX via
    /// <see cref="GetAnchoredPosition"/> — the [MOVE] log makes any path that
    /// bypasses it immediately visible (haloX would not equal finalX).
    /// </summary>
    private void ApplyGeometry(int x, int y, int widthPx, int heightPx, string origin)
    {
        // Identical rect — skip the Win32 call entirely. Re-issuing MoveAndResize
        // for the same effective rectangle would churn the OS window manager and
        // force a redundant DWM repaint of an unchanged window.
        if (_lastApplied.X == x && _lastApplied.Y == y
            && _lastApplied.Width == widthPx && _lastApplied.Height == heightPx)
        {
            // While animating, a fully-deduped frame would stop invalidating the
            // window, and CompositionTarget.Rendering would stop firing before
            // the tween's settle condition is ever evaluated. Request a cheap
            // layout invalidation to keep the composition loop alive.
            if (_isAnimating)
            {
                _window.Content.InvalidateArrange();
            }
            return;
        }

        // Profile counter: actual MoveAndResize calls (deduped skips excluded).
        _profileAppliedCount++;

        _lastApplied = new RectInt32(x, y, widthPx, heightPx);

        // Per-frame animation steps are deliberately NOT logged: a spring settles
        // over ~30-60 frames and every log line is a file write. The final
        // "Settle" frame and all non-animation origins still log, so every real
        // geometry change stays visible without disk I/O per animation frame.
        if (origin != "Frame")
        {
            Logger.Info($"[MOVE] {origin}: haloX={_compactLayout.HaloX:F1} finalX={x} finalY={y} w={widthPx} h={heightPx}");
        }

        _appWindow.MoveAndResize(new RectInt32(x, y, widthPx, heightPx));
    }

    private double GetScaleFactor()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi / 96.0;
    }

    // Shell UI host processes that own full-screen borderless overlays
    // (Start, Search, Widgets, taskbar flyouts). These must never be treated
    // as fullscreen, even though their windows cover the monitor.
    private static readonly string[] ShellHostProcesses =
    {
        "explorer",
        "StartMenuExperienceHost",
        "SearchHost",
        "Widgets",
        "ShellExperienceHost",
    };

    // Shell-host check cache: the z-order guard runs IsFullscreenModeActive every
    // 150 ms, and the common case (any app window in the foreground) reaches
    // IsShellHostWindow — a Process.GetProcessById().ProcessName query that used
    // to allocate a Process object every single tick. Re-query at most once per
    // second per PID; a recycled PID within the TTL only delays a classification
    // by ≤1 s, which is invisible to the debounced fullscreen logic.
    private uint _shellHostPid;
    private bool _shellHostResult;
    private long _shellHostCheckedAtMs = long.MinValue;
    private static readonly long ShellHostCheckTtlMs = 1000;

    private bool IsShellHostWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        long now = _fsClock.ElapsedMilliseconds;
        if (pid == _shellHostPid && now - _shellHostCheckedAtMs < ShellHostCheckTtlMs)
        {
            return _shellHostResult;
        }

        _shellHostResult = false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            foreach (string name in ShellHostProcesses)
            {
                if (string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                {
                    _shellHostResult = true;
                    break;
                }
            }
        }
        catch (ArgumentException)
        {
            // Process already exited — not a shell host.
        }
        _shellHostPid = pid;
        _shellHostCheckedAtMs = now;
        return _shellHostResult;
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
            // Never treat the taskbar, desktop, or Explorer's shell UI as
            // fullscreen. Explorer's XAML islands (XamlExplorerHostIslandWindow)
            // cover the monitor and have no caption, so without this check the
            // pill would hide whenever Start/Search/Widgets/taskbar overlays take
            // foreground — the visible hide/show blink during taskbar churn.
            StringBuilder sb = new StringBuilder(256);
            GetClassName(fgHwnd, sb, sb.Capacity);
            string className = sb.ToString();
            if (className == "Shell_TrayWnd" || className == "Progman" || className == "WorkerW"
                || className == "XamlExplorerHostIslandWindow" || IsShellHostWindow(fgHwnd))
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
}
