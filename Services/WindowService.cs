using System;
using System.Collections.Generic;
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
    // driven by composition frames. Pass 35 (final V1 motion): expand 260 → 280 ms,
    // collapse 220 → 230 ms, and the fresh-segment curve becomes the simple
    // native Windows easeOutCubic 1-(1-t)³ (replacing Pass 33's aggressive
    // cubic-bezier(0.16, 1.0, 0.3, 1.0), which read as mechanical). Retarget
    // segments keep the generalized cubic seeded with the interrupted
    // velocity, so Pass 9 velocity-aware reversal continuity is preserved.
    // HALO_P26_EXPAND_MS / HALO_P26_COLLAPSE_MS override the durations for A/B
    // testing without a rebuild.
    private double _currentWidthDip;
    private double _currentHeightDip;
    private double _animFromWidth, _animFromHeight;
    private double _animTargetWidth, _animTargetHeight;
    private long _animStartMs;
    private const double DefaultExpandAnimMs = 280;
    private const double DefaultCollapseAnimMs = 230;
    private static readonly double ExpandAnimMs = ParseDurationMs("HALO_P26_EXPAND_MS", DefaultExpandAnimMs);
    private static readonly double CollapseAnimMs = ParseDurationMs("HALO_P26_COLLAPSE_MS", DefaultCollapseAnimMs);

    private bool _isAnimating = false;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    private DispatcherQueueTimer? _zOrderTimer;
    private DispatcherQueueTimer? _dragRouteTimer;

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _hWinEventHook = IntPtr.Zero;

    // Keeps the managed callback referenced so the GC never collects it while hooked.
    private LowLevelMouseProc? _mouseHookDelegate;
    private IntPtr _mouseHook = IntPtr.Zero;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    // PASS 47 (GOAL 2): the native OLE drop target that routes Explorer file
    // drags into the File Shelf (the XAML AllowDrop pipeline does not deliver
    // them on this bridge-hosted, region-clipped window). Held here so the CCW
    // stays alive for the lifetime of the window.
    private Helpers.OleDropTarget? _oleDropTarget;

    // PASS 53: dedicated invisible drop-target overlay (see PillDropOverlay).
    // Covers the live SetWindowRgn shape BELOW the main HWND so Explorer drags
    // resolve to a non-alpha-0 window at the pill and reach the OLE target.
    private PillDropOverlay? _dropOverlay;

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

    // Pass 13 diagnostic: HALO_P13_YOFFSET (DIPs) × scale = physical pixels
    // added to the final Y inside ApplyGeometry only. The whole existing motion
    // (initialize, every animation frame, settle) is displaced consistently
    // because ApplyGeometry is the single geometry funnel. 0 by default.
    private int _p13OffsetPx;

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out int crKey, out byte bAlpha, out uint dwFlags);

    // PASS 38 (GOAL 1) forensics: DWMWA_EXTENDED_FRAME_BOUNDS is the window
    // rect WITHOUT the DWM drop shadow (documented: GetWindowRect includes the
    // shadow on Vista+). extFrame != windowRect → a shadow/frame is present.
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint LWA_COLORKEY = 0x00000001;

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

    // ── PASS 40 (GOAL 1/4): HWND attribution + process window census ──────
    // GetWindowLongPtr covers GWL_STYLE/GWL_EXSTYLE/GWL_HWNDPARENT with the
    // 64-bit-safe width; EnumWindows + GetWindowThreadProcessId enumerate every
    // top-level window of this process so a rogue second HWND (backdrop host,
    // XAML island helper, stale popup, duplicate MainWindow) is ruled out as
    // the rectangle's painter.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPoint(IntPtr hWnd, POINT pt);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // PASS 42: the desktop bridge test hides the Microsoft.UI.Content.
    // DesktopChildSiteBridge child window (SW_HIDE) — nothing is destroyed, the
    // main HWND stays alive; only the compositor surface hosting the XAML
    // island stops presenting.
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_SHOWNOACTIVATE = 4; // PASS 44: restore without stealing focus

    // PASS 53: the PillDropOverlay is a raw RegisterClassW + CreateWindowExW
    // window (not a WinUI island) so it exists on the true top-level HWND
    // layer with a native hit-test OLE can see.
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    // PASS 43: force a compositor/window redraw WITHOUT changing geometry —
    // InvalidateRect + RedrawWindow + DwmFlush re-present the window surface so
    // the binary tests measure the real post-intervention desktop pixels.
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;

    private const int GWL_HWNDPARENT = -8;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    // PASS 39 (GOAL 1): the real applied region is read back from the OS via
    // GetWindowRgn — the live truth for what is visible + hit-testable.
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool GetRgnBox(IntPtr rgn, out RECT rect);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(IntPtr hrgn, int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_HWNDPREV = 3;

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
    private const uint SWP_NOZORDER = 0x0004; // Keep current z-order
    private const uint SWP_NOACTIVATE = 0x0010; // Don't steal focus when repositioning
    private const uint SWP_FRAMECHANGED = 0x0020; // Recalculate the NC frame/shadow after style changes

    private const int GWL_STYLE         = -16;
    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TOOLWINDOW  = 0x00000080; // Hides from Alt+Tab & taskbar button
    private const int WS_EX_NOACTIVATE  = 0x08000000; // Prevents focus steal on click
    private const int WS_EX_LAYERED     = 0x00080000; // No DWM shadow; per-pixel alpha
    private const int WS_EX_TRANSPARENT = 0x00000020; // Mouse events pass to windows below (WS_EX_LAYERED windows)
    private const int WS_EX_TOPMOST     = 0x00000008; // PASS 53: overlay lives in the topmost band

    // PASS 39 (GOAL 1) style-bit decoding for the [P39-SURFACE] dump.
    private const int WS_POPUP      = unchecked((int)0x80000000);
    private const int WS_CHILD      = unchecked((int)0x40000000);
    private const int WS_CAPTION    = 0x00C00000; // WS_BORDER | WS_DLGFRAME
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_BORDER     = 0x00800000;
    private const int WS_DLGFRAME   = 0x00400000;
    // GetWindowRgn return values.
    private const int RGN_ERROR         = 0;
    private const int RGN_NULLREGION    = 1;
    private const int RGN_SIMPLEREGION  = 2;
    private const int RGN_COMPLEXREGION = 3;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE   = -2;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Low-level mouse hook: detect clicks landing outside the dock.
    private const int WH_MOUSE_LL   = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    // PASS 53 drop-target overlay window.
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE    = 3;
    private const int WM_ERASEBKGND    = 0x0014;
    private const double DropOverlayRegionRadiusDip = 24; // matches MainWindow RegionRadiusDip
    private const string DropOverlayClass = "DynowinDropOverlay";

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    // Suppress DWM drop shadow
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;
    // PASS 46 (GOAL 1): DONOTROUND is the PRODUCTION corner preference. The
    // visible rounding is owned by SetWindowRgn (CreateRoundRectRgn with
    // RegionRadiusDip) — DWMWCP_ROUND made DWM additionally draw its own
    // rounded-corner fringe AROUND the region shape (the dark edge P45 proved
    // follows the region, is content-independent, and has no envelope seam).
    private const int DWMWCP_DONOTROUND = 1;

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

        // PASS 32: stop the animation render loop when the window closes. A
        // final CompositionTarget.Rendering tick against the already-closed
        // window used to throw "The WinUI Desktop Window object has already
        // been closed" from ApplyGeometry during app teardown (observed after
        // the diagnostic auto-cycles exit). Unsubscribing here means the tick
        // never fires; ApplyGeometry also guards against it defensively.
        _window.Closed += (_, _) => StopAnimations();

        _cachedScale       = GetScaleFactor();
        _cachedDisplayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        _compactLayout.Scale = _cachedScale;

        _currentWidthDip = _compactLayout.CurrentWidth;
        _currentHeightDip = 48;

        _fsClock.Start();

        // Pass 11: confirm the primary monitor's refresh rate so the frame-pacing
        // thresholds are interpreted correctly (144 Hz → 6.94 ms per frame).
        MotionDiagnostics.LogRefreshRate();

        // Pass 11.5: passive idle/static cadence probe (env HALO_P11_5=1).
        MotionDiagnostics.EnableCadenceProbe();
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

        // Pass 13: fix the physical-pixel displacement once per session (scale is
        // stable during a run). offset=0 → control; ApplyGeometry's y += 0 no-op.
        _p13OffsetPx = (int)Math.Round(MotionDiagnostics.PositionYOffsetDips * _cachedScale);

        ConfigureBorderless();
        SetAlwaysOnTop(true);

        // Stable-window production (Pass 23/24 validated, Pass 25 promoted): the
        // HWND is pre-sized ONCE to the fixed expanded envelope — the expanded
        // dashboard plus the taskbar-lift strip, bottom edge at the screen
        // bottom so the compact pill sits exactly where it always has. The HWND
        // is NEVER resized again: the animation runs inside the window (clip
        // reveal + pill translate + rounded region driven by MainWindow),
        // eliminating the per-frame native surface regrowth that produced the
        // black leading-edge band. _currentWidthDip/_currentHeightDip track the
        // LOGICAL pill/dashboard dimensions (not the HWND) so the shared-progress
        // tween and ResolveProfileSize stay in sync with the visual stage.
        var expanded = ResolveProfileSize(WindowProfile.Expanded);
        int expW = (int)Math.Round(expanded.Width * _cachedScale);
        int expH = (int)Math.Round(expanded.Height * _cachedScale);
        int lift = (int)Math.Round(_taskbarHeightDips * _cachedScale);
        int winH = expH + lift;
        var da = _cachedDisplayArea ?? DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        int fx = da.WorkArea.X + (int)Math.Round(_compactLayout.HaloX * _cachedScale);
        int fy = da.OuterBounds.Y + da.OuterBounds.Height - winH;
        _currentWidthDip = ResolveProfileSize(WindowProfile.Collapsed).Width;
        _currentHeightDip = _taskbarHeightDips;
        _appWindow.MoveAndResize(new RectInt32(fx, fy, expW, winH));
        _lastApplied = new RectInt32(fx, fy, expW, winH);
        Logger.Info($"[WINDOW] FixedInit rect=({fx},{fy},{expW}x{winH}) taskbarDips={_taskbarHeightDips} tMs={Environment.TickCount64}");
        Logger.Info($"[WINDOW] MotionDurations expandMs={ExpandAnimMs:F0} collapseMs={CollapseAnimMs:F0} curve=easeOutCubic v0={MotionDiagnostics.FreshV0:F1}");

        // Pass 26: AppWindow.MoveAndResize can reset the DWM non-client
        // attributes applied in ApplyDwmAttributes — re-assert here so the
        // pre-Activate state is already shadow/border-free (the post-Activate
        // re-assert and the 150 ms guard close the remaining gaps).
        ReassertDwmAttributes();

        Logger.Info($"InitializeWindow: taskbarHeight={_taskbarHeightDips} dips, anchor=({start.X},{start.Y})");

        if (MotionDiagnostics.EnableP13)
        {
            // Diagnostic POSITION line: normalY = the anchor math's Y (source of
            // truth), effectiveY = what was actually handed to the OS.
            string mode = _p13OffsetPx == 0 ? "control" : "offset";
            Logger.Info($"[MOTION-P13] POSITION mode={mode} offset={MotionDiagnostics.PositionYOffsetDips} " +
                        $"normalY={start.Y} effectiveY={start.Y + _p13OffsetPx} x={start.X} w={rawWidth} h={rawHeight}");
            LogP13Region("startup-compact");
        }
    }

    private void ConfigureBorderless()
    {
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable   = false;
            p.IsMinimizable = false;
            p.IsMaximizable = false;
            // PASS 36: tell DWM this window has NO non-client frame or title
            // bar. The window style was already WS_POPUP, but the presenter
            // kept its default frame configuration — on some machines DWM still
            // renders the non-client drop shadow / frame for the fixed
            // 1000×890 HWND envelope (the persistent dark rectangle the user
            // reports, which survived WS_EX_LAYERED and the region because the
            // shadow is drawn for the WINDOW RECT, not the visible region).
            // SetBorderAndTitleBar(false, false) is the documented source-level
            // fix for borderless WinUI 3 overlay windows.
            p.SetBorderAndTitleBar(false, false);
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
        ApplyDwmCornerPreference();
        SuppressBorder();
        SuppressShadow();
        ApplyToolWindowStyle();

        // Anchor to the taskbar window as its owner so we always render on top of it.
        // Pass 12 diagnostic: HALO_NOOWNER=1 skips ownership so the composition-rate
        // effect of GWLP_HWNDPARENT (taskbar 60 Hz path vs panel 144 Hz) can be
        // isolated in a controlled A/B run. Default behavior is unchanged.
        if (MotionDiagnostics.DisableTaskbarOwnership)
        {
            Logger.Info("[P12] HALO_NOOWNER=1 — taskbar ownership skipped (diagnostic).");
        }
        else
        {
            IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
            if (taskbarHwnd != IntPtr.Zero)
            {
                SetWindowLongPtr(_hwnd, -8, taskbarHwnd); // GWLP_HWNDPARENT = -8
                Logger.Info($"SetWindowLongPtr owner to taskbar (0x{taskbarHwnd.ToInt64():X}) succeeded.");
                // PASS 38 (GOAL 1): ownership is also window data — re-frame so
                // DWM recomputes the frame for the new owner chain.
                ReFrameWindow();
            }
        }

        // Initialize WinEvent hook for foreground window changes
        _winEventDelegate = new WinEventDelegate(WinEventProc);
        _hWinEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        Logger.Info("WinEventHook (EVENT_SYSTEM_FOREGROUND) registered successfully.");

        // Low-level mouse hook: the dock is WS_EX_NOACTIVATE so "click outside"
        // never changes the foreground window — the mouse hook is the reliable signal.
        // Pass 15 diagnostic: HALO_P15_NOMOUSE=1 skips registration so the idle
        // render stream can be attributed to the hook vs. other sustainers.
        _dispatcherQueue = dispatcherQueue;
        if (MotionDiagnostics.P15NoMouseHook)
        {
            Logger.Info("[P15] mouse hook disabled (HALO_P15_NOMOUSE=1, diagnostic).");
        }
        else
        {
            _mouseHookDelegate = MouseHookProc;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, IntPtr.Zero, 0);
            Logger.Info(_mouseHook == IntPtr.Zero
                ? "MouseHook (WH_MOUSE_LL) failed to register."
                : $"MouseHook (WH_MOUSE_LL) registered: 0x{_mouseHook.ToInt64():X}");
        }

        StartZOrderGuard(dispatcherQueue);
        ForceAboveTaskbar(); // Immediate push to top of TOPMOST z-order.
    }

    /// <summary>
    /// PASS 46 (GOAL 1): sets the DWM corner preference to DONOTROUND. The
    /// visible shape rounding is owned by the SetWindowRgn region
    /// (CreateRoundRectRgn, RegionRadiusDip=24 — matches the design's
    /// CornerRadius), so DWM must NOT round the window itself: DWMWCP_ROUND made
    /// Windows 11 DWM draw a dark rounded-corner fringe around the region shape
    /// (P45: boundaryFollows=shape, surfaceDependent=false — present with fully
    /// transparent content, no envelope seam). DONOTROUND removes it while the
    /// region keeps the true rounded silhouette and true transparency outside.
    /// </summary>
    private void ApplyDwmCornerPreference()
    {
        int pref = DWMWCP_DONOTROUND;
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

    private static bool _dwmReassertLogged;

    /// <summary>
    /// Re-asserts the DWM non-client window attributes (DONOTROUND corner
    /// preference, no border, no shadow). Kept as cheap defensive insurance
    /// only: the guard tick re-applies them because MoveAndResize/presenter
    /// changes can reset DWM attributes. PASS 46: the corner preference is now
    /// DONOTROUND — DWM must not add its own rounded-corner fringe around the
    /// SetWindowRgn region shape.
    /// </summary>
    public void ReassertDwmAttributes()
    {
        ApplyDwmCornerPreference();
        SuppressBorder();
        SuppressShadow();
        if (!_dwmReassertLogged)
        {
            _dwmReassertLogged = true;
            Logger.Info("[WINDOW] DWM non-client attributes re-asserted (DONOTROUND corners/border/shadow) — guard tick active.");
        }
    }

    // ── PASS 42 — black-rectangle FINAL OWNER isolation (diagnostic only) ──
    // Hard binary test NO_DWM_FRAME: temporarily disable the DWM non-client
    // frame on the LIVE HWND and log every attribute before/after so the effect
    // (or lack of it) is proven on the real desktop, never assumed. If the
    // black rectangle disappears ⇒ DWM non-client rendering is the source; if
    // it persists ⇒ the source is the window surface / bridge / compositor.

    /// <summary>
    /// PASS 42 (GOAL 1): applies the NO_DWM_FRAME diagnostic — NCRENDERING_POLICY
    /// DISABLED, WINDOW_CORNER_PREFERENCE DONOTROUND, BORDER_COLOR NONE — and
    /// logs each attribute + the extended-frame-bounds before and after. The
    /// "after" values are read back from the OS, not assumed from the call.
    /// Env-gated: only runs under HALO_P42_NO_DWM_FRAME=1.
    /// </summary>
    public void ApplyP42NoDwmFrame()
    {
        try
        {
            string AttrString()
            {
                int corner = -1, border = -1, policy = -1;
                bool cornerOk = DwmGetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out corner, sizeof(int)) == 0;
                bool borderOk = DwmGetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, out border, sizeof(int)) == 0;
                bool policyOk = DwmGetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, out policy, sizeof(int)) == 0;
                string ext = "n/a";
                if (DwmGetWindowAttribute(_hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT e, Marshal.SizeOf<RECT>()) == 0)
                    ext = $"({e.Left},{e.Top},{e.Right - e.Left}x{e.Bottom - e.Top})";
                return $"corner=0x{corner:X}(ok={cornerOk}) border=0x{border:X}(ok={borderOk}) policy=0x{policy:X}(ok={policyOk}) extFrame={ext}";
            }

            Logger.Info($"[P42-DWM] BEFORE {AttrString()} hwnd=0x{_hwnd.ToInt64():X}");
            int none = DWMWA_COLOR_NONE;
            int corner = DWMWCP_DONOTROUND;
            int policy = DWMNCRP_DISABLED;
            int r1 = DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
            int r2 = DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int r3 = DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            Logger.Info($"[P42-DWM] SET ncrPolicy={r1} corner={r2} border={r3}");
            Logger.Info($"[P42-DWM] AFTER {AttrString()}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P42-DWM] apply failed", ex);
        }
    }

    /// <summary>
    /// PASS 42 (GOAL 1): hard binary test NO_CONTENT_BRIDGE. Enumerates every
    /// child of the main HWND (the Microsoft.UI.Content.DesktopChildSiteBridge
    /// is the one that hosts the XAML compositor surface) and hides it with
    /// ShowWindow(SW_HIDE). Nothing is destroyed and the main HWND stays alive.
    /// Rectangle disappears ⇒ the bridge/compositor surface is the source;
    /// persists ⇒ top-level HWND/DWM/non-client. Env-gated: HALO_P42_NO_CONTENT_BRIDGE=1.
    /// </summary>
    public void HideP42ContentBridge()
    {
        try
        {
            var children = new List<(IntPtr hwnd, string cls, RECT rc, int style, int ex, bool visible)>();
            EnumChildWindows(_hwnd, (h, l) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();
                GetWindowRect(h, out RECT rc);
                int style = GetWindowLong(h, GWL_STYLE);
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                children.Add((h, cls, rc, style, ex, IsWindowVisible(h)));
                return true;
            }, IntPtr.Zero);

            Logger.Info($"[P42-BRIDGE] mainHwnd=0x{_hwnd.ToInt64():X} childCount={children.Count}");
            foreach (var (h, cls, rc, style, ex, visible) in children)
                Logger.Info($"[P42-BRIDGE] CHILD hwnd=0x{h.ToInt64():X} class={cls} rect=({rc.Left},{rc.Top},{rc.Right - rc.Left}x{rc.Bottom - rc.Top}) " +
                            $"style=0x{style:X} ex=0x{ex:X} visible={visible}");

            int hidden = 0;
            foreach (var (h, cls, rc, style, ex, visible) in children)
            {
                if (!cls.Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase)) continue;
                bool ok = ShowWindow(h, SW_HIDE);
                Logger.Info($"[P42-BRIDGE] HIDE hwnd=0x{h.ToInt64():X} class={cls} ret={ok} nowVisible={IsWindowVisible(h)}");
                hidden++;
            }
            Logger.Info($"[P42-BRIDGE] hidBridges={hidden}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P42-BRIDGE] hide failed", ex);
        }
    }

    // ── PASS 43 — binary-test primitives (apply/restore/redraw) ────────────
    // Each binary test in MainWindow.RunP42BinaryTests needs an idempotent
    // apply + an exact restore so the next test starts from a clean baseline.
    // These three primitives are the only surface they touch.

    private IntPtr _p42BridgeHwnd = IntPtr.Zero;

    private IntPtr FindP42Bridge()
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(_hwnd, (h, l) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// PASS 43: show or hide the Microsoft.UI.Content.DesktopChildSiteBridge
    /// child (SW_SHOW / SW_HIDE). Nothing is destroyed; the main HWND stays
    /// alive and the XAML/content infrastructure stays fully intact. Logs the
    /// result so the state change is provable.
    /// </summary>
    public void SetP42BridgeVisible(bool visible)
    {
        try
        {
            if (_p42BridgeHwnd == IntPtr.Zero) _p42BridgeHwnd = FindP42Bridge();
            IntPtr h = _p42BridgeHwnd;
            if (h == IntPtr.Zero)
            {
                Logger.Info("[P42-BRIDGE] no DesktopChildSiteBridge child found");
                return;
            }
            bool ret = ShowWindow(h, visible ? SW_SHOW : SW_HIDE);
            Logger.Info($"[P42-BRIDGE] {(visible ? "SHOW" : "HIDE")} hwnd=0x{h.ToInt64():X} ret={ret} nowVisible={IsWindowVisible(h)}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P42-BRIDGE] set-visible failed", ex);
        }
    }

    // ── PASS 47 (GOAL 2) — native OLE drop target ──────────────────────────
    // Explorer file drags never surface as XAML DragEnter on this window (the
    // XAML tree is hosted in the DesktopChildSiteBridge child of a fixed,
    // region-clipped, layered HWND), so the drag signal is received by a real
    // OLE IDropTarget registered on the Halo HWND AND its bridge child — the
    // deepest window the OLE hit-test resolves. DragEnter/DragOver only probe
    // the payload for shell-file formats (never resolve); Drop resolves paths
    // and routes them to the File Shelf.

    private List<IntPtr> EnumerateBridgeChildren()
    {
        var bridges = new List<IntPtr>();
        EnumChildWindows(_hwnd, (h, l) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase))
                bridges.Add(h);
            return true;
        }, IntPtr.Zero);
        return bridges;
    }

    /// <summary>
    /// Registers the native OLE drop target on the Halo HWND and its
    /// DesktopChildSiteBridge child(ren). Idempotent. Must run after the first
    /// Activate so the bridge child exists and so this registration is the last
    /// (winning) one on the top-level HWND. Drag signals are routed to the File
    /// Shelf via IslandController.
    /// </summary>
    public void ArmOleDropTarget()
    {
        try
        {
            if (_oleDropTarget != null) return;

            Helpers.OleDropTarget.EnsureOleInitialized();
            ApplyOleStyleDiagnostics();
            var target = new Helpers.OleDropTarget(
                _dispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            target.FileDragEntered += (_, _) =>
            {
                Logger.Info("[DRAG-EVENT] FileDragEntered raised → IslandController.NotifyFileDragEnter");
                App.IslandController.NotifyFileDragEnter();
            };
            target.FileDragLeft += (_, _) =>
            {
                Logger.Info("[DRAG-EVENT] FileDragLeft raised → IslandController.NotifyFileDragLeave");
                App.IslandController.NotifyFileDragLeave();
            };
            target.FilesDropped += (_, paths) =>
            {
                Logger.Info($"[DRAG-EVENT] FilesDropped raised ({paths.Length} path(s)) → IslandController.NotifyFilesDropped");
                App.IslandController.NotifyFilesDropped(paths);
            };

            // PASS 50: the DesktopChildSiteBridge is the AUTHORITATIVE native OLE
            // drop target — it is the HWND that renders the pill and that
            // WindowFromPoint resolves to at the pill (the layered main HWND is
            // transparent to the native hit-test). Production registers the bridge
            // FIRST (primary) and keeps the main HWND registration as a secondary
            // fallback for any drag that resolves to the top-level window.
            // HALO_OLE_MAIN_ONLY=1 / HALO_OLE_BRIDGE_ONLY=1 still isolate one layer
            // (diagnostic only, OFF by default).
            bool mainOnly = Helpers.MotionDiagnostics.OleMainOnly && !Helpers.MotionDiagnostics.OleBridgeOnly;
            bool bridgeOnly = Helpers.MotionDiagnostics.OleBridgeOnly && !Helpers.MotionDiagnostics.OleMainOnly;
            int registered = 0;
            if (!bridgeOnly)
            {
                foreach (var bridge in EnumerateBridgeChildren())
                {
                    target.Register(bridge, _hwnd);
                    registered++;
                }
            }
            if (!mainOnly)
            {
                target.Register(_hwnd, _hwnd);
                registered++;
            }

            // PASS 53: the invisible drop-target overlay. Created once and
            // registered alongside the bridge so ANY drag that resolves to it
            // (WindowFromPoint at the pill, where the alpha-0 layered main is
            // skipped by the native hit-test) reaches the SAME OLE target and
            // keeps the File Shelf open. Skipped in HALO_OLE_BRIDGE_ONLY
            // diagnostic mode so the A/B stays a pure bridge test.
            if (!bridgeOnly)
            {
                _dropOverlay = PillDropOverlay.Create(_hwnd);
                if (_dropOverlay != null)
                {
                    target.Register(_dropOverlay.Hwnd, _hwnd);
                    registered++;
                    UpdateDropOverlayFromWindow();
                }
            }

            _oleDropTarget = target;
            Logger.Info($"[DRAG] OLE drop target armed on {registered} HWND(s) " +
                        $"mode={(mainOnly ? "MAIN_ONLY" : bridgeOnly ? "BRIDGE_ONLY" : "BOTH")}.");
            ArmDragRouteTimer();
            RunDragAutoProbe();
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG] OLE drop target arm failed", ex);
        }
    }

    /// <summary>
    /// PASS 53: called on every settled layout (from MainWindow.ApplyStableRegion)
    /// with the LIVE SetWindowRgn rect + settle state. The overlay mirrors that
    /// exact geometry so it is a drop target exactly where the pill is visible.
    /// During an active drop session the shelf's drag-grown region stays a
    /// target (the OLE session gates acceptance), so the drop continues working
    /// while the pill animates collapsed→expanded.
    /// </summary>
    public void UpdateDropOverlay(int regX, int regY, int regW, int regH, bool collapsed)
    {
        try
        {
            if (_dropOverlay == null) return;
            bool show = (collapsed || _oleDropTarget?.IsDragActive == true) && !_isHiddenForFullscreen;
            _dropOverlay.Update(regX, regY, regW, regH, show);
        }
        catch (Exception ex)
        {
            Logger.Error("[DROP-OVERLAY] update failed", ex);
        }
    }

    private void UpdateDropOverlayFromWindow()
    {
        var (rx, ry, rw, rh, collapsed) = App.Window.GetRegionState();
        UpdateDropOverlay(rx, ry, rw, rh, collapsed);
    }

    /// <summary>
    /// PASS 47B (STEP 6): temporary diagnostic — clears ONE extended style on
    /// the main Halo HWND (WS_EX_LAYERED and/or WS_EX_NOACTIVATE) when the
    /// matching env flag is set, to prove whether that style blocks OLE routing.
    /// OFF by default; no-op in production. Restart without the env flag to
    /// restore the normal styles (nothing is persisted).
    /// </summary>
    private void ApplyOleStyleDiagnostics()
    {
        if (!Helpers.MotionDiagnostics.OleClearLayered && !Helpers.MotionDiagnostics.OleClearNoActivate)
            return;
        int clear = 0;
        if (Helpers.MotionDiagnostics.OleClearLayered) clear |= WS_EX_LAYERED;
        if (Helpers.MotionDiagnostics.OleClearNoActivate) clear |= WS_EX_NOACTIVATE;
        int ex = (int)GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex & ~clear));
        Logger.Info($"[DRAG] diagnostic style clear applied to main HWND " +
                    $"(ex=0x{ex & ~clear:X8} cleared=0x{clear:X8}) — TEMPORARY, restart to restore.");
    }

    /// <summary>
    /// PASS 47B (STEP 1): continuous hit-test logger, armed only when
    /// HALO_DRAG_ROUTE=1. A 50 ms DispatcherQueue timer logs [DRAG-ROUTE] only
    /// while the cursor is inside the Halo envelope — the actual HWND Windows
    /// reports under the cursor during a real Explorer drag, plus class/root/
    /// child identity and styles. Stops logging the moment the cursor leaves
    /// the envelope. Proves whether Windows routes the drag over the main Halo
    /// HWND, the bridge, or an unrelated intercepting HWND.
    /// </summary>
    private void ArmDragRouteTimer()
    {
        if (!Helpers.MotionDiagnostics.EnableDragRoute) return;
        var dq = _dispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _dragRouteTimer?.Stop();
        _dragRouteTimer = dq.CreateTimer();
        _dragRouteTimer.Interval = TimeSpan.FromMilliseconds(50);
        _dragRouteTimer.IsRepeating = true;
        _dragRouteTimer.Tick += (_, _) => DragRouteTick();
        _dragRouteTimer.Start();
        Logger.Info("[DRAG-ROUTE] hit-test logger armed (50 ms; logs only while cursor is over the Halo envelope).");
    }

    private void DragRouteTick()
    {
        try
        {
            if (!GetCursorPos(out POINT pt)) return;
            if (!GetWindowRect(_hwnd, out RECT halo)) return;
            bool insideHaloRect = pt.X >= halo.Left && pt.X <= halo.Right
                && pt.Y >= halo.Top && pt.Y <= halo.Bottom;
            if (!insideHaloRect) return; // stop logging when cursor leaves Halo bounds

            IntPtr wfp = WindowFromPoint(pt);
            IntPtr root = GetAncestor(wfp, GA_ROOT);
            IntPtr child = ChildWindowFromPoint(_hwnd, new POINT { X = pt.X - halo.Left, Y = pt.Y - halo.Top });

            var (pillX, pillY, pillW, pillH) = App.Window.GetPillScreenRect() ?? (0, 0, 0, 0);
            bool insidePill = pillW > 0
                && pt.X >= pillX && pt.X <= pillX + pillW
                && pt.Y >= pillY && pt.Y <= pillY + pillH;

            Logger.Info(
                "[DRAG-ROUTE] " +
                $"cursor=({pt.X},{pt.Y}) " +
                $"WindowFromPoint=0x{wfp.ToInt64():X} windowClass=\"{P40Class(wfp)}\" " +
                $"rect={P40RectStr(wfp)} style=0x{GetWindowLong(wfp, GWL_STYLE):X8} " +
                $"exStyle=0x{GetWindowLong(wfp, GWL_EXSTYLE):X8} " +
                $"root=0x{root.ToInt64():X} rootClass=\"{P40Class(root)}\" " +
                $"child=0x{child.ToInt64():X} childClass=\"{P40Class(child)}\" " +
                $"haloMain=0x{_hwnd.ToInt64():X} haloBridge=0x{FindRegisteredBridge().ToInt64():X} " +
                $"insideHaloRect={insideHaloRect} insidePill={insidePill}");

            // PASS 48 (STEP 3): the live native-region dump — whether the ACTUAL
            // SetWindowRgn region covers the VISIBLE XAML pill at this cursor
            // point (GetWindowRgn/GetRgnBox/type, rendered pill rect, z-order vs
            // the taskbar). Additive; armed only by HALO_DRAG_REGION=1.
            if (Helpers.MotionDiagnostics.EnableDragRegion)
                Logger.Info(App.Window.DescribeRegionHitTest(pt.X, pt.Y));
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG-ROUTE] tick failed", ex);
        }
    }

    private IntPtr FindRegisteredBridge()
    {
        if (_oleDropTarget == null) return IntPtr.Zero;
        foreach (var h in _oleDropTarget.RegisteredHwnds)
            if (P40Class(h).Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase))
                return h;
        return IntPtr.Zero;
    }

    /// <summary>
    /// PASS 49: HALO_DRAG_AUTOPROBE=1 — FINAL drag hit-test A/B. One shot,
    /// runs on the UI thread right after the OLE drop target is armed. Waits
    /// ~450 ms (three 150 ms z-order-guard ticks) so the steady-state Z-order
    /// is asserted — a probe captured immediately after Activate() can report
    /// the taskbar above the Halo purely because the guard has not ticked yet.
    /// Then parks the cursor on the compact-pill center and IMMEDIATELY
    /// (synchronously, while still parked) performs the native hit-test at the
    /// parked coordinate and logs ONE authoritative [DRAG-PROBE-RESULT] line.
    /// No reliance on the 50 ms [DRAG-ROUTE] timer. Then restores the original
    /// cursor position. Observation-only: no style, region, geometry, z-order,
    /// XAML, bridge, or OLE changes.
    /// </summary>
    private void RunDragAutoProbe()
    {
        if (!Helpers.MotionDiagnostics.EnableDragAutoProbe) return;
        var rect = App.Window.GetPillScreenRect();
        if (rect is not (int pillX, int pillY, int pillW, int pillH) || pillW <= 0)
        {
            Logger.Info("[DRAG-PROBE] aborted — pill rect not available (not in compact state?).");
            return;
        }
        if (!GetCursorPos(out POINT home))
        {
            Logger.Info("[DRAG-PROBE] aborted — GetCursorPos failed.");
            return;
        }

        int x = pillX + pillW / 2;
        int y = pillY + pillH / 2;
        var dq = _dispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var timer = dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(450);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => RunDragAutoProbeAt(x, y, home);
        timer.Start();
    }

    private void RunDragAutoProbeAt(int x, int y, POINT home)
    {
        SetCursorPos(x, y);
        Logger.Info($"[DRAG-PROBE] parked cursor at pill center ({x},{y}).");
        try
        {
            LogDragProbeResult(x, y);
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG-PROBE] result failed", ex);
        }
        finally
        {
            SetCursorPos(home.X, home.Y);
            Logger.Info($"[DRAG-PROBE] done — cursor restored to ({home.X},{home.Y}).");
        }
    }

    /// <summary>
    /// PASS 49: the single authoritative [DRAG-PROBE-RESULT] line, captured at
    /// the parked pill-center coordinate while the cursor is still there.
    /// mode reflects whether HALO_OLE_CLEAR_LAYERED=1 was applied (the A/B).
    /// </summary>
    private void LogDragProbeResult(int x, int y)
    {
        string mode = Helpers.MotionDiagnostics.OleClearLayered ? "CLEAR_LAYERED" : "NORMAL";
        var pt = new POINT { X = x, Y = y };

        IntPtr wfp = WindowFromPoint(pt);
        IntPtr parent = GetParent(wfp);
        IntPtr root = GetAncestor(wfp, GA_ROOT);
        GetWindowRect(_hwnd, out RECT halo);
        IntPtr child = ChildWindowFromPoint(_hwnd, new POINT { X = x - halo.Left, Y = y - halo.Top });
        int wfpEx = GetWindowLong(wfp, GWL_EXSTYLE);

        // LIVE native region at the parked point (window-relative).
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        int rgnType = GetWindowRgn(_hwnd, rgn);
        bool insideRegion = rgnType > 0 && PtInRegion(rgn, x - halo.Left, y - halo.Top);
        DeleteObject(rgn);

        // Pill containment — by construction the parked point IS the pill center.
        bool insidePill = true;

        // Z-order: is Shell_TrayWnd above the Halo? GW_HWNDPREV walks upward.
        bool haloBelowTaskbar = false;
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            IntPtr cur = _hwnd;
            while ((cur = GetWindow(cur, GW_HWNDPREV)) != IntPtr.Zero)
                if (cur == tray) { haloBelowTaskbar = true; break; }
        }

        Logger.Info(
            "[DRAG-PROBE-RESULT] " +
            $"mode={mode} " +
            $"point=({x},{y}) " +
            $"windowFromPoint=0x{wfp.ToInt64():X} windowClass=\"{P40Class(wfp)}\" " +
            $"windowRect={P40RectStr(wfp)} " +
            $"parent=0x{parent.ToInt64():X} " +
            $"root=0x{root.ToInt64():X} rootClass=\"{P40Class(root)}\" " +
            $"child=0x{child.ToInt64():X} childClass=\"{P40Class(child)}\" " +
            $"haloMain=0x{_hwnd.ToInt64():X} haloBridge=0x{FindRegisteredBridge().ToInt64():X} " +
            $"insidePill={insidePill} insideRegion={insideRegion} " +
            $"haloBelowTaskbar={haloBelowTaskbar} " +
            $"exStyle=0x{wfpEx:X8} " +
            $"layered={(wfpEx & WS_EX_LAYERED) != 0} " +
            $"noActivate={(wfpEx & WS_EX_NOACTIVATE) != 0} " +
            $"transparent={(wfpEx & WS_EX_TRANSPARENT) != 0}");
    }

    /// <summary>
    /// PASS 44: hides or restores the ENTIRE main Halo HWND. visible=false →
    /// ShowWindow(SW_HIDE) with the 150 ms z-order guard suspended so nothing
    /// re-presents or re-styles the window during the measurement; visible=true
    /// → ShowWindow(SW_SHOWNOACTIVATE) (no focus steal) and the guard resumes.
    /// Nothing is destroyed: no bridge, no DWM attribute, no region, no acrylic,
    /// no geometry change. DwmFlush follows so the desktop composite is up to
    /// date before the caller samples.
    /// </summary>
    public void SetP44HaloVisible(bool visible)
    {
        try
        {
            if (!visible)
            {
                _zOrderTimer?.Stop();
                bool ret = ShowWindow(_hwnd, SW_HIDE);
                DwmFlush();
                Logger.Info($"[P44-SEQ] HIDE mainHwnd=0x{_hwnd.ToInt64():X} ret={ret} visibleNow={IsWindowVisible(_hwnd)} (z-order guard suspended; no geometry/DWM/region/acrylic change)");
            }
            else
            {
                bool ret = ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                DwmFlush();
                _zOrderTimer?.Start();
                Logger.Info($"[P44-SEQ] SHOW mainHwnd=0x{_hwnd.ToInt64():X} ret={ret} visibleNow={IsWindowVisible(_hwnd)} (SW_SHOWNOACTIVATE; z-order guard resumed)");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[P44-SEQ] halo visibility failed", ex);
        }
    }

    /// <summary>
    /// PASS 43: enabled=true disables the DWM non-client frame (NCRENDERING_POLICY
    /// DISABLED, CORNER DONOTROUND, BORDER NONE) — the NO_DWM_FRAME test.
    /// enabled=false restores the production non-client configuration (corner
    /// DONOTROUND, border NONE, NCR rendering disabled — identical to the
    /// PASS 46 defaults ReassertDwmAttributes re-applies; the region owns the
    /// visible rounding, so DONOTROUND is production). Values are read back and
    /// logged so the effect is provable.
    /// </summary>
    public void ApplyP42DwmNoFrame(bool enabled)
    {
        try
        {
            int corner = DWMWCP_DONOTROUND;
            int border = DWMWA_COLOR_NONE;   // production already suppresses the border
            int policy = DWMNCRP_DISABLED;   // production already disables NCR rendering
            int r1 = DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
            int r2 = DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int r3 = DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            DwmGetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out int cornerAfter, sizeof(int));
            Logger.Info($"[P42-DWM] {(enabled ? "ENABLE-NOFRAME" : "RESTORE-PRODUCTION")} set={r1}/{r2}/{r3} cornerAfter=0x{cornerAfter:X}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P42-DWM] apply failed", ex);
        }
    }

    /// <summary>
    /// PASS 43: forces a compositor/window redraw + invalidation WITHOUT
    /// changing window geometry — the binary tests sample the real desktop
    /// pixels only after this has re-presented the surface.
    /// </summary>
    public void ForceP42Redraw(IntPtr hwnd)
    {
        try
        {
            InvalidateRect(hwnd, IntPtr.Zero, false);
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            DwmFlush();
            Logger.Info("[P42-REDRAW] forced invalidate + redraw + DwmFlush (no geometry change)");
        }
        catch (Exception ex)
        {
            Logger.Error("[P42-REDRAW] failed", ex);
        }
    }

    // ── PASS 38 (GOAL 1) rectangle forensics ────────────────────────────────
    // Test D of the shadow-isolation sequence: dump the actual Win32 window
    // styles, ex-styles, layered per-pixel-alpha mode, presenter/border state
    // and the DWM extended-frame-bounds vs window-rect delta (the shadow
    // extent) so the rectangle's source is attributed by runtime evidence on
    // the user's desktop — never by a headless assumption.

    /// <summary>
    /// Logs a one-line [P38] forensics snapshot of the real Win32 window state.
    /// The critical field is the DWM shadow delta: on Vista+ GetWindowRect
    /// INCLUDES the drop shadow, while DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)
    /// returns the true window bounds WITHOUT it — a nonzero delta proves DWM
    /// is still rendering a shadow/frame for the window rect, independent of
    /// SetWindowRgn and the layered style bit. Call once after styling and
    /// again after the first present; HALO_P38_FORENSICS=1 repeats it on a
    /// timer so state can be compared across expand/collapse.
    /// </summary>
    public void LogWindowForensics(string tag)
    {
        try
        {
            int style = GetWindowLong(_hwnd, GWL_STYLE);
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            bool popup = ((uint)style & 0x80000000) != 0;
            bool layered = (ex & WS_EX_LAYERED) != 0;
            bool toolWindow = (ex & WS_EX_TOOLWINDOW) != 0;
            bool noActivate = (ex & WS_EX_NOACTIVATE) != 0;

            bool lwa = GetLayeredWindowAttributes(_hwnd, out int crKey, out byte bAlpha, out uint dwFlags);
            // layered && no LWA_ALPHA/COLORKEY => DirectComposition per-pixel alpha
            // (the only layered mode where the unpainted surface is transparent).
            bool perPixelAlpha = layered && (!lwa || (dwFlags & (LWA_ALPHA | LWA_COLORKEY)) == 0);

            string presenter = _appWindow.Presenter?.GetType().Name ?? "none";
            // OverlappedPresenter exposes no getter for its border/title-bar
            // state — derive it from the window style (WS_CAPTION = 0x00C00000
            // = border + title bar).
            bool hasCaption = ((uint)style & 0x00C00000) != 0;
            string borderBar = $"border={hasCaption}";

            int corner = -1, borderColor = -1;
            bool cornerOk = DwmGetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out corner, sizeof(int)) == 0;
            bool borderOk = DwmGetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, out borderColor, sizeof(int)) == 0;

            bool extOk = DwmGetWindowAttribute(_hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT ext, Marshal.SizeOf<RECT>()) == 0;
            bool winOk = GetWindowRect(_hwnd, out RECT win);
            string shadow = "n/a";
            if (extOk && winOk)
            {
                // GetWindowRect includes the DWM drop shadow on Vista+; the
                // extended-frame-bounds exclude it → delta = live shadow extent.
                int shadowBottom = Math.Max(0, win.Bottom - ext.Bottom);
                int shadowRight = Math.Max(0, win.Right - ext.Right);
                shadow = $"shadowDelta=(right={shadowRight},bottom={shadowBottom}) " +
                         $"extFrame=({ext.Left},{ext.Top},{ext.Right - ext.Left}x{ext.Bottom - ext.Top})";
            }

            Logger.Info($"[P38] forensics[{tag}] style=0x{style:X} ex=0x{ex:X} popup={popup} layered={layered} " +
                        $"perPixelAlpha={perPixelAlpha} lwa={lwa} lwaFlags=0x{dwFlags:X} " +
                        $"toolWindow={toolWindow} noActivate={noActivate} presenter={presenter} {borderBar} " +
                        $"cornerPref=0x{corner:X}(ok={cornerOk}) borderColor=0x{borderColor:X}(ok={borderOk}) " +
                        $"winRect=({win.Left},{win.Top},{win.Right - win.Left}x{win.Bottom - win.Top}) {shadow}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P38] forensics dump failed", ex);
        }
    }

    /// <summary>
    /// PASS 39 (GOAL 1): the [P39-SURFACE] pixel-source dump. Logs every known
    /// mechanism that can own rectangular pixels on the user's desktop — HWND
    /// style bits, ex-style bits, layered per-pixel-alpha mode, DWM frame /
    /// shadow deltas (GetWindowRect vs DWMWA_EXTENDED_FRAME_BOUNDS), corner
    /// preference, the LIVE region box (GetWindowRgn), the presenter frame and,
    /// via <paramref name="context"/> (filled by App/MainWindow), the backdrop
    /// material + root backgrounds. The desktop recording is ground truth; this
    /// dump attributes the rectangle to one of: A HWND non-client rendering,
    /// B DWM shadow, C system backdrop, D WinUI root background, E DirectComposition
    /// surface, F presenter frame, G another Halo HWND, H popup/secondary HWND,
    /// I acrylic/backdrop material.
    /// </summary>
    public void LogP39Surface(string tag, string context = "")
    {
        try
        {
            int style = GetWindowLong(_hwnd, GWL_STYLE);
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            uint ust = (uint)style;
            uint uex = (uint)ex;

            bool lwa = GetLayeredWindowAttributes(_hwnd, out int crKey, out byte bAlpha, out uint dwFlags);
            bool perPixelAlpha = (uex & WS_EX_LAYERED) != 0
                && (!lwa || (dwFlags & (LWA_ALPHA | LWA_COLORKEY)) == 0);

            string presenter = _appWindow.Presenter?.GetType().Name ?? "none";
            bool hasCaption = (ust & WS_CAPTION) != 0;

            int corner = -1, borderColor = -1;
            bool cornerOk = DwmGetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out corner, sizeof(int)) == 0;
            bool borderOk = DwmGetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, out borderColor, sizeof(int)) == 0;

            GetWindowRect(_hwnd, out RECT win);
            GetClientRect(_hwnd, out RECT client);
            bool extOk = DwmGetWindowAttribute(_hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT ext, Marshal.SizeOf<RECT>()) == 0;
            int shadowRight = extOk ? Math.Max(0, win.Right - ext.Right) : -1;
            int shadowBottom = extOk ? Math.Max(0, win.Bottom - ext.Bottom) : -1;

            string region = "n/a";
            IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
            if (rgn != IntPtr.Zero)
            {
                int rgnType = GetWindowRgn(_hwnd, rgn);
                if (GetRgnBox(rgn, out RECT rb))
                    region = $"({rb.Left},{rb.Top},{rb.Right - rb.Left}x{rb.Bottom - rb.Top})";
                region += $" type={RgnTypeName(rgnType)}";
                DeleteObject(rgn);
            }

            Logger.Info(
                $"[P39-SURFACE] tag={tag} {context} " +
                $"hwnd=0x{_hwnd.ToInt64():X} " +
                $"style=0x{style:X} popup={(ust & WS_POPUP) != 0} child={(ust & WS_CHILD) != 0} " +
                $"caption={(ust & WS_CAPTION) != 0} thickframe={(ust & WS_THICKFRAME) != 0} " +
                $"border={(ust & WS_BORDER) != 0} dlgframe={(ust & WS_DLGFRAME) != 0} " +
                $"ex=0x{ex:X} layered={(uex & WS_EX_LAYERED) != 0} noActivate={(uex & WS_EX_NOACTIVATE) != 0} " +
                $"toolWindow={(uex & WS_EX_TOOLWINDOW) != 0} transparent={(uex & WS_EX_TRANSPARENT) != 0} " +
                $"windowRect=({win.Left},{win.Top},{win.Right - win.Left}x{win.Bottom - win.Top}) " +
                $"clientRect=({client.Left},{client.Top},{client.Right - client.Left}x{client.Bottom - client.Top}) " +
                $"extFrame=({ext.Left},{ext.Top},{ext.Right - ext.Left}x{ext.Bottom - ext.Top}) " +
                $"dwmShadow=(right={shadowRight},bottom={shadowBottom}) dwmCorner=0x{corner:X}(ok={cornerOk}) " +
                $"borderColor=0x{borderColor:X}(ok={borderOk}) " +
                $"region={region} layeredMode={LayerModeName(perPixelAlpha, lwa, dwFlags, bAlpha)} " +
                $"presenter={presenter} borderBar={hasCaption}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P39-SURFACE] dump failed", ex);
        }
    }

    private static string RgnTypeName(int type) => type switch
    {
        RGN_NULLREGION => "NULL",
        RGN_SIMPLEREGION => "SIMPLE",
        RGN_COMPLEXREGION => "COMPLEX",
        _ => $"ERROR({type})",
    };

    private static string LayerModeName(bool perPixelAlpha, bool lwa, uint dwFlags, byte bAlpha)
        => perPixelAlpha
            ? "perPixelAlpha"
            : lwa
                ? $"lwa(alpha={bAlpha},flags=0x{dwFlags:X})"
                : "none";

    // ── PASS 40 (GOAL 4): HWND attribution + process window census ────────
    // Ruling out "the user's symptom may not be coming from the MainWindow
    // HWND at all": every top-level HWND this process owns is enumerated with
    // class/title/rect/exstyle/parent/owner, and any sampled screen point is
    // attributed to the exact HWND/class that owns its pixels.

    private static string P40Class(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "(none)";
    }

    private static string P40Title(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "(none)";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string P40RectStr(IntPtr h)
        => GetWindowRect(h, out RECT r)
            ? $"({r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top})"
            : "n/a";

    /// <summary>
    /// PASS 40 (GOAL 4): census of every top-level window owned by this
    /// process. Proves whether ANY second Halo HWND exists (backdrop host,
    /// XAML island helper, stale popup, tooltip, duplicate MainWindow) that
    /// could paint the black rectangle over the desktop.
    /// </summary>
    public void LogP40WindowCensus()
    {
        try
        {
            uint pid = (uint)Environment.ProcessId;
            Logger.Info($"[P40-HWNDS] census process=Halo pid={pid} haloHwnd=0x{_hwnd.ToInt64():X}");
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid != pid) return true;
                int ex = (int)GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                int style = (int)GetWindowLongPtr(h, GWL_STYLE).ToInt64();
                IntPtr owner = GetWindowLongPtr(h, GWL_HWNDPARENT);
                Logger.Info($"[P40-HWNDS] hwnd=0x{h.ToInt64():X} class={P40Class(h)} title=\"{P40Title(h)}\" " +
                            $"rect={P40RectStr(h)} style=0x{style:X} ex=0x{ex:X} " +
                            $"owner=0x{owner.ToInt64():X} parent=0x{GetParent(h).ToInt64():X} root=0x{GetAncestor(h, GA_ROOT).ToInt64():X} " +
                            $"rootOwner=0x{GetAncestor(h, GA_ROOTOWNER).ToInt64():X} visible={IsWindowVisible(h)}");
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Error("[P40-HWNDS] census failed", ex);
        }
    }

    /// <summary>
    /// PASS 41 (GOAL 3): census of every top-level AND child window owned by
    /// this process, each logged with its rect and whether it overlaps the
    /// suspicious rectangle (the detected anomalous-dark bbox). Pays particular
    /// attention to any WinUI/compositor helper HWND whose rectangle overlaps
    /// the Halo envelope — such a sibling (backdrop host, XAML island bridge,
    /// input site) can be the black rectangle's actual painter.
    /// </summary>
    public void LogP41WindowCensus(int suspX0, int suspY0, int suspX1, int suspY1)
    {
        try
        {
            uint pid = (uint)Environment.ProcessId;
            Logger.Info($"[P41-HWNDS] census process=Halo pid={pid} haloHwnd=0x{_hwnd.ToInt64():X} " +
                        $"suspiciousRect=({suspX0},{suspY0},{suspX1 - suspX0}x{suspY1 - suspY0})");

            void DumpOne(IntPtr h, string indent)
            {
                GetWindowThreadProcessId(h, out uint wpid);
                int ex = (int)GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                int style = (int)GetWindowLongPtr(h, GWL_STYLE).ToInt64();
                IntPtr owner = GetWindowLongPtr(h, GWL_HWNDPARENT);
                bool overlaps = false;
                if (GetWindowRect(h, out RECT r))
                    overlaps = r.Left < suspX1 && r.Right > suspX0 && r.Top < suspY1 && r.Bottom > suspY0;
                Logger.Info($"[P41-HWNDS] {indent}hwnd=0x{h.ToInt64():X} class={P40Class(h)} title=\"{P40Title(h)}\" " +
                            $"rect={P40RectStr(h)} style=0x{style:X} ex=0x{ex:X} " +
                            $"owner=0x{owner.ToInt64():X} parent=0x{GetParent(h).ToInt64():X} " +
                            $"root=0x{GetAncestor(h, GA_ROOT).ToInt64():X} rootOwner=0x{GetAncestor(h, GA_ROOTOWNER).ToInt64():X} " +
                            $"pid={wpid} visible={IsWindowVisible(h)} overlapsSuspicious={overlaps}");
            }

            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid != pid) return true;
                DumpOne(h, "TOP");
                EnumChildWindows(h, (c, cl) => { DumpOne(c, " CHILD"); return true; }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Error("[P41-HWNDS] census failed", ex);
        }
    }

    /// <summary>
    /// PASS 40 (GOAL 4): full HWND attribution for one sampled screen point —
    /// the [P40-HIT] log. Answers "which HWND owns the black pixels the user
    /// sees" at that coordinate (windowFromPoint / root / rootOwner / parent /
    /// class / title / rect / exstyle / pid).
    /// </summary>
    public void LogP40Hit(int x, int y, string tag)
    {
        try
        {
            IntPtr wfp = WindowFromPoint(new POINT { X = x, Y = y });
            uint wpid = 0;
            if (wfp != IntPtr.Zero) GetWindowThreadProcessId(wfp, out wpid);
            int ex = wfp != IntPtr.Zero ? (int)GetWindowLongPtr(wfp, GWL_EXSTYLE).ToInt64() : -1;
            IntPtr owner = wfp != IntPtr.Zero ? GetWindowLongPtr(wfp, GWL_HWNDPARENT) : IntPtr.Zero;
            bool halo = wfp == _hwnd;
            Logger.Info($"[P40-HIT] tag={tag} coord=({x},{y}) hwnd=0x{wfp.ToInt64():X} " +
                        $"class={P40Class(wfp)} title=\"{P40Title(wfp)}\" rect={P40RectStr(wfp)} " +
                        $"ex=0x{ex:X} owner=0x{owner.ToInt64():X} parent=0x{GetParent(wfp).ToInt64():X} " +
                        $"root=0x{GetAncestor(wfp, GA_ROOT).ToInt64():X} rootOwner=0x{GetAncestor(wfp, GA_ROOTOWNER).ToInt64():X} " +
                        $"pid={wpid} isHalo={halo}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P40-HIT] attribution failed", ex);
        }
    }

    /// <summary>
    /// Applies WS_EX_TOOLWINDOW (hide from Alt+Tab and taskbar button strip)
    /// and WS_EX_NOACTIVATE (never steal keyboard focus from the active app).
    /// </summary>
    private void ApplyToolWindowStyle()
    {
        // Strip WS_OVERLAPPEDWINDOW and add WS_POPUP to completely disable any OS window frames/outlines
        int style = GetWindowLong(_hwnd, GWL_STYLE);
        int newStyle = (style & ~0x00CF0000) | unchecked((int)0x80000000); // 0x80000000 is WS_POPUP
        SetWindowLong(_hwnd, GWL_STYLE, newStyle);

        int current = GetWindowLong(_hwnd, GWL_EXSTYLE);
        int updated = current | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        // PASS 31 (GOAL 1): WS_EX_LAYERED is now ALWAYS applied. Two effects:
        //  1. Per-pixel alpha — DWM composites the DirectComposition surface
        //     with the XAML alpha channel, so the unpainted window surface is
        //     genuinely transparent and the desktop shows through everywhere
        //     outside the visible Halo shape (the fixed 800×712 envelope no
        //     longer clears to the opaque theme color — the persistent black
        //     surface behind the pill/dashboard, Pass 29-30). This is the only
        //     mechanism that satisfies "desktop visible outside the visible
        //     Halo shape" with the stable-HWND architecture.
        //  2. Layered windows cast no DWM drop shadow — the faint envelope
        //     rectangle (Pass 26/30 GOAL 2) is removed by the same flag.
        // Pass 30 shipped this gated behind HALO_P30_NOSHADOW=1 for a desktop
        // A/B; Pass 31 promotes it to default. HALO_NO_LAYERED=1 restores the
        // old opaque-surface behavior as an escape hatch.
        // PASS 39 (GOAL 1): MODE B (HALO_P39_NO_LAYERED=1) and MODE C
        // (HALO_P39_RAW_WINDOW=1) skip the layered style so the black rectangle
        // can be attributed: if it is a DWM drop shadow / frame for the fixed
        // envelope it disappears only when WS_EX_LAYERED is off (shadow on).
        bool p39NoLayered = Helpers.MotionDiagnostics.EnableP39NoLayered
            || Helpers.MotionDiagnostics.EnableP39RawWindow;
        if (Environment.GetEnvironmentVariable("HALO_NO_LAYERED") != "1" && !p39NoLayered)
        {
            updated |= WS_EX_LAYERED;
            Logger.Info("[WINDOW] WS_EX_LAYERED applied (per-pixel alpha: transparent window surface; no DWM shadow).");
        }
        else if (p39NoLayered)
        {
            Logger.Info("[WINDOW] WS_EX_LAYERED SKIPPED (PASS 39 MODE B/C — layered disabled for pixel-source isolation).");
        }
        SetWindowLong(_hwnd, GWL_EXSTYLE, updated);
        Logger.Info($"ApplyToolWindowStyle: style 0x{style:X} → 0x{newStyle:X}, exStyle 0x{current:X} → 0x{updated:X}");

        // PASS 38 (GOAL 1): the SetWindowLong style mutations above only fully
        // reach DWM after a SWP_FRAMECHANGED re-frame — the documented
        // requirement for changing window data via SetWindowLong ("you must
        // call SetWindowPos for the changes to take effect"). Without it DWM
        // keeps its cached non-client frame/shadow for the fixed 800×712
        // envelope rect, which is the machine-specific dark rectangle around
        // the Halo: the style bits (WS_POPUP / WS_EX_LAYERED) read correctly
        // from GetWindowLong, so every previous headless probe looked clean,
        // while the real compositor still draws the frame/shadow for the
        // window rect. The re-frame makes DWM recompute the NC area — no
        // caption, no shadow — before the first present.
        ReFrameWindow();
        LogWindowForensics("post-styles");
    }

    /// <summary>
    /// PASS 38 (GOAL 1): tells DWM to re-evaluate the non-client frame after
    /// any SetWindowLong/SetWindowLongPtr style or ownership mutation. Per the
    /// SetWindowPos docs, style changes applied with SetWindowLong do not take
    /// effect for the frame/shadow until the window is re-framed with
    /// SWP_FRAMECHANGED (which forces a WM_NCCALCSIZE regardless of size).
    /// Safe no-op when nothing needs recomputing — the flags are the canonical
    /// NOMOVE|NOSIZE|NOZORDER|FRAMECHANGED|NOACTIVATE combination.
    /// </summary>
    private void ReFrameWindow()
    {
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
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
        // Pass 26: style changes can reset the DWM non-client attributes too —
        // re-assert so no shadow/border flash appears while typing.
        ReassertDwmAttributes();
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
        // Pass 15 diagnostic: HALO_P15_NOZGUARD=1 skips the repeating timer so
        // the idle render stream can be attributed to the guard vs. other
        // sustainers. Default behavior unchanged.
        if (MotionDiagnostics.P15NoZGuard)
        {
            Logger.Info("[P15] z-order guard disabled (HALO_P15_NOZGUARD=1, diagnostic).");
            return;
        }
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

        // PASS 53: keep the drop-target overlay directly BELOW the main HWND in
        // the topmost band (and hidden during fullscreen, matching the main).
        _dropOverlay?.ReassertZOrder(_hwnd, _isHiddenForFullscreen);

        // Pass 26/27: SetWindowPos z-order re-asserts (and Show/Hide/Activate)
        // can reset the DWM non-client attributes. Pass 27 proved the visible
        // full-envelope rectangle was the DWM system backdrop (ignores
        // SetWindowRgn — removed in MainWindow), not the DWM shadow; these
        // re-asserts remain as cheap defensive insurance for the border/corners.
        ReassertDwmAttributes();

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
                    ReassertDwmAttributes(); // Show() can also reset the DWM frame attributes
                    UpdateDropOverlayFromWindow(); // PASS 53: unhide the overlay
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
                    bool outsideWindowRect = !GetWindowRect(_hwnd, out RECT rect)
                        || data.pt.X < rect.Left || data.pt.X > rect.Right
                        || data.pt.Y < rect.Top || data.pt.Y > rect.Bottom;

                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        // PASS 47 (GOAL 1): a press is "outside" when it lands
                        // outside the CURRENT visible region — the actual
                        // interactive shape — never the fixed 1000x890 HWND
                        // envelope. A press on the taskbar strip below the
                        // expanded dashboard, or in the transparent envelope
                        // margins, is inside the envelope but OUTSIDE the
                        // region: it must collapse the island. The region is
                        // UI-thread state, so the classification runs inside
                        // this enqueued callback.
                        if (!outsideWindowRect && App.Window.IsPointInCurrentRegion(data.pt.X, data.pt.Y))
                            return;

                        // Pass 13 diagnostics (env-gated): confirm the outside-click reached the hook.
                        if (Helpers.MotionDiagnostics.EnableP13)
                            Logger.Info($"[P13DBG] hook outside-click at ({data.pt.X},{data.pt.Y}) winRect=({rect.Left},{rect.Top},{rect.Right - rect.Left}x{rect.Bottom - rect.Top})");

                        // Stable-window production: the per-frame SetWindowRgn
                        // updates can leave WinUI's PointerExited undelivered, so
                        // _mouseIsOver can be stuck true and block the outside-click
                        // collapse. The hook has PROVEN the pointer is outside the
                        // visible region — clear the stale hover state first.
                        App.IslandController.NotifyMouseLeave();
                        MouseClickedOutside?.Invoke(this, EventArgs.Empty);
                    });
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
        try
        {
            _oleDropTarget?.RevokeAll();
        }
        catch
        {
            // Best-effort teardown — never throw from a finalizer.
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

    // ── Profile counters (temporary — remove after profiling).
    private int _profileFrameCount;
    private int _profileAppliedCount;
    private long _profileStartMs;
    private long _profileFirstFrameMs;

    // ── Pass 8 [MOTION-P8] forensics state (temporary instrumentation).
    // Direction of the current segment, recorded for the [WINDOW] start/complete
    // evidence logs (the stable window never changes native geometry, so the
    // segment direction is the only per-segment geometry state left).
    private string _motionLastDir = "";
    private string _animDir = "";

    // ── Pass 9 shared-progress choreography state (temporary instrumentation).
    // Initial normalized velocity of the current tween segment: 1.8 by default
    // for fresh segments (Pass 20 jitter fix — gentler start), or the
    // interrupted segment's velocity for retargets (velocity-aware reversal).
    // Always overwritten by StartSizeAnimation before OnRendering reads it.
    private double _animV0 = 1.8;
    private double _motionInterruptProgress;
    private double _motionInterruptVelocity;

    // ── Pass 10 frame-pacing forensics (temporary instrumentation).
    private readonly System.Diagnostics.Stopwatch _geomClock = new();

    // ── Pass 9 shared motion events ────────────────────────────────────────

    /// <summary>
    /// Describes one window-geometry animation segment (a retarget starts a new
    /// segment). <see cref="IsDashboardTransition"/> distinguishes real
    /// compact↔expanded profile transitions from compact-width tweaks and the
    /// legacy clipboard preview, which must not trigger content choreography.
    /// </summary>
    public sealed record MotionSegment(bool Expanding, double FromWidth, double FromHeight, double TargetWidth, double TargetHeight, bool IsDashboardTransition);

    /// <summary>Fired on the UI thread when a size-animation segment starts.</summary>
    public event Action<MotionSegment>? MotionSegmentStarted;

    /// <summary>Fired on the UI thread every animation frame with eased progress (0→1).</summary>
    public event Action<double>? MotionProgressChanged;

    // ── Pass 35 native-ease curve ──────────────────────────────────────────
    // A simple, controlled Windows-style ease-out: eased = 1 - (1-t)³
    // (easeOutCubic). Pass 33's cubic-bezier(0.16, 1.0, 0.3, 1.0) opened with
    // an initial velocity of ~6.25 (BezY1/BezX1), which read on the desktop as
    // aggressive / mechanical. easeOutCubic halves that initial burst to a
    // natural 3.0 while keeping the native-flyout feel — quick response,
    // smooth acceleration, fast reveal, gentle settle, zero overshoot and no
    // bounce. Fresh segments run this curve; retarget segments run the
    // generalized cubic seeded with the interrupted velocity (velocity-aware
    // reversal). The generalized cubic with v0 = 3.0 IS exactly easeOutCubic,
    // so interrupted chains converge on the same curve family.

    // True while the current segment runs the fresh ease-out (fresh segments).
    // Retargets (wasAnimating) run the generalized cubic seeded with the
    // interrupted velocity; _animIsFresh remembers which curve the CURRENT
    // segment uses so the next interrupt computes the exact derivative.
    private bool _animIsFresh = true;

    /// <summary>Eased progress (0→1) for wall-clock progress t∈[0,1] — easeOutCubic.</summary>
    private static double BezierEase(double t)
    {
        double u = Math.Clamp(t, 0.0, 1.0);
        double s = 1.0 - u;
        return 1.0 - s * s * s;
    }

    /// <summary>
    /// Normalized velocity d(eased)/dt of the ease-out at progress t — the
    /// interrupted-segment velocity used to seed a retarget (Pass 9).
    /// </summary>
    private static double BezierSlope(double t)
    {
        double u = Math.Clamp(t, 0.0, 1.0);
        return 3.0 * (1.0 - u) * (1.0 - u);
    }

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
    public void StartSizeAnimation(int targetWidth, int targetHeight, bool dashboardTransition = false)
    {
        // Was an animation already flagged before the stale-snap guard below ran?
        // Used for [MOTION-P8] segment classification and the Pass 9
        // velocity-aware retarget.
        bool wasAnimating = _isAnimating;

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

        // Pass 9 velocity-aware retarget: a new segment starts with the
        // interrupted segment's normalized velocity (3(1-t)^2 for the cubic
        // ease-out), scaled by the duration ratio, instead of restarting at
        // peak velocity (3.0) — that was the reversal "whip". Fresh segments use
        // v0 = 1.8 by default (Pass 20: video-confirmed jitter fix — the 3.0
        // front-loading moved the edges 100-280 px per frame at the ~60 Hz
        // effective cadence; 1.8 cuts early steps ~40% with identical duration
        // and monotonicity). HALO_V0 overrides for A/B. Clamped to [0,3] so the
        // curve stays monotonic (no overshoot).
        double v0 = MotionDiagnostics.FreshV0;
        if (wasAnimating)
        {
            long interruptedAtMs = Environment.TickCount64 - _animStartMs; // old segment elapsed
            double oldDuration = _animTargetHeight >= _animFromHeight ? ExpandAnimMs : CollapseAnimMs;
            double tOld = Math.Clamp(interruptedAtMs / oldDuration, 0.0, 1.0);
            // Exact interrupt velocity of the ACTUAL interrupted segment, which
            // may itself have been a retarget with _animV0 < 3 (rapid chains) —
            // not the fresh ease-out derivative 3(1-t)^2. _animIsFresh selects
            // the interrupted curve's derivative: the fresh ease-out (fresh
            // segments) or the generalized cubic (retarget chains). _animV0 still
            // holds the old segment's value here; it is only overwritten below.
            double oldVel = _animIsFresh
                ? BezierSlope(tOld)
                : 3.0 * (_animV0 - 2) * tOld * tOld
                  + 2.0 * (3.0 - 2.0 * _animV0) * tOld
                  + _animV0;
            double newDuration = targetHeight >= _currentHeightDip ? ExpandAnimMs : CollapseAnimMs;
            v0 = Math.Clamp(oldVel * newDuration / oldDuration, 0.0, 3.0);
            _motionInterruptProgress = tOld;
            _motionInterruptVelocity = oldVel;
        }
        else
        {
            _motionInterruptProgress = 0;
            _motionInterruptVelocity = 0;
        }
        _animV0 = v0;
        // PASS 35: a retarget (wasAnimating) continues on the generalized cubic
        // seeded with the interrupted velocity; a fresh segment runs the
        // natural easeOutCubic.
        _animIsFresh = !wasAnimating;

        // Retarget from the CURRENT geometry: an interrupt mid-animation simply
        // re-bases the tween (no stacked/overlapping loops — the Rendering
        // subscription is created once and never duplicated).
        _animFromWidth = _currentWidthDip;
        _animFromHeight = _currentHeightDip;
        _animTargetWidth = targetWidth;
        _animTargetHeight = targetHeight;
        _animStartMs = Environment.TickCount64;

        // Pass 8 motion forensics: classify this segment (new / restarted / reversed)
        // and reset the easing-bucket sampler for it.
        bool expanding = _animTargetHeight >= _animFromHeight;
        string dir = expanding ? "expand" : "collapse";
        string oldDir = _motionLastDir;
        string segment = !wasAnimating ? "GeometryAnimationStarted"
            : (!string.IsNullOrEmpty(oldDir) && oldDir != dir
                ? "AnimationReversed"
                : "AnimationRestarted");
        Logger.Info($"[MOTION-P8] {segment} dir={dir} from={(int)Math.Round(_animFromWidth)}x{(int)Math.Round(_animFromHeight)} to={targetWidth}x{targetHeight}");
        _motionLastDir = dir;

        // Stable-window production: record the segment direction and prove the
        // HWND rect stays constant across every animation (real native writes
        // are counted via _profileAppliedCount — always 0 in the stable path).
        _animDir = dir;
        if (GetWindowRect(_hwnd, out RECT rc))
            Logger.Info($"[WINDOW] AnimationStarted dir={dir} hwnd=({rc.Left},{rc.Top},{rc.Right - rc.Left}x{rc.Bottom - rc.Top}) tMs={Environment.TickCount64}");

        // Pass 10 frame-pacing forensics: sample only real dashboard transitions
        // (same gate the VM's choreography uses) — compact width tweaks and the
        // startup 48→48 settle are skipped so the FRAME_STATS stream stays clean.
        // Any still-pending segment is flushed first.
        if (dashboardTransition && Math.Abs(targetHeight - _currentHeightDip) > 0.5)
            MotionDiagnostics.BeginSegment(dir);
        else
            MotionDiagnostics.EndSegment("skipped");

        // Pass 9 forensics: segment broadcast + reversal metrics. The content
        // choreography (MainWindowViewModel) consumes the same shared progress.
        Logger.Info($"[MOTION-P9] MotionStarted dir={dir} dashboardTransition={dashboardTransition} from={(int)Math.Round(_animFromWidth)}x{(int)Math.Round(_animFromHeight)} to={targetWidth}x{targetHeight} v0={_animV0:F2} activeAnim={(wasAnimating ? 1 : 0)} subs=1");
        if (wasAnimating && !string.IsNullOrEmpty(oldDir) && oldDir != dir)
        {
            Logger.Info($"[MOTION-P9] AnimationReversed fromDir={oldDir} toDir={dir} currentProgress={_motionInterruptProgress:F3} currentVel={_motionInterruptVelocity:F2} newInitVel={_animV0:F2} velocityDelta={_animV0 - _motionInterruptVelocity:F2}");
        }
        MotionSegmentStarted?.Invoke(new MotionSegment(expanding, _animFromWidth, _animFromHeight, _animTargetWidth, _animTargetHeight, dashboardTransition));

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
            // Pass 11.5: keep the cadence probe AFTER the animation handler in the
            // invocation list so each probe sample carries THIS frame's animation
            // state (Anim/W/H/resize), not the stale frame N-1 values.
            MotionDiagnostics.OnAnimationSubscribed();
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

    /// <summary>The measured taskbar strip height in DIPs — the stable window's
    /// pill strip, consumed by MainWindow's visual stage.</summary>
    public int TaskbarHeightDips => _taskbarHeightDips;

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
        // Profile transitions are dashboard transitions: the content choreography
        // (pill ↔ dashboard crossfade) runs only for these, not for compact-width
        // tweaks or the legacy clipboard preview.
        StartSizeAnimation(width, height, dashboardTransition: true);
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

        // Deterministic fixed-duration eased tween, evaluated against
        // wall-clock elapsed time since StartSizeAnimation (or the last retarget).
        // The animation therefore always ends at the target (260 ms expand /
        // 220 ms collapse — Pass 34) — it cannot linger for seconds due to
        // spring convergence.
        double elapsed = now - _animStartMs;
        double duration = _animTargetHeight >= _animFromHeight ? ExpandAnimMs : CollapseAnimMs;
        double t = Math.Clamp(elapsed / duration, 0.0, 1.0);
        // PASS 35 (fresh segments): the simple Windows-style easeOutCubic
        // 1-(1-t)³ — quick response, smooth acceleration, fast reveal, gentle
        // settle, no overshoot (replaces Pass 33's aggressive cubic-bezier
        // (0.16, 1.0, 0.3, 1.0)). Retarget segments keep the generalized cubic
        // and start at the interrupted velocity instead of a fresh spike
        // (Pass 9 velocity-aware reversal).
        double eased = _animIsFresh
            ? BezierEase(t)
            : (_animV0 - 2) * t * t * t + (3 - 2 * _animV0) * t * t + _animV0 * t;

        _currentWidthDip = _animFromWidth + (_animTargetWidth - _animFromWidth) * eased;
        _currentHeightDip = _animFromHeight + (_animTargetHeight - _animFromHeight) * eased;

        int rawW = (int)Math.Round(_currentWidthDip * _cachedScale);
        int rawH = (int)Math.Round(_currentHeightDip * _cachedScale);
        int finalW = (int)Math.Round(_animTargetWidth * _cachedScale);
        int finalH = (int)Math.Round(_animTargetHeight * _cachedScale);

        // Pass 11.5: publish per-frame state for the cadence probe (animation
        // on, current logical window size).
        MotionDiagnostics.AnimatingFlag = true;
        MotionDiagnostics.WinWFlag = rawW;
        MotionDiagnostics.WinHFlag = rawH;

        // Settle when the tween is complete OR the rounded rect has visually
        // reached the target. Ease-out flattens the tail, so waiting for
        // sub-pixel convergence is what let the old spring stall for seconds.
        // Settling early also guarantees the settle frame itself runs, keeping
        // the compositor producing frames until we unsubscribe.
        if (t >= 1.0 || (rawW == finalW && rawH == finalH))
        {
            // Pass 13: classify the settled state for the REGION log (uses the
            // actual OS rect via GetWindowRect, not the requested geometry).
            // Label by FINAL height vs taskbar height — a compact-width
            // adaptation settles at taskbar height and must read "collapsed",
            // not "expanded" (from==target height would fool a >= comparison).
            bool settledExpanding = _animTargetHeight > _taskbarHeightDips + 1;

            _currentWidthDip = _animTargetWidth;
            _currentHeightDip = _animTargetHeight;
            var finalPos = GetAnchoredPosition(finalW, finalH);

            ApplyGeometry(finalPos.X, finalPos.Y, finalW, finalH, "Settle");
            _isAnimating = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            Logger.Info($"[PROFILE] Animation settled: frames={_profileFrameCount} applied={_profileAppliedCount} duration={now - _profileStartMs}ms");
            Logger.Info($"[MOTION-P8] GeometryAnimationCompleted durationMs={now - _profileStartMs} frames={_profileFrameCount} applied={_profileAppliedCount}");
            // Force the content choreography to its terminal state — the eased
            // value can settle below 1.0 on the early rounded-rect settle.
            MotionProgressChanged?.Invoke(1.0);
            int targetPxW = (int)Math.Round(_animTargetWidth * _cachedScale);
            int targetPxH = (int)Math.Round(_animTargetHeight * _cachedScale);
            Logger.Info($"[MOTION-P9] MotionCompleted finalPx={finalW}x{finalH} targetPx={targetPxW}x{targetPxH} durationMs={now - _profileStartMs}");
            // Pass 13: report the settled rectangle and its taskbar overlap
            // (compact vs expanded overlap are reported separately by state).
            if (MotionDiagnostics.EnableP13)
                LogP13Region(settledExpanding ? "expanded" : "collapsed");
            // Stable-window production evidence: the HWND rect is unchanged and
            // no native geometry write ran during the segment
            // (_profileAppliedCount counts only real MoveAndResize calls — 0 in
            // the stable path).
            string hwndStr = GetWindowRect(_hwnd, out RECT rc)
                ? $"({rc.Left},{rc.Top},{rc.Right - rc.Left}x{rc.Bottom - rc.Top})"
                : "unknown";
            Logger.Info($"[WINDOW] AnimationCompleted dir={_animDir} nativeWrites={_profileAppliedCount} " +
                        $"hwnd={hwndStr} durationMs={now - _profileStartMs} tMs={Environment.TickCount64}");
            // Pass 11.5: animation ended — expose the static state to the probe.
            MotionDiagnostics.AnimatingFlag = false;
            MotionDiagnostics.WinWFlag = finalW;
            MotionDiagnostics.WinHFlag = finalH;
            MotionDiagnostics.EndSegment("settled");
            return;
        }

        // Pass 9: broadcast the shared eased progress to the content choreography.
        MotionProgressChanged?.Invoke(eased);

        if (_profileFrameCount % 10 == 0)
            Logger.Info($"[PROFILE] Rendering frame {_profileFrameCount}: ms={now} dt={dt * 1000:F1}ms");

        // Stable-window production: the HWND never changes — record the fixed
        // rect into the P10 ring and no-op the geometry write (the
        // clip/translate/region choreography in MainWindow drives the visual,
        // phase-locked to this shared progress).
        MotionDiagnostics.RecordFrame(now, _animStartMs, t, eased,
            _lastApplied.X, _lastApplied.Y, _lastApplied.Width, _lastApplied.Height,
            _lastApplied.Width, _lastApplied.Height);
        ApplyGeometry(_lastApplied.X, _lastApplied.Y, _lastApplied.Width, _lastApplied.Height, "Frame");
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
        // Pass 13 diagnostic: the ONLY changed quantity — displace the final Y by
        // the configured offset (physical px). No-op with the default offset.
        y += _p13OffsetPx;

        // Pass 10: time the geometry application path so the ring stats carry
        // the (no-op) sync cost of this write.
        bool sampling = MotionDiagnostics.IsSampling;
        if (sampling) _geomClock.Restart();

        // Stable-window production: the HWND is pre-sized once at init and must
        // NEVER change — every geometry write is a no-op (the visual stage is
        // driven by the clip/translate/region choreography in MainWindow).
        // While animating, InvalidateArrange keeps the composition loop alive
        // until the tween settles. _profileAppliedCount (the real native-write
        // counter) is therefore never incremented here — the settle log reports
        // nativeWrites=0 as the stable-path assertion.
        if (_isAnimating)
        {
            try
            {
                _window.Content.InvalidateArrange();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Window closed during teardown (the last Rendering tick raced
                // the close) — stop the render loop so it cannot throw again.
                StopAnimations();
            }
        }
        MotionDiagnostics.RecordGeometryCall(sampling ? _geomClock.Elapsed.TotalMilliseconds : 0, false, 0);
    }        // Pass 13 diagnostic: report whether the actual HWND rectangle overlaps
        // the taskbar strip at this moment (computed from real rects — never
        // inferred from the offset switch). Taskbar rect via the same
        // SHAppBarMessage source used by ComputeTaskbarHeightDips.
        private void LogP13Region(string state)
        {
            if (!MotionDiagnostics.EnableP13) return;
            if (!GetWindowRect(_hwnd, out RECT rc)) return;
            var data = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
            SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
            bool overlaps = rc.Bottom > data.rc.Top && rc.Top < data.rc.Bottom;
            Logger.Info(
                $"[MOTION-P13] REGION state={state} " +
                $"hwndX={rc.Left} hwndY={rc.Top} hwndW={rc.Right - rc.Left} hwndH={rc.Bottom - rc.Top} " +
                $"taskbarTop={data.rc.Top} taskbarBottom={data.rc.Bottom} overlapsTaskbar={overlaps} " +
                $"offset={MotionDiagnostics.PositionYOffsetDips}");
        }

        private double GetScaleFactor()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi / 96.0;
    }

    /// <summary>
    /// Reads an optional env override for an animation duration (ms), clamped to
    /// a sane range. Pass 26 A/B: HALO_P26_EXPAND_MS / HALO_P26_COLLAPSE_MS.
    /// </summary>
    private static double ParseDurationMs(string envName, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double ms)
            && ms >= 50 && ms <= 2000)
            return ms;
        return fallback;
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

    /// <summary>
    /// PASS 53: dedicated native drop-target overlay (GOAL 2 root-cause fix).
    ///
    /// The layered main Halo HWND has an all-alpha-0 redirection surface, so
    /// the native hit-test (and therefore OLE's drop-target lookup) skips it
    /// AND its DesktopChildSiteBridge child at the pill — drags resolve to
    /// Shell_TrayWnd and the registered IDropTarget never fires (Pass 49/50/52
    /// forensics; CLEAR_LAYERED proved the downstream chain works).
    ///
    /// This overlay is a separate raw top-level window: layered with uniform
    /// alpha=1 it is visually invisible (1/255) yet nonzero-alpha, so
    /// WindowFromPoint at the pill resolves to it and OLE consults its
    /// registered target. It is kept directly BELOW the main HWND so pointer
    /// input continues to reach the pill exactly as it does today (click-to-
    /// expand unaffected), and it is region-clipped to the live SetWindowRgn
    /// shape so only the pill (and the shelf region it grows into during a
    /// drag) is ever a drop target — never the envelope, dashboard, or taskbar.
    /// </summary>
    private sealed class PillDropOverlay
    {
        // The single live overlay, reachable from the native WndProc.
        private static PillDropOverlay? _live;

        // Rooted for the process lifetime so the unmanaged class registration
        // always has a valid callback pointer.
        private static readonly WndProcDelegate WndProcStub = WndProc;

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    return (IntPtr)MA_NOACTIVATE; // never steal activation
                case WM_ERASEBKGND:
                    return (IntPtr)1; // invisible window paints nothing
                case WM_LBUTTONDOWN:
                    // Safety net: the overlay sits BELOW the main HWND so input
                    // is expected to keep reaching the pill (island input). If a
                    // click ever lands here instead, preserve click-to-expand.
                    try { App.IslandController.NotifyIslandClick(); } catch { }
                    return IntPtr.Zero;
                default:
                    return DefWindowProcW(hWnd, msg, wParam, lParam);
            }
        }

        private readonly IntPtr _hwnd;
        private readonly IntPtr _mainHwnd;
        private int _lastX = int.MinValue, _lastY = int.MinValue, _lastW, _lastH;
        private bool _lastShown;

        public IntPtr Hwnd => _hwnd;

        private PillDropOverlay(IntPtr hwnd, IntPtr mainHwnd)
        {
            _hwnd = hwnd;
            _mainHwnd = mainHwnd;
        }

        public static PillDropOverlay? Create(IntPtr mainHwnd)
        {
            try
            {
                if (_live != null) return _live;

                IntPtr hInstance = GetModuleHandleW(null);
                var cls = new WNDCLASSW
                {
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcStub),
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = DropOverlayClass,
                };
                ushort classAtom = RegisterClassW(ref cls);
                if (classAtom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS — fine, reused
                        Logger.Info($"[DROP-OVERLAY] RegisterClassW error={err}");
                }

                IntPtr hwnd = CreateWindowExW(
                    WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
                    DropOverlayClass, "",
                    WS_POPUP,
                    0, 0, 10, 10,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                {
                    Logger.Error($"[DROP-OVERLAY] CreateWindowExW failed error={Marshal.GetLastWin32Error()}");
                    return null;
                }

                // Uniform alpha=1: invisible (1/255) but hit-testable (alpha>0).
                SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);

                _live = new PillDropOverlay(hwnd, mainHwnd);
                Logger.Info($"[DROP-OVERLAY] created hwnd=0x{hwnd.ToInt64():X} class={DropOverlayClass}");
                return _live;
            }
            catch (Exception ex)
            {
                Logger.Error("[DROP-OVERLAY] create failed", ex);
                return null;
            }
        }

        /// <summary>Mirror the live SetWindowRgn rect (client px). Shown only
        /// while the pill is the collapsed (or drag-grown) shape, hidden with
        /// the main HWND during fullscreen, and clipped to the pill radius so
        /// only the interactive shape is a drop target.</summary>
        public void Update(int regX, int regY, int regW, int regH, bool visible)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (!visible)
            {
                if (_lastShown)
                {
                    _lastShown = false;
                    ShowWindow(_hwnd, SW_HIDE);
                }
                return;
            }

            POINT origin = new() { X = regX, Y = regY };
            if (!ClientToScreen(_mainHwnd, ref origin)) return;
            int x = origin.X, y = origin.Y, w = regW, h = regH;
            if (w < 1 || h < 1) return;

            if (_lastShown && x == _lastX && y == _lastY && w == _lastW && h == _lastH)
                return; // no geometry change — already positioned & visible

            _lastX = x; _lastY = y; _lastW = w; _lastH = h; _lastShown = true;

            // Position and pin z-order directly below the main HWND so the pill
            // still receives pointer input (layered main = alpha-0, hit-test
            // skips it; the overlay is the hit-testable layer under it).
            SetWindowPos(_hwnd, _mainHwnd, x, y, w, h, SWP_NOACTIVATE);

            double scale = GetDpiForWindow(_mainHwnd) / 96.0;
            int rad = (int)Math.Round(DropOverlayRegionRadiusDip * scale);
            IntPtr hrgn = CreateRoundRectRgn(0, 0, w, h, rad, rad);
            if (hrgn != IntPtr.Zero)
            {
                if (!SetWindowRgn(_hwnd, hrgn, true))
                    DeleteObject(hrgn);
            }

            if (!IsWindowVisible(_hwnd))
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }

        /// <summary>Z-order guard (150 ms tick): keep the overlay adjacent below
        /// the main HWND, and mirror the main window's fullscreen hide.</summary>
        public void ReassertZOrder(IntPtr mainHwnd, bool hidden)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (hidden)
            {
                if (_lastShown)
                {
                    _lastShown = false;
                    ShowWindow(_hwnd, SW_HIDE);
                }
                return;
            }
            if (!_lastShown) return; // Update() owns showing
            SetWindowPos(_hwnd, mainHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }
}
