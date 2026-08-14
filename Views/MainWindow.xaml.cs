using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using DynamicIsland.ViewModels;
using DynamicIsland.Services;
using DynamicIsland.Helpers;
using WinRT;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;

namespace DynamicIsland.Views;

/// <summary>
/// Taskbar widget shell window.
///
/// Routing rules:
///  - Left-click  → IslandController.NotifyIslandClick()   (expand/collapse toggle)
///  - Hover enter → IslandController.NotifyMouseEnter()    (cancel auto-collapse)
///  - Hover exit  --> IslandController.NotifyMouseLeave()    (restart short auto-collapse)
///  - Deactivated → IslandController.NotifyFocusLost()     (immediate collapse)
///
/// Stable-window architecture (Pass 23/24 validated → Pass 25 production):
/// The HWND is pre-sized ONCE to the fixed expanded envelope (expanded
/// dashboard + taskbar-lift strip) and is never resized during expand/collapse
/// — per-frame native SIZE changes were the cause of the black leading-edge
/// band. The transition runs INSIDE the window: a RectangleGeometry clip on
/// the root reveals the dashboard upward from the pill (PASS 30/32: the pill
/// is the taskbar anchor and never moves — the dashboard grows from its top
/// edge and the pill fades away as it takes over), and a rounded SetWindowRgn
/// limits both the visible surface AND hit-testing to the current reveal
/// rect. WS_EX_LAYERED (PASS 31) makes the unpainted window surface
/// per-pixel-alpha transparent, so the desktop shows through and stays
/// clickable everywhere outside the visible Halo shape.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; } = new();
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configuration;

    // ── Stable-window geometry (source of truth: WindowService.ResolveProfileSize) ──
    // The expanded profile defines the fixed HWND envelope; the taskbar strip
    // height is measured by WindowService. Resolved from the service at arm time.
    private double _windowWidthDip = 800;
    private double _dashboardHeightDip = 664;
    private double _stripHeightDip = 48;
    private const double RegionRadiusDip = 24; // matches the design's CornerRadius

    // Visual stage state: clip reveal + pill translate + region.
    private RectangleGeometry? _revealClip;
    private TranslateTransform? _pillTranslate;
    private bool _clipArmed;
    private bool _stageActive;
    private bool _expanding;
    private bool _drivingDashboard; // clip/translate (real compact↔expanded)
    private bool _drivingWidth;     // compact pill width tweak
    private double _clipTop = 664;  // DIPs; dashboard height → pill strip only
    private double _clipFrom, _clipTo;
    private double _tyFrom, _tyTo;
    private double _pillBottomDip;  // DIPs; bottom edge of the pill strip (window-client)
    private bool _settledCollapsed = true;
    private bool _settledExpanded;
    private double _compactPillW = 350;
    private double _widthFrom, _widthTo;

    // Popup / pill-growth stage (Pass 27): the file shelf (340), clipboard
    // preview (180) and drag-over (80) grow the pill upward from the taskbar
    // strip. The stable-window stage models this as a third shape alongside the
    // compact pill and the expanded dashboard: the reveal clip top animates
    // between the strip top (664) and (strip top − popup height), and the region
    // tracks a pill-width × popup-height rect anchored at the window bottom.
    private bool _drivingPopup;
    private bool _settledPopup;
    private double _popupHeightDip;      // settled popup height (window-client DIPs)
    private double _popupFrom, _popupTo; // clip-top endpoints for the popup tween

    // Current region rect in window-client PHYSICAL pixels, written by
    // ApplyStableRegion/ApplyInitialRegion — the hover monitor's source of
    // truth so it matches SetWindowRgn exactly.
    private int _regX0, _regY0, _regW, _regH;
    private bool _regValid;

    // Last pill height written by SetPillHeight (PASS 29). The pill's painted
    // surface must fill the region or the unpainted window surface shows as a
    // black band; the height is driven to the region height in every stage.
    // Delta-guarded so the per-frame popup tracking only re-lays-out the pill
    // subtree when the value actually moves.
    private double _lastPillHSet = double.NaN;

    private void SetPillHeight(double h)
    {
        if (!double.IsNaN(_lastPillHSet) && Math.Abs(h - _lastPillHSet) < 0.5) return;
        _lastPillHSet = h;
        PillBorder.Height = h;
    }

    // Hover-state monitor: the per-frame SetWindowRgn updates in the
    // stable-window mode can leave WinUI's PointerExited undelivered, so
    // IslandController's _mouseIsOver can be stuck true — which would block the
    // outside-click collapse AND the auto-collapse timer. PointerEntered still
    // fires (verified), so we track enter here and clear the hover state ONCE
    // when the cursor provably leaves the VISIBLE region (GetCursorPos against
    // the current region rect — a cursor in the taskbar strip below the expanded
    // dashboard is outside the region but inside the window rect).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverMonitorTimer;
    private bool _pointerOver;

    public MainWindow()
    {
        InitializeComponent();

        // Acrylic — PASS 33: the window-level DesktopAcrylicController backdrop
        // is restored as the production glass. Pass 27 removed it because without
        // WS_EX_LAYERED the DWM material ignored SetWindowRgn and frosted the
        // whole fixed envelope; PASS 31's per-pixel-alpha layered window clips it
        // to the region (verified on the desktop), so the glass now follows the
        // visible Halo shape and the in-app opaque card is retired. See
        // SetAcrylicBackdrop for the full chain. HALO_NOACRYLIC=1 disables the
        // glass; HALO_P27_WINACRYLIC=1 is kept as an alias of the default.
        SetAcrylicBackdrop();

        // PASS 33: release the system backdrop BEFORE teardown. An active
        // DesktopAcrylicController left registered as a backdrop target on a
        // closing window crashes WinUI at Application.Exit() (measured: exit
        // code 139 with the backdrop armed, clean exit without it). Disposing on
        // Closed detaches the DWM material cleanly.
        Closed += (_, _) =>
        {
            _acrylicController?.Dispose();
            _acrylicController = null;
            _configuration = null;
        };
    }

    private void SetAcrylicBackdrop()
    {
        // PASS 38 (GOAL 3): translucent dark glass, geometrically confined to
        // the visible Halo surface.
        //
        // Layer evidence (Pass 38):
        //  - The dark "rectangle" survives Pass 37's default (shaped in-app
        //    acrylic, NO window-level backdrop) — so the in-app brush and the
        //    window-level backdrop are BOTH exonerated as its source; the
        //    rectangle is the DWM frame/shadow for the fixed HWND envelope,
        //    addressed by WindowService.ReFrameWindow (SWP_FRAMECHANGED) in
        //    this pass.
        //  - In-app AcrylicBrush reads as a SOLID dark slab (the current build's
        //    "solid grey/black") because inside the layered window there is
        //    nothing behind it to sample — acrylic falls back to its tint.
        //    Genuine translucency requires the window-level system backdrop,
        //    which samples the real desktop and is confined to the SetWindowRgn
        //    shape under WS_EX_LAYERED (measured on a desktop, Pass 33).
        //
        // Therefore the PASS 38 default restores the window-level
        // DesktopAcrylicController (dark, translucent) as the glass; the shaped
        // in-app brush survives behind HALO_P37_SHAPED=1 as the zero-backdrop
        // A/B, and HALO_NOACRYLIC=1 / HALO_P38_TESTE=1 are the isolation configs
        // for the shadow-isolation sequence (Test A/B/C and Test E).
        //
        // HALO_P27_WINACRYLIC=1 remains an alias of the (now default) window-level
        // backdrop.

        // Test E (GOAL 1): bright diagnostic surface — a black envelope
        // rectangle around it is unmistakable against any wallpaper.
        if (Helpers.MotionDiagnostics.EnableP38TestE)
        {
            var bright = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x00, 0xFF));
            PillBorder.Background = bright;
            DashboardBorder.Background = bright;
            Helpers.Logger.Info("[P38] TestE: bright magenta diagnostic surface armed — any black rectangle around it = the envelope artifact.");
            return;
        }

        if (Environment.GetEnvironmentVariable("HALO_NOACRYLIC") == "1")
        {
            PillBorder.Background = null;
            DashboardBorder.Background = null;
            Helpers.Logger.Info("[WINDOW] Acrylic: disabled (HALO_NOACRYLIC=1) — transparent cards over the window surface.");
            return;
        }

        // PASS 39 (GOAL 1): MODE A (HALO_P39_NO_BACKDROP=1) and MODE C
        // (HALO_P39_RAW_WINDOW=1) skip the window-level system backdrop while
        // keeping XAML content + WS_EX_LAYERED + region + presenter — the shaped
        // in-app brush below becomes the surface. If the black rectangle is the
        // DWM system-backdrop material, it must vanish in this mode; if it
        // persists, the pixels belong to a different mechanism.
        // PASS 40: the NUKE_XAML and NO_WINDOW_CONTENT diagnostic modes also
        // disable the backdrop (both tests require "no acrylic").
        // PASS 42: EMPTY_CONTENT is the P42 binary test that requires the same
        // no-backdrop condition.
        bool p39DisableBackdrop = Helpers.MotionDiagnostics.EnableP39NoBackdrop
            || Helpers.MotionDiagnostics.EnableP39RawWindow
            || Helpers.MotionDiagnostics.EnableP40NukeXaml
            || Helpers.MotionDiagnostics.EnableP40NoWindowContent
            || Helpers.MotionDiagnostics.EnableP42EmptyContent;
        bool wantWindowBackdrop = !p39DisableBackdrop
            && DesktopAcrylicController.IsSupported()
            && (Environment.GetEnvironmentVariable("HALO_P37_SHAPED") != "1"
                || Environment.GetEnvironmentVariable("HALO_P27_WINACRYLIC") == "1");
        if (p39DisableBackdrop)
            Helpers.Logger.Info("[P39-SURFACE] MODE A/C active — window-level backdrop SKIPPED (pixel-source isolation).");
        if (wantWindowBackdrop)
        {
            _acrylicController = new DesktopAcrylicController();

            _configuration = new SystemBackdropConfiguration();

            // Force active backdrop state permanently so the window never goes solid gray on deactivation
            _configuration.IsInputActive = true;
            // PASS 34: pin the glass theme to Dark so the tint is the deterministic
            // dark acrylic regardless of the user's system light/dark mode (the
            // Halo content is dark-themed and would clash with a light wash).
            _configuration.Theme = SystemBackdropTheme.Dark;

            // PASS 38 (GOAL 3): translucent dark glass — the wallpaper's
            // luminance shows through the tint (LuminosityOpacity) while the
            // dark tint (TintOpacity) keeps the Halo content readable on any
            // wallpaper. Tune these two floats only if the glass reads too
            // light/dark on the user's desktop.
            _acrylicController.TintColor = Windows.UI.Color.FromArgb(255, 20, 20, 20);
            _acrylicController.TintOpacity = 0.45f;
            _acrylicController.LuminosityOpacity = 0.55f;

            var supportsSystemBackdrop = this.As<ICompositionSupportsSystemBackdrop>();
            _acrylicController.AddSystemBackdropTarget(supportsSystemBackdrop);
            _acrylicController.SetSystemBackdropConfiguration(_configuration);
            PillBorder.Background = null;
            DashboardBorder.Background = null;
            Helpers.Logger.Info("[WINDOW] Acrylic: window-level DesktopAcrylicController (PASS 38 default — region-clipped glass under WS_EX_LAYERED, dark translucent).");
            return;
        }

        // PASS 37 shaped in-app translucent acrylic on the pill and dashboard
        // borders ONLY (A/B via HALO_P37_SHAPED=1, and the fallback when the
        // window-level backdrop is unsupported). The brush is geometrically
        // confined to the visible Halo shapes so it can NEVER render a
        // full-envelope frost; note it reads more solid than the system
        // backdrop because there is nothing behind it to blur inside the
        // layered window.
        var shaped = new AcrylicBrush
        {
            TintColor = Windows.UI.Color.FromArgb(255, 46, 46, 46),
            TintOpacity = 0.55,
            TintLuminosityOpacity = 0.35,
        };
        PillBorder.Background = shaped;
        DashboardBorder.Background = shaped;
        Helpers.Logger.Info("[WINDOW] Acrylic: shaped in-app translucent tint on pill/dashboard (A/B HALO_P37_SHAPED — no window-level backdrop).");
    }

    // ── Stable-window visual driver ────────────────────────────────────────

    /// <summary>
    /// Production stable-window configuration. Called by App after WindowService
    /// exists but before InitializeWindow pre-sizes the HWND, so the
    /// clip/transform stage is ready for the first present.
    /// </summary>
    public void ConfigureStableWindow()
    {
        // Resolve the fixed envelope from the service (single source of truth).
        var expanded = App.WindowService.ResolveProfileSize(WindowProfile.Expanded);
        _windowWidthDip = expanded.Width;
        _dashboardHeightDip = expanded.Height;

        // Compact state: clip reveals only the pill strip at the bottom. The
        // clip rect lives in the root's coordinate space, which is 0x0 until
        // the window is pre-sized and shown — assigning the clip while the root
        // has NO size produced a broken (all-black) composition clip in this
        // WinUI build, so the assignment is deferred to the first layout where
        // the root reaches its fixed size (OnRootLayoutUpdated).
        _clipTop = _dashboardHeightDip;
        _revealClip = new RectangleGeometry();
        RootGrid.LayoutUpdated += OnRootLayoutUpdated;

        // Dashboard pinned to the window top at its production height; the pill
        // stays bottom-anchored in the remaining taskbar strip.
        DashboardBorder.Height = _dashboardHeightDip;
        DashboardBorder.VerticalAlignment = VerticalAlignment.Top;

        _pillTranslate = new TranslateTransform();
        PillBorder.RenderTransform = _pillTranslate;

        // Motion events are subscribed via a local hoisted BEFORE any
        // null-conditional access to App.WindowService (the `?.` below would
        // otherwise make the flow analysis treat the later plain accesses as
        // maybe-null → CS8602). App assigns the singleton before this call.
        var windowService = App.WindowService;
        windowService.MotionSegmentStarted += OnMotionSegmentStarted;
        windowService.MotionProgressChanged += OnMotionProgress;

        // Hover monitor — 200 ms is well below the 2 s auto-collapse window, so
        // a single leave transition clears the stale hover state long before
        // the timer would matter.
        _hoverMonitorTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hoverMonitorTimer.Interval = TimeSpan.FromMilliseconds(200);
        _hoverMonitorTimer.IsRepeating = true;
        _hoverMonitorTimer.Tick += (_, _) => OnHoverMonitorTick();
        _hoverMonitorTimer.Start();

        // Constrain the compact pill to its production width at its home
        // position (X=0). In the fixed 800-wide window the pill content would
        // otherwise stretch to the full window width and the compact region
        // would expose a full-width frost/hit-test band over the taskbar. The
        // pill then behaves exactly like the production compact window (width
        // from ResolveProfileSize, cards centered within it).
        _compactPillW = Math.Max(windowService.ResolveProfileSize(WindowProfile.Collapsed).Width, 1);
        PillBorder.HorizontalAlignment = HorizontalAlignment.Left;
        PillBorder.Width = _compactPillW;
        Helpers.Logger.Info($"[WINDOW] StableStageArmed grid={RootGrid.ActualWidth:F0}x{RootGrid.ActualHeight:F0} " +
                            $"pillW={_compactPillW:F0} tMs={Environment.TickCount64}");

        StartP30Dump();
    }

    /// <summary>
    /// Applies the root clip once the window content is laid out at its fixed
    /// size (the arm-time root is 0x0; a clip assigned there broke the whole
    /// surface). Fires from RootGrid.LayoutUpdated — guarded to run exactly once.
    /// </summary>
    private void OnRootLayoutUpdated(object? sender, object e)
    {
        if (_clipArmed || _revealClip == null) return;
        if (RootGrid.ActualWidth < 700 || RootGrid.ActualHeight < 600) return;
        _clipArmed = true;
        RootGrid.LayoutUpdated -= OnRootLayoutUpdated; // once-only; stop the per-layout no-op
        // The taskbar strip is measured by WindowService during InitializeWindow
        // (before the first layout) — pick it up so the stage matches the envelope.
        _stripHeightDip = App.WindowService.TaskbarHeightDips;
        _pillBottomDip = _dashboardHeightDip + _stripHeightDip;
        // PASS 29: pin the pill's painted surface to the strip height so the
        // acrylic fills the compact region (the content alone is only 28-44 DIPs
        // and left the unpainted window surface exposed as a black band above).
        SetPillHeight(_stripHeightDip);
        _revealClip.Rect = new Windows.Foundation.Rect(
            0, _clipTop, _windowWidthDip, _dashboardHeightDip + _stripHeightDip - _clipTop);
        RootGrid.Clip = _revealClip;
        ApplyStableRegion();
        Helpers.Logger.Info($"[WINDOW] ClipArmed grid={RootGrid.ActualWidth:F0}x{RootGrid.ActualHeight:F0} " +
                            $"clipTop={_clipTop:F0} strip={_stripHeightDip:F0} tMs={Environment.TickCount64}");
    }

    // ── Region (visible + hit-test) ────────────────────────────────────────
    // SetWindowRgn clips the HWND's visible AND hit-test area to the current
    // reveal rect (window-client physical pixels, derived from the root clip's
    // DIP rect × DPI). The fixed-size window then presents ONLY the revealed
    // region — the acrylic frost and the pill/dashboard content — while the
    // desktop shows through (and clicks pass through) everywhere else. No
    // native resize occurs; SetWindowRgn takes ownership of the region handle.

    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Applies the COMPACT region to the pre-sized HWND before the window's
    /// first present (called right before Window.Activate), so the very first
    /// frame is already pill-limited — no default-styled opaque flash. The
    /// layout/arm path re-applies the identical rect after first layout.
    /// InitializeWindow (which measured the taskbar) has already run by now, so
    /// the strip height is read from the service — never the arm-time default.
    /// </summary>
    public void ApplyInitialRegion()
    {
        _stripHeightDip = App.WindowService.TaskbarHeightDips;
        _compactPillW = Math.Max(App.WindowService.ResolveProfileSize(WindowProfile.Collapsed).Width, 1);
        // PASS 29: the pill border is content-sized (no Height); its painted acrylic
        // surface must fill the region or the unpainted window surface shows as a
        // black band. Pin it to the strip height for the very first present.
        SetPillHeight(_stripHeightDip);
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double s = GetDpiForWindow(hwnd) / 96.0;
        int rad = (int)Math.Round(RegionRadiusDip * s);
        int y0 = (int)Math.Round(_dashboardHeightDip * s);
        int x1 = (int)Math.Round(_compactPillW * s);
        int y1 = (int)Math.Round((_dashboardHeightDip + _stripHeightDip) * s);
        _regX0 = 0; _regY0 = y0; _regW = x1; _regH = y1 - y0; _regValid = true;
        IntPtr hrgn = CreateRoundRectRgn(0, y0, x1, y1, rad, rad);
        if (hrgn != IntPtr.Zero && !SetWindowRgn(hwnd, hrgn, true))
            DeleteObject(hrgn);
        Helpers.Logger.Info($"[WINDOW] InitialRegion rect=(0,{y0},{x1}x{y1 - y0}) rad={rad} tMs={Environment.TickCount64}");
    }

    private void ApplyStableRegion()
    {
        if (_revealClip == null) return;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double s = GetDpiForWindow(hwnd) / 96.0;

        // Region rect (window-client DIPs) depends on the current state:
        //  - collapsed settle → the compact pill rect at its home position,
        //  - expanded settle   → the dashboard rect ONLY (PASS 37: the pill
        //    disappears on click; dashboardBottom = pillTop = 664 sits flush
        //    with the taskbar top, and the strip below stays outside the Halo
        //    — visible + clickable),
        //  - animating         → the reveal band between the clip top and the
        //    reveal bottom (full dashboard width; pill-width only while the
        //    band is confined to the pill strip on the collapse tail).
        double x0 = 0, y0, w, h;
        if (_settledCollapsed)
        {
            y0 = _dashboardHeightDip; w = _compactPillW; h = _stripHeightDip;
        }
        else if (_settledExpanded)
        {
            // PASS 37: only the dashboard exists when expanded (the pill strip
            // below is outside the region — taskbar visible + clickable).
            y0 = 0; w = _windowWidthDip; h = _dashboardHeightDip;
        }
        else if (_settledPopup)
        {
            // Settled popup: pill-width × popup height at the window bottom.
            y0 = _dashboardHeightDip + _stripHeightDip - _popupHeightDip;
            w = _compactPillW;
            h = _popupHeightDip;
        }
        else
        {
            y0 = _clipTop;
            // PASS 37: during a real dashboard reveal the band is the full
            // dashboard width (the dashboard spans the window); it stays
            // pill-width only while the band is confined to the pill strip on
            // the collapse tail. Popups always stay pill-width.
            w = (_drivingPopup || _settledPopup || _clipTop >= _dashboardHeightDip - 0.5) ? _compactPillW : _windowWidthDip;
            // Reveal bottom: dashboard band (bottom fixed at dashboardHeight),
            // extended to include the pill strip only on the collapse tail
            // when the pill is fading back in.
            h = RevealBottomDip() - _clipTop;
            if (h < 1) h = 1;
        }

        int rad = (int)Math.Round(RegionRadiusDip * s);
        _regX0 = (int)Math.Round(x0 * s);
        _regY0 = (int)Math.Round(y0 * s);
        _regW = (int)Math.Round((x0 + w) * s) - _regX0;
        _regH = (int)Math.Round((y0 + h) * s) - _regY0;
        _regValid = true;
        IntPtr hrgn = CreateRoundRectRgn(_regX0, _regY0, _regX0 + _regW, _regY0 + _regH, rad, rad);
        if (hrgn != IntPtr.Zero)
        {
            // On success the system takes ownership of the region (and frees the
            // previous one); on failure we must free it ourselves to avoid a
            // per-frame handle leak during animation.
            if (!SetWindowRgn(hwnd, hrgn, true))
                DeleteObject(hrgn);
        }

        // PASS 53: mirror the live region onto the invisible drop-target overlay
        // so the pill shape is always exactly the native drop target.
        App.WindowService.UpdateDropOverlay(_regX0, _regY0, _regW, _regH, _settledCollapsed);
    }

    /// <summary>
    /// PASS 37: bottom edge of the current reveal band (window-client DIPs).
    /// During a real dashboard transition the band is bottom-anchored at
    /// dashboardHeight (= pillTop = taskbar top) so the dashboard reveals ONLY
    /// upward and the taskbar is never covered; on the collapse tail the band
    /// extends to include the pill strip so the pill fades back in at its home
    /// position. Popups keep the pill bottom as their anchor.
    /// </summary>
    private double RevealBottomDip()
    {
        if (!_drivingDashboard) return _pillBottomDip;
        if (!_expanding && _clipTop >= _dashboardHeightDip - 0.5)
            return _dashboardHeightDip + _stripHeightDip;
        return _dashboardHeightDip;
    }

    // ── PASS 30 diagnostics (HALO_P30_DUMP=1) ─────────────────────────────
    // Headless sessions cannot capture the composited desktop, so the app dumps
    // its own truth: the ACTUAL applied window region (in-process, DPI-correct)
    // and pixel renders of the pill/dashboard/root via RenderTargetBitmap. Used
    // to attribute the remaining black surface (acrylic tint vs unpainted
    // window surface) and the envelope shadow without screen capture.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _p30DumpTimer;
    private int _p30DumpCount;

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    private static extern bool GetRgnBox(IntPtr rgn, out RECT rect);

    // PASS 32 (GOAL 1 evidence): layered-mode probe — the ex-style must carry
    // WS_EX_LAYERED and GetLayeredWindowAttributes must report NO LWA_ALPHA /
    // LWA_COLORKEY for the window to be in DirectComposition per-pixel-alpha
    // mode (the only layered variant that lets the desktop show through the
    // unpainted surface).
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out int crKey, out byte bAlpha, out uint dwFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint LWA_COLORKEY = 0x00000001;

    private void StartP30Dump()
    {
        if (Environment.GetEnvironmentVariable("HALO_P30_DUMP") != "1") return;
        _p30DumpTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _p30DumpTimer.Interval = TimeSpan.FromSeconds(2);
        _p30DumpTimer.IsRepeating = true;
        _p30DumpTimer.Tick += async (_, _) =>
        {
            if (_p30DumpCount++ >= 6) { _p30DumpTimer?.Stop(); return; }
            string state = _settledExpanded ? "expanded" : _settledPopup ? "popup" : "collapsed";
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var rgn = CreateRectRgn(0, 0, 0, 0);
                int t = GetWindowRgn(hwnd, rgn);
                if (GetRgnBox(rgn, out RECT rb))
                    Helpers.Logger.Info($"[P30] state={state} rgnType={t} box=({rb.Left},{rb.Top},{rb.Right - rb.Left}x{rb.Bottom - rb.Top}) " +
                                        $"pillH={PillBorder.ActualHeight:F0} pillW={PillBorder.ActualWidth:F0} " +
                                        $"pillTy={_pillTranslate?.Y ?? double.NaN:F1} " +
                                        $"dashH={DashboardBorder.ActualHeight:F0} dashW={DashboardBorder.ActualWidth:F0} " +
                                        $"dashVis={DashboardBorder.Visibility} pillVis={PillBorder.Visibility}");
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                bool layered = (ex & WS_EX_LAYERED) != 0;
                bool lwa = GetLayeredWindowAttributes(hwnd, out int crKey, out byte bAlpha, out uint dwFlags);
                // layered && no LWA_ALPHA/COLORKEY => DComp per-pixel alpha.
                bool ppxAlpha = layered && (!lwa || (dwFlags & (LWA_ALPHA | LWA_COLORKEY)) == 0);
                Helpers.Logger.Info($"[P30] layeredStyle={layered} lwa={lwa} flags=0x{dwFlags:X} alpha={bAlpha} " +
                                    $"perPixelAlpha={ppxAlpha}");
                if (PillBorder.Visibility == Visibility.Visible)
                    await RenderToPngAsync(PillBorder, $"C:/tmp/p30_{_p30DumpCount}_{state}_pill.png");
                if (DashboardBorder.Visibility == Visibility.Visible)
                    await RenderToPngAsync(DashboardBorder, $"C:/tmp/p30_{_p30DumpCount}_{state}_dash.png");
                if (RootGrid.Visibility == Visibility.Visible)
                    await RenderToPngAsync(RootGrid, $"C:/tmp/p30_{_p30DumpCount}_{state}_root.png");
            }
            catch (Exception ex)
            {
                Helpers.Logger.Error("[P30] dump failed", ex);
            }
        };
        _p30DumpTimer.Start();
        Helpers.Logger.Info("[P30] dump harness started.");
    }

    private static async Task RenderToPngAsync(UIElement element, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixels = await rtb.GetPixelsAsync();
        var bytes = pixels.ToArray();
        using var ms = new MemoryStream();
        var ras = ms.AsRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ras);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        System.IO.File.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>
    /// Re-bases the clip/translate from the CURRENT visual state so retargets
    /// and reversals continue without a jump (same pattern as the Pass 9
    /// opacity/scale choreography in MainWindowViewModel). Compact-width tweaks
    /// (height unchanged) route to the pill-width stage instead.
    /// </summary>
    private void OnMotionSegmentStarted(WindowService.MotionSegment seg)
    {
        bool heightChanged = Math.Abs(seg.TargetHeight - seg.FromHeight) > 0.5;

        if (seg.IsDashboardTransition && heightChanged)
        {
            // Real compact↔expanded transition — drive clip + pill translate.
            Helpers.Logger.Info($"[WINDOW] SegmentStart grid={RootGrid.ActualWidth:F0}x{RootGrid.ActualHeight:F0} " +
                                $"pillH={PillBorder.ActualHeight:F0} dashH={DashboardBorder.ActualHeight:F0} tMs={Environment.TickCount64}");
            _stageActive = true;
            _drivingDashboard = true;
            _drivingWidth = false;
            _drivingPopup = false;
            _expanding = seg.Expanding;
            _settledCollapsed = false;
            _settledExpanded = false;
            _settledPopup = false;
            // PASS 29/30/31: the pill stays a full-strip capsule during dashboard
            // transitions — it never lifts (ty=0, taskbar anchor) and never goes
            // content-sized (its acrylic surface must fill the strip region).
            SetPillHeight(_stripHeightDip);
            _clipFrom = _clipTop;
            _clipTo = seg.Expanding ? 0 : _dashboardHeightDip;
            double curTy = _pillTranslate?.Y ?? 0;
            _tyFrom = curTy;
            // PASS 30/32 (motion origin): the pill is the TASKBAR anchor — it
            // never lifts off the taskbar during expansion. The dashboard grows
            // upward from the pill's top edge (the reveal clip top animates
            // 664→0, dashboardBottom = pillTop = 664) while the pill stays put
            // and fades away; the expanded settle region is dashboard-only and
            // the strip below it returns to the taskbar.
            _tyTo = 0;
            if (!seg.Expanding)
            {
                // The collapse settles to the CURRENT compact width (it may have
                // changed while expanded via SetOverrideCollapsedWidth).
                _compactPillW = Math.Max(seg.TargetWidth, 1);
                PillBorder.Width = _compactPillW;
            }
        }
        else if (heightChanged && seg.TargetHeight < _dashboardHeightDip - 100)
        {
            // Popup / pill-growth segment (file shelf 340, clipboard preview 180,
            // drag-over 80): NOT a dashboard transition — the pill grows upward
            // from the taskbar strip, so drive the reveal clip + region at pill
            // width (the HWND stays fixed; the visible region grows instead).
            _stageActive = true;
            _drivingDashboard = false;
            _drivingWidth = false;
            _drivingPopup = true;
            _settledCollapsed = false;
            _settledExpanded = false;
            _settledPopup = false;
            _popupFrom = _clipTop;
            _popupTo = (_dashboardHeightDip + _stripHeightDip) - Math.Max(seg.TargetHeight, 1);
            _compactPillW = Math.Max(seg.TargetWidth, 1);
            PillBorder.Width = _compactPillW;
            // PASS 29: the pill grows WITH the region so the acrylic always fills
            // the exposed band (the popup content alone is shorter than the target
            // height and left a black band of unpainted window surface).
            SetPillHeight(Math.Max(_pillBottomDip - _clipFrom, 1));
            Helpers.Logger.Info($"[WINDOW] PopupStart targetH={seg.TargetHeight:F0} clipTop {_popupFrom:F0}->{_popupTo:F0} pillW={_compactPillW:F0}");
        }
        else if (!heightChanged && seg.TargetHeight < _dashboardHeightDip - 100)
        {
            // Compact↔compact width change (controller publish or pill content
            // width): height unchanged AND compact — animate the pill width and
            // region, not the reveal. Keyed to the dashboard height (not the
            // strip height) so non-48-DIP taskbars route correctly.
            _stageActive = true;
            _drivingDashboard = false;
            _drivingWidth = true;
            _drivingPopup = false;
            _widthFrom = _compactPillW;
            _widthTo = Math.Max(seg.TargetWidth, 1);
            // PASS 29: the pill height follows the REGION height, not the strip:
            // a compact-width tweak can arrive while a popup is settled or
            // mid-animation (region still popup-sized) and must keep the acrylic
            // filling it. Yields 48 in normal compact (664→712) and the popup
            // height when one is open.
            SetPillHeight(Math.Max(_pillBottomDip - _clipTop, 1));
        }
    }

    private void OnMotionProgress(double progress)
    {
        if (!_stageActive) return;
        double k = Math.Clamp(progress, 0.0, 1.0);

        if (_drivingDashboard)
        {
            if (_revealClip == null) return;
            _clipTop = _clipFrom + (_clipTo - _clipFrom) * k;
            if (_pillTranslate != null)
                _pillTranslate.Y = _tyFrom + (_tyTo - _tyFrom) * k;
            // PASS 37: the dashboard reveal is bottom-anchored — the clip's
            // BOTTOM edge stays fixed at dashboardHeight (= pillTop = taskbar
            // top); only the reveal TOP edge travels (664→0 on expand). The
            // pill strip below is excluded (the pill disappears on click); it
            // is re-included only on the collapse tail so the pill fades back
            // in at its home position. The dashboard itself never translates
            // or scales.
            _pillBottomDip = (_dashboardHeightDip + _stripHeightDip) + (_pillTranslate?.Y ?? 0);
            if (k >= 1.0)
            {
                // Terminal flush (settle can end below 1.0 on the early
                // rounded-rect settle) — force the exact final visual state.
                _clipTop = _clipTo;
                if (_pillTranslate != null) _pillTranslate.Y = _tyTo;
                _settledCollapsed = !_expanding;
                _settledExpanded = _expanding;
                // PASS 37: expanded settle is dashboard-only (the strip below
                // returns to the taskbar); the collapse settle keeps the strip
                // as the pill's home.
                _pillBottomDip = _expanding ? _dashboardHeightDip : _dashboardHeightDip + _stripHeightDip;
                _stageActive = false;
            }
            double rb = RevealBottomDip();
            _revealClip.Rect = new Windows.Foundation.Rect(
                0, _clipTop, _windowWidthDip, Math.Max(rb - _clipTop, 1));
            ApplyStableRegion();
        }
        else if (_drivingWidth)
        {
            _compactPillW = _widthFrom + (_widthTo - _widthFrom) * k;
            PillBorder.Width = _compactPillW;
            // PASS 29: keep the pill filling the region (popup-sized if a popup
            // is open, strip-height otherwise).
            SetPillHeight(Math.Max(_pillBottomDip - _clipTop, 1));
            if (k >= 1.0)
            {
                _compactPillW = _widthTo;
                PillBorder.Width = _compactPillW;
                _stageActive = false;
            }
            if (_revealClip != null)
            {
                _revealClip.Rect = new Windows.Foundation.Rect(
                    0, _clipTop, _windowWidthDip, _dashboardHeightDip + _stripHeightDip - _clipTop);
                ApplyStableRegion();
            }
        }
        else if (_drivingPopup)
        {
            if (_revealClip == null) return;
            // The pill grows upward from the taskbar strip: the clip top moves up
            // (revealing more of the strip) while the pill bottom stays anchored
            // to the window bottom. PASS 29: the pill's painted surface tracks the
            // region height so the acrylic fills the whole exposed band.
            _clipTop = _popupFrom + (_popupTo - _popupFrom) * k;
            _pillBottomDip = _dashboardHeightDip + _stripHeightDip;
            SetPillHeight(Math.Max(_pillBottomDip - _clipTop, 1));
            if (k >= 1.0)
            {
                _clipTop = _popupTo;
                if (_popupTo >= _dashboardHeightDip - 0.5)
                {
                    // Back to the compact strip.
                    _settledCollapsed = true;
                    _settledPopup = false;
                }
                else
                {
                    _settledPopup = true;
                    _popupHeightDip = (_dashboardHeightDip + _stripHeightDip) - _popupTo;
                }
                _stageActive = false;
            }
            _revealClip.Rect = new Windows.Foundation.Rect(
                0, _clipTop, _windowWidthDip, _pillBottomDip - _clipTop);
            ApplyStableRegion();
            if (k >= 1.0)
                Helpers.Logger.Info($"[WINDOW] PopupSettled clipTop={_clipTop:F0} region=({_regX0},{_regY0},{_regW}x{_regH}) pillHSet={PillBorder.Height:F0}");
        }
    }

    // ── Click to expand/collapse ───────────────────────────────────────────

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(RootGrid).Properties;
        if (!properties.IsLeftButtonPressed) return;

        // Don't trigger expand if the original source is a Button or
        // is inside a Button — those handle their own click logic.
        if (e.OriginalSource is FrameworkElement source)
        {
            var current = source as DependencyObject;
            while (current != null)
            {
                if (current is Button) return;
                if (current is FrameworkElement fe && fe.Name == "ShelfPanel") return;
                current = VisualTreeHelper.GetParent(current);
            }
        }

        App.IslandController.NotifyIslandClick();
        e.Handled = true;
    }

    // SetFullscreenSuppressed: actual hide/show is handled by WindowService.ForceAboveTaskbar
    // via AppWindow.Hide() / AppWindow.Show() so the acrylic surface is also removed.
    // This method is kept for future use (e.g. additional UI state on suppression).
    public void SetFullscreenSuppressed(bool suppress)
    {
    }

    // ── Hover tracking (for auto-collapse) ────────────────────────────────

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        App.IslandController.NotifyMouseEnter();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        App.IslandController.NotifyMouseLeave();
    }

    // ── Hover-state monitor ────────────────────────────────────────────────
    // The stable-window region mode's per-frame SetWindowRgn can leave XAML's
    // PointerExited undelivered (verified: _mouseIsOver stuck true, blocking
    // outside-click collapse and auto-collapse). PointerEntered still fires, so
    // this monitor supplies the missing LEAVE transition using the cursor
    // position — the same GetCursorPos mechanism CompactLayoutController
    // already uses. Fires NotifyMouseLeave exactly ONCE per leave transition.

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    // PASS 39 (GOAL 2): HWND-level drag hit-testing. WindowFromPoint answers
    // "who owns the pixel under the cursor" — the first check in the drag
    // decision tree. GetWindowFromPoint skips disabled/invisible windows;
    // ChildWindowFromPoint finds the deepest child for the target HWND.
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPoint(IntPtr hWnd, POINT pt);

    // PASS 40 (GOAL 3): REAL desktop pixel reads (GDI). RenderTargetBitmap only
    // reports what the XAML compositor thinks it rendered — this samples the
    // actual composited desktop frame the user's screen shows (GetPixel on the
    // screen DC), which is exactly what the black rectangle is made of.
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    // PASS 41 (GOAL 2/3): HWND owner attribution per sampled pixel + process
    // window enumeration. WindowFromPoint (declared above, PASS 39) gives the
    // exact owner of each black pixel; GetWindowThreadProcessId/GetClassName/
    // GetAncestor classify it; EnumWindows (here) lets the PIXEL_OWNER_MATCH
    // compare the rectangle against every sibling Halo HWND rect.
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT pvAttribute, int cbAttribute);

    // PASS 42 (GOAL 1/3): the deepest hit-test is needed to reveal the
    // Microsoft.UI.Content.DesktopChildSiteBridge child that hosts the XAML
    // compositor surface. ChildWindowFromPoint (declared above, PASS 39) returns
    // that child, so a P42-PIXEL sample attributes its pixels to the bridge
    // rather than to the parent.

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush(); // PASS 44: settle the desktop composite before sampling

    // PASS 48 (STEP 3): live native-region forensics. PtInRegion tests the ACTUAL
    // SetWindowRgn region (the source of truth for Win32 hit-testing) against the
    // cursor point; FindWindow/GetWindow walk the Z order to compare the Halo
    // against Shell_TrayWnd — if the taskbar is ABOVE the Halo, overlapping pill
    // pixels go to the taskbar even though the region covers them.
    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr hrgn, int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const int GWLP_HWNDPARENT = -8; // PASS 42 (STEP 2): owner window of a top-level HWND
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint GA_ROOT = 2;
    private const uint GW_HWNDPREV = 3;     // PASS 48: the window ABOVE the given window in the Z order
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private void OnHoverMonitorTick()
    {
        // PASS 33: the monitor is UNCONDITIONAL — hover is driven purely by the
        // cursor position against the current SetWindowRgn region, not by which
        // XAML pointer transitions happened to be delivered. Per-frame
        // SetWindowRgn updates can drop BOTH PointerEntered and PointerExited,
        // so neither direction can be trusted as the source of truth:
        //   - cursor inside the region and not tracked → synthesize ENTER
        //     (disarms auto-collapse; keeps the dashboard/popup open while
        //     hovered — also restores the Clipboard 400 ms hover-expand, which
        //     depends on PointerEntered firing);
        //   - cursor outside the region while tracked → synthesize LEAVE (arms
        //     the short auto-collapse).
        // The fixed 1000×890 envelope can never count as hover space because the
        // test is the REGION rect, not the window rect.
        if (!_regValid) return; // region is the source of truth; nothing to test before it exists
        if (!GetCursorPos(out POINT pt)) return;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetWindowRect(hwnd, out RECT r)) return;
        // Region rect in screen coords = CLIENT origin (screen coords) +
        // region client rect. PASS 36: ClientToScreen on (0,0) gives the true
        // client origin — robust against any non-client frame offset (a
        // presenter frame or title bar would shift the client area, breaking a
        // window-rect-origin test even though the region itself is exact).
        POINT origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) { origin.X = r.Left; origin.Y = r.Top; }
        bool inside = PointInRegion(pt, origin);
        if (inside && !_pointerOver)
        {
            _pointerOver = true;
            Helpers.Logger.Info($"[HOVER] enter cursor=({pt.X},{pt.Y}) client=({origin.X},{origin.Y}) " +
                                $"reg=({_regX0},{_regY0},{_regW}x{_regH})");
            App.IslandController.NotifyMouseEnter();
            // PASS 35: hover is the region monitor's job, not fragile XAML
            // pointer delivery — feed the active widget (e.g. the Clipboard
            // hover-expand preview) on the same enter/leave transitions.
            App.IslandController.NotifyWidgetHover(true);
        }
        else if (!inside && _pointerOver)
        {
            _pointerOver = false;
            Helpers.Logger.Info($"[HOVER] leave cursor=({pt.X},{pt.Y}) client=({origin.X},{origin.Y}) " +
                                $"reg=({_regX0},{_regY0},{_regW}x{_regH})");
            App.IslandController.NotifyMouseLeave();
            App.IslandController.NotifyWidgetHover(false);
        }
    }

    /// <summary>
    /// True when the cursor is inside the current SetWindowRgn region (screen
    /// coords). The region is the hover authority — the fixed HWND envelope
    /// can never count as hover space.
    /// </summary>
    private bool PointInRegion(POINT pt, POINT origin)
    {
        return pt.X >= origin.X + _regX0 && pt.X <= origin.X + _regX0 + _regW
            && pt.Y >= origin.Y + _regY0 && pt.Y <= origin.Y + _regY0 + _regH;
    }

    /// <summary>
    /// PASS 47 (GOAL 1): true when the screen-space point lies inside the
    /// CURRENT SetWindowRgn visible region — the authoritative interactive
    /// shape. The fixed 1000x890 HWND envelope is never the dashboard's
    /// interactive area: a press inside the envelope but outside the region
    /// (the taskbar strip below the expanded dashboard, or the transparent
    /// envelope margins) is an outside-click and must collapse the island.
    /// Called on the UI thread from WindowService.MouseHookProc. Uses ints so
    /// the private nested POINT never leaks across an API surface.
    /// </summary>
    public bool IsPointInCurrentRegion(int screenX, int screenY)
    {
        if (!_regValid) return false;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        POINT origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return false;
        return PointInRegion(new POINT { X = screenX, Y = screenY }, origin);
    }

    /// <summary>
    /// PASS 53: live region rect (client px) + settle state, as applied by the
    /// last ApplyStableRegion. WindowService mirrors this onto the invisible
    /// drop-target overlay so it always covers exactly the interactive area the
    /// SetWindowRgn region defines.
    /// </summary>
    public (int X, int Y, int W, int H, bool Collapsed) GetRegionState()
        => (_regX0, _regY0, _regW, _regH, _settledCollapsed);

    /// <summary>
    /// Compact-pill screen rect (screen px) for OLE drag-hit forensics. Mirrors
    /// ApplyStableRegion's collapsed branch: x spans 0..pillW, y spans
    /// dashboardHeightDip..+stripHeightDip in client px scaled by DPI.
    /// </summary>
    public (int X, int Y, int W, int H)? GetPillScreenRect()
    {
        if (!_regValid) return null;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        POINT origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return null;
        double s = GetDpiForWindow(hwnd) / 96.0;
        int px = (int)Math.Round(_compactPillW * s);
        int py0 = (int)Math.Round(_dashboardHeightDip * s);
        int py1 = (int)Math.Round((_dashboardHeightDip + _stripHeightDip) * s);
        return (origin.X, origin.Y + py0, px, py1 - py0);
    }

    /// <summary>
    /// PASS 48 (STEP 3): live native-region vs VISIBLE pill forensics for drag
    /// hit-testing, emitted by the [DRAG-ROUTE] tick (HALO_DRAG_REGION=1).
    ///
    /// PASS 48's core question is whether the LIVE SetWindowRgn region actually
    /// covers the VISIBLE XAML pill. Both geometries are reported independently:
    ///   - regionType/regionBox — the ACTUAL region Windows has applied
    ///     (GetWindowRgn/GetRgnBox, window coords), compared against the assumed
    ///     bookkeeping (regionMatchesAssumed) so a stale/missing/offset region is
    ///     detectable;
    ///   - pillRectVisible — the RENDERED pill rect (PillBorder.TransformToVisual,
    ///     which includes the pill's RenderTransform), NEVER assumed to equal the
    ///     region;
    ///   - pillRectAssumed — the region-derived pill rect (GetPillScreenRect);
    ///   - haloBelowTaskbar — Z-order vs Shell_TrayWnd. The pill overlaps the
    ///     taskbar strip (y≈1020-1080), so if the taskbar is ABOVE the Halo in the
    ///     Z order it wins every overlapping pixel even when the region covers it.
    /// The live PtInRegion uses WINDOW-relative coords (a region is always window-
    /// relative), unlike the assumed rects which are screen-space.
    /// </summary>
    public string DescribeRegionHitTest(int screenX, int screenY)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetWindowRect(hwnd, out RECT wr)) return "windowRect=n/a";
        POINT origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) { origin.X = wr.Left; origin.Y = wr.Top; }
        double s = GetDpiForWindow(hwnd) / 96.0;
        var pt = new POINT { X = screenX, Y = screenY };

        // LIVE native region — the actual SetWindowRgn state, not bookkeeping.
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        int rgnType = GetWindowRgn(hwnd, rgn); // 0=ERROR 1=NULL 2=SIMPLE 3=COMPLEX
        string regionType = rgnType switch { 1 => "NULLREGION", 2 => "SIMPLEREGION", 3 => "COMPLEXREGION", _ => "ERROR" };
        string regionBox = GetRgnBox(rgn, out RECT rb) ? $"({rb.Left},{rb.Top},{rb.Right - rb.Left}x{rb.Bottom - rb.Top})" : "n/a";
        bool regionMatchesAssumed = _regValid && rgnType > 0
            && rb.Left == _regX0 && rb.Top == _regY0
            && (rb.Right - rb.Left) == _regW && (rb.Bottom - rb.Top) == _regH;
        // Regions are window-relative: test the point minus the window origin.
        bool pointInRegion = rgnType > 0 && PtInRegion(rgn, screenX - wr.Left, screenY - wr.Top);
        DeleteObject(rgn);

        string regionRect = _regValid ? $"({origin.X + _regX0},{origin.Y + _regY0},{_regW}x{_regH})" : "n/a";
        bool pointInAssumedRegion = _regValid && PointInRegion(pt, origin);

        // VISIBLE XAML pill rect — the RENDERED pill (RenderTransform included).
        string pillVisible = "n/a";
        bool pointInVisiblePill = false;
        if (PillBorder != null)
        {
            var t = PillBorder.TransformToVisual(null);
            if (t != null)
            {
                var tl = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                double vx0 = tl.X * s, vy0 = tl.Y * s;
                double vx1 = (tl.X + PillBorder.ActualWidth) * s;
                double vy1 = (tl.Y + PillBorder.ActualHeight) * s;
                pillVisible = $"({origin.X + (int)Math.Round(vx0)},{origin.Y + (int)Math.Round(vy0)},{(int)Math.Round(vx1 - vx0)}x{(int)Math.Round(vy1 - vy0)})";
                pointInVisiblePill = screenX >= origin.X + vx0 && screenX <= origin.X + vx1
                    && screenY >= origin.Y + vy0 && screenY <= origin.Y + vy1;
            }
        }

        string pillAssumed = "n/a";
        bool pointInAssumedPill = false;
        var p = GetPillScreenRect();
        if (p is (int pillX, int pillY, int pillW, int pillH))
        {
            pillAssumed = $"({pillX},{pillY},{pillW}x{pillH})";
            pointInAssumedPill = screenX >= pillX && screenX <= pillX + pillW
                && screenY >= pillY && screenY <= pillY + pillH;
        }

        string dashRect = $"({origin.X},{origin.Y},{(int)Math.Round(_windowWidthDip * s)}x{(int)Math.Round(_dashboardHeightDip * s)})";

        return "[DRAG-REGION] " +
               $"hwnd=0x{hwnd.ToInt64():X} windowRect=({wr.Left},{wr.Top},{wr.Right - wr.Left}x{wr.Bottom - wr.Top}) " +
               $"regionType={regionType} regionBox={regionBox} regionMatchesAssumed={regionMatchesAssumed} " +
               $"regionRect={regionRect} pillRectVisible={pillVisible} pillRectAssumed={pillAssumed} " +
               $"dashboardRect={dashRect} point=({screenX},{screenY}) " +
               $"pointInRegion={pointInRegion} pointInAssumedRegion={pointInAssumedRegion} " +
               $"pointInVisiblePill={pointInVisiblePill} pointInAssumedPill={pointInAssumedPill} " +
               DescribeHaloZOrder(hwnd);
    }

    /// <summary>
    /// PASS 48 (STEP 3): Z-order comparison between the Halo HWND and
    /// Shell_TrayWnd. The compact pill overlaps the taskbar strip, so whichever
    /// topmost window is HIGHER in the Z order wins the pixels (and therefore the
    /// OLE hit-test) at the overlap. GW_HWNDPREV walks upward through the Z order;
    /// reaching the top without crossing Shell_TrayWnd proves the Halo is above it.
    /// </summary>
    private string DescribeHaloZOrder(IntPtr hwnd)
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return "taskbar=n/a";
        IntPtr cur = hwnd;
        while ((cur = GetWindow(cur, GW_HWNDPREV)) != IntPtr.Zero)
            if (cur == tray) return "haloBelowTaskbar=true";
        return "haloBelowTaskbar=false";
    }

    /// <summary>
    /// PASS 39 (GOAL 1): the surface materials currently armed — embedded in the
    /// [P39-SURFACE] log so the backdrop/brush state is attributable alongside
    /// the Win32 dump. "backdrop" reflects the window-level system backdrop
    /// (DesktopAcrylicController), the rest are the XAML root/child brushes.
    /// </summary>
    public string DescribeSurfaceContext()
    {
        string backdrop = _acrylicController != null ? "window-level(armed)" : "none";
        string pillBg = PillBorder?.Background?.GetType().Name ?? "null";
        string dashBg = DashboardBorder?.Background?.GetType().Name ?? "null";
        string rootBg = RootGrid?.Background?.GetType().Name ?? "null";
        return $"backdrop={backdrop} pillBg={pillBg} dashBg={dashBg} rootBg={rootBg}";
    }

    /// <summary>
    /// PASS 39 (GOAL 2): HWND hit-test forensics for drag attribution. Returns a
    /// single log-ready line (embedded in the [P39-DRAG] events):
    ///   - cursor (screen px) and every WindowFromPoint-family owner of that
    ///     pixel: windowFromPoint (topmost), getWindowFromPoint (skips disabled/
    ///     invisible), childWindowFromPoint (deepest child of the halo HWND);
    ///   - whether the halo HWND itself is under the cursor (vs the desktop or
    ///     another window), the live region screen rect, and whether the cursor
    ///     is inside the region (hover authority) and inside the compact-pill
    ///     screen rect (the drop-target surface).
    /// Decision tree (from the task): cursor pixel not owned by haloHwnd → HWND
    /// hit-testing problem; owned but no DragEnter → OLE registration; DragEnter
    /// but StorageItems false → data-format handling.
    /// </summary>
    public string DescribeDragHitTest()
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetCursorPos(out POINT pt)) return "cursor=n/a";
        if (!GetWindowRect(hwnd, out RECT r)) return "cursor=n/a";
        POINT origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) { origin.X = r.Left; origin.Y = r.Top; }

        IntPtr wfp = WindowFromPoint(pt);
        IntPtr gfwp = GetWindowFromPoint(pt);
        IntPtr child = ChildWindowFromPoint(hwnd, new POINT { X = pt.X - origin.X, Y = pt.Y - origin.Y });

        bool insideHalo = _regValid && PointInRegion(pt, origin);

        // Compact-pill screen rect (home strip slot): x spans 0..pillW,
        // y spans dashboardHeightDip..+stripHeightDip, in client px scaled by
        // DPI — mirrors ApplyStableRegion's collapsed branch.
        double s = GetDpiForWindow(hwnd) / 96.0;
        int px = (int)Math.Round(_compactPillW * s);
        int py0 = (int)Math.Round(_dashboardHeightDip * s);
        int py1 = (int)Math.Round((_dashboardHeightDip + _stripHeightDip) * s);
        bool insidePill = pt.X >= origin.X && pt.X <= origin.X + px
            && pt.Y >= origin.Y + py0 && pt.Y <= origin.Y + py1;

        string regionRect = _regValid
            ? $"({origin.X + _regX0},{origin.Y + _regY0},{_regW}x{_regH})"
            : "n/a";

        return $"cursor=({pt.X},{pt.Y}) hwndUnderCursor=0x{wfp.ToInt64():X} " +
               $"windowFromPoint=0x{wfp.ToInt64():X} getWindowFromPoint=0x{gfwp.ToInt64():X} " +
               $"childWindowFromPoint=0x{child.ToInt64():X} haloHwnd=0x{hwnd.ToInt64():X} " +
               $"underCursorIsHalo={wfp == hwnd} " +
               $"insideHaloRegion={insideHalo} insidePillRegion={insidePill} " +
               $"haloRect=({r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}) " +
               $"regionRect={regionRect} pillRect=({origin.X},{origin.Y + py0},{px}x{py1 - py0})";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PASS 40 — black-rectangle SOURCE forensics (diagnostic only, env-gated)
    // ═══════════════════════════════════════════════════════════════════════

    private string _lastP40State = "";

    /// <summary>
    /// PASS 40: the two extreme binary tests + audit + pixel scan are armed from
    /// App after the window content exists. Env-gated, all OFF by default.
    /// </summary>
    public void ArmP40Modes()
    {
        if (Helpers.MotionDiagnostics.EnableP40NoWindowContent)
        {
            // Replace Window.Content with a transparent minimal Grid. HWND
            // geometry + region + hover machinery stay alive (they read hwnd /
            // region fields, not the content tree), so the ONLY thing removed is
            // the XAML visual tree. If the rectangle persists ⇒ the source is
            // the window/compositor/non-client/another HWND, NOT the content.
            // (PASS 43: HALO_P42_EMPTY_CONTENT is NOT swapped here — the
            // binary-test sequence swaps it at runtime AFTER sampling the
            // baseline, then restores it.)
            try
            {
                Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                Helpers.Logger.Info("[P40-NOCONTENT] Window.Content replaced with a transparent minimal Grid — geometry + region machinery alive.");
            }
            catch (Exception ex)
            {
                Helpers.Logger.Error("[P40-NOCONTENT] content swap failed", ex);
            }
        }

        if (Helpers.MotionDiagnostics.EnableP40NukeXaml)
        {
            // Nuke on first Loaded: the visual tree is fully connected by then,
            // and it runs before the first rendered frame shows to the user.
            void OnLoaded(object? sender, RoutedEventArgs e)
            {
                RootGrid.Loaded -= OnLoaded;
                NukeXaml();
            }
            RootGrid.Loaded += OnLoaded;
        }
    }

    /// <summary>
    /// PASS 40 (GOAL 1): [P40-XAML] live visual-tree surface audit — the actual
    /// element/bounds/brush/border/shadow/visibility state of every surface-
    /// bearing node (named, or carrying a background/border/shadow). Proves what
    /// the XAML tree is REALLY painting (nothing is assumed from XAML source).
    /// </summary>
    public void XamlSurfaceAudit()
    {
        try
        {
            if (RootGrid == null) { Helpers.Logger.Info("[P40-XAML] root n/a"); return; }
            int logged = 0;
            void Visit(DependencyObject node, int depth)
            {
                if (node is FrameworkElement fe)
                {
                    string bg = DescribeBrush(P40Background(fe));
                    string border = DescribeBrush(P40BorderBrush(fe));
                    bool interesting = !string.IsNullOrEmpty(fe.Name)
                        || P40Background(fe) != null || P40BorderBrush(fe) != null || fe.Shadow != null;
                    if (interesting)
                    {
                        var pos = fe.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                        logged++;
                        Helpers.Logger.Info(
                            $"[P40-XAML] {new string(' ', depth * 2)}type={fe.GetType().Name} name=\"{fe.Name}\" " +
                            $"pos=({pos.X:F0},{pos.Y:F0}) size={fe.ActualWidth:F0}x{fe.ActualHeight:F0} " +
                            $"opacity={fe.Opacity:F2} vis={fe.Visibility} bg={bg} border={border} " +
                            $"thick={ThicknessStr(P40BorderThickness(fe))} corner={CornerRadiusStr(P40CornerRadius(fe))} " +
                            $"shadow={(fe.Shadow != null ? fe.Shadow.GetType().Name : "null")}");
                    }
                    if (depth >= 4) return;
                    int n = VisualTreeHelper.GetChildrenCount(fe);
                    for (int i = 0; i < n; i++) Visit(VisualTreeHelper.GetChild(fe, i), depth + 1);
                }
            }
            Visit(RootGrid, 0);
            Helpers.Logger.Info($"[P40-XAML] audit complete (logged {logged} surface-bearing nodes)");
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P40-XAML] audit failed", ex);
        }
    }

    /// <summary>
    /// PASS 40 (GOAL 6): [P40-NUKE] make EVERY XAML surface completely
    /// transparent — backgrounds, borders, thickness, corner radius, shadows —
    /// with no backdrop/acrylic/effects (the backdrop is already disabled by the
    /// SetAcrylicBackdrop gate). THE binary test: rectangle present ⇒ source is
    /// outside the XAML tree; absent ⇒ inside it.
    /// </summary>
    public void NukeXaml()
    {
        try
        {
            if (RootGrid == null) return;
            int mutated = 0;
            void Visit(DependencyObject node)
            {
                if (node is UIElement ue) ue.Shadow = null;
                switch (node)
                {
                    case Border b:
                        b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        b.BorderThickness = new Thickness(0);
                        b.CornerRadius = new CornerRadius(0);
                        b.Opacity = 1;
                        mutated++;
                        break;
                    case Panel p:
                        p.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        p.Opacity = 1;
                        mutated++;
                        break;
                    case ContentControl cc:
                        cc.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        cc.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        cc.BorderThickness = new Thickness(0);
                        cc.CornerRadius = new CornerRadius(0);
                        cc.Opacity = 1;
                        mutated++;
                        break;
                    case Control c:
                        c.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        c.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        c.BorderThickness = new Thickness(0);
                        c.CornerRadius = new CornerRadius(0);
                        c.Opacity = 1;
                        mutated++;
                        break;
                    case ContentPresenter cp:
                        cp.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        cp.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                        cp.BorderThickness = new Thickness(0);
                        cp.CornerRadius = new CornerRadius(0);
                        cp.Opacity = 1;
                        mutated++;
                        break;
                }
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++) Visit(VisualTreeHelper.GetChild(node, i));
            }
            Visit(RootGrid);
            Helpers.Logger.Info($"[P40-NUKE] every XAML surface made transparent ({mutated} elements mutated) — if the black rectangle persists it is NOT XAML content.");
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P40-NUKE] failed", ex);
        }
    }

    /// <summary>
    /// PASS 40 (GOAL 3): [P40-SCAN] — real desktop pixel forensics. GDI GetPixel
    /// on the screen DC samples the ACTUAL composited frame the user sees (never
    /// RenderTargetBitmap). Six scanlines cross the pill and the fixed envelope;
    /// every dark pixel is classified by which surface owns it (pill / region /
    /// window / desktop), compared against a wallpaper reference taken far from
    /// the window, and the anomalous dark pixels' bounding box is matched against
    /// windowRect / clientRect / region / extended-frame / pill — the exact box
    /// that paints the rectangle. Re-runs automatically on compact→expanded→popup
    /// state changes via RunP40ScanIfChanged.
    /// </summary>
    public void RunP40PixelScan(string stateTag)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!GetWindowRect(hwnd, out RECT wr)) return;
            if (!GetClientRect(hwnd, out RECT cr)) cr = default;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) { origin.X = wr.Left; origin.Y = wr.Top; }

            double s = GetDpiForWindow(hwnd) / 96.0;
            int pillW = (int)Math.Round(_compactPillW * s);
            int pillY0 = (int)Math.Round(_dashboardHeightDip * s);
            int pillY1 = (int)Math.Round((_dashboardHeightDip + _stripHeightDip) * s);
            int pillX0 = origin.X;
            int pillX1 = origin.X + pillW;
            int pillCY = origin.Y + (pillY0 + pillY1) / 2;
            int pillCX = origin.X + pillW / 2;

            int regX0 = _regValid ? origin.X + _regX0 : -1;
            int regY0 = _regValid ? origin.Y + _regY0 : -1;
            int regX1 = _regValid ? regX0 + _regW : -1;
            int regY1 = _regValid ? regY0 + _regH : -1;

            // Wallpaper reference: far to the left of the window, at pill height.
            int wRefX = wr.Left - 400;
            uint wb = PixelAt(wRefX, pillCY);
            int wMax = Math.Max(R(wb), Math.Max(G(wb), B(wb)));
            Helpers.Logger.Info($"[P40-SCAN] state={stateTag} wallpaperRef=({wRefX},{pillCY}) rgb=({R(wb)},{G(wb)},{B(wb)})");

            int anomX0 = int.MaxValue, anomY0 = int.MaxValue, anomX1 = int.MinValue, anomY1 = int.MinValue, anomCount = 0;

            void ScanLine(bool horizontal, int fixedCoord, int from, int to, string label)
            {
                int runStart = -1, runEnd = -1, runCount = 0, runSum = 0;
                string runClass = "";
                for (int v = from; v <= to; v += 4)
                {
                    int x = horizontal ? v : fixedCoord;
                    int y = horizontal ? fixedCoord : v;
                    uint c = PixelAt(x, y);
                    int mx = Math.Max(R(c), Math.Max(G(c), B(c)));
                    bool dark = mx < 70;
                    if (dark)
                    {
                        bool belowWall = wb != 0xFFFFFFFF && mx <= wMax - 12;
                        string cls = Classify(x, y, wr, cr, origin, regX0, regY0, regX1, regY1, pillX0, pillX1, pillY0, pillY1);
                        if (runStart < 0)
                        {
                            runStart = v; runClass = cls; runCount = 1; runSum = mx;
                            runEnd = v;
                        }
                        else if (cls == runClass)
                        {
                            runEnd = v; runCount++; runSum += mx;
                        }
                        else
                        {
                            FlushRun(label, horizontal, runStart, runEnd, runCount, runSum / runCount, runClass, belowWall);
                            runStart = v; runEnd = v; runCount = 1; runSum = mx; runClass = cls;
                        }
                        if (cls is "windowOutsideRegion" or "outsideWindow")
                        {
                            anomX0 = Math.Min(anomX0, x); anomX1 = Math.Max(anomX1, x);
                            anomY0 = Math.Min(anomY0, y); anomY1 = Math.Max(anomY1, y);
                            anomCount++;
                        }
                    }
                    else if (runStart >= 0)
                    {
                        FlushRun(label, horizontal, runStart, runEnd, runCount, runSum / runCount, runClass, false);
                        runStart = -1;
                    }
                }
                if (runStart >= 0) FlushRun(label, horizontal, runStart, runEnd, runCount, runSum / runCount, runClass, false);
            }

            void FlushRun(string label, bool horizontal, int a0, int a1, int count, int avgMax, string cls, bool belowWall)
            {
                string extent = horizontal ? $"x={a0}..{a1}" : $"y={a0}..{a1}";
                Helpers.Logger.Info($"[P40-SCAN] line={label} class={cls} darkRun {extent} n={count} avgMax={avgMax} belowWallpaper={belowWall}");
            }

            ScanLine(true, pillCY, wr.Left - 24, wr.Right + 24, "horizPillCenter");
            ScanLine(true, origin.Y + pillY0 - 10, wr.Left - 24, wr.Right + 24, "horizJustAbovePill");
            ScanLine(true, origin.Y + pillY1 + 10, wr.Left - 24, wr.Right + 24, "horizJustBelowPill");
            ScanLine(false, pillCX, wr.Top - 24, wr.Bottom + 24, "vertPillCenterX");
            ScanLine(false, pillX0 - 10, wr.Top - 24, wr.Bottom + 24, "vertJustLeftOfPill");
            ScanLine(false, pillX1 + 10, wr.Top - 24, wr.Bottom + 24, "vertJustRightOfPill");

            // Attribution at the exact spot where a just-outside-pill dark run
            // exists (midpoints of the four just-outside pill scanlines).
            App.WindowService.LogP40Hit(pillX0 - 10, pillCY, "justLeftOfPill");
            App.WindowService.LogP40Hit(pillX1 + 10, pillCY, "justRightOfPill");
            App.WindowService.LogP40Hit(pillCX, origin.Y + pillY0 - 10, "justAbovePill");
            App.WindowService.LogP40Hit(pillCX, origin.Y + pillY1 + 10, "justBelowPill");

            // Anomalous-dark-pixel bounding box vs every candidate rect.
            if (anomCount > 0)
            {
                GetWindowRect(hwnd, out RECT wr2);
                App.WindowService.LogP39Surface("p40-scan", DescribeSurfaceContext());
                string matches =
                    $"windowRect={(anomX0 >= wr2.Left - 4 && anomX1 <= wr2.Right + 4 && anomY0 >= wr2.Top - 4 && anomY1 <= wr2.Bottom + 4)} " +
                    $"clientRect={(cr.Left != 0 || cr.Top != 0) && (anomX0 >= origin.X - 4 && anomX1 <= origin.X + (cr.Right - cr.Left) + 4 && anomY0 >= origin.Y - 4 && anomY1 <= origin.Y + (cr.Bottom - cr.Top) + 4)} " +
                    $"region={_regValid && anomX0 >= regX0 - 4 && anomX1 <= regX1 + 4 && anomY0 >= regY0 - 4 && anomY1 <= regY1 + 4} " +
                    $"pill={(anomX0 >= pillX0 - 4 && anomX1 <= pillX1 + 4 && anomY0 >= origin.Y + pillY0 - 4 && anomY1 <= origin.Y + pillY1 + 4)}";
                Helpers.Logger.Info($"[P40-BOX] state={stateTag} anomalousDark count={anomCount} bbox=({anomX0},{anomY0})-({anomX1},{anomY1}) " +
                                    $"size=({anomX1 - anomX0}x{anomY1 - anomY0}) matches: {matches}");
            }
            else
            {
                Helpers.Logger.Info($"[P40-BOX] state={stateTag} NO anomalous dark pixels outside pill/region — no rectangle-class pixels found on these scanlines.");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P40-SCAN] failed", ex);
        }
    }

    /// <summary>Runs the census once + the pixel scan whenever the island's
    /// compact/expanded/popup state changes.</summary>
    public void RunP40ScanIfChanged()
    {
        string state = P40StateString();
        if (state == _lastP40State) return;
        _lastP40State = state;
        App.WindowService.LogP40WindowCensus();
        if (Helpers.MotionDiagnostics.EnableP42OwnerForensics)
            RunP42OwnerScan(state);   // PASS 42 = corrected coordinate system (in-bounds only)
        else if (Helpers.MotionDiagnostics.EnableP41OwnerForensics)
            RunP41OwnerScan(state);
        else
            RunP40PixelScan(state);
    }

    // ── PASS 41 — PIXEL-OWNER forensics ───────────────────────────────────
    // The black rectangle's pixels are sampled directly (GDI GetPixel on the
    // real desktop composite) at every significant point, and EACH pixel is
    // attributed to the exact HWND that owns it. The answer is empirical:
    // PIXEL_OWNER_MATCH names the candidate rect that contains the rectangle's
    // anomalous-dark bounding box, and the dominant owner class names the
    // producer ("haloMain" / "haloOther" / "haloHelperWinUI" / "otherApp" /
    // "dwm" / "desktop").

    /// <summary>
    /// PASS 41 (GOAL 1-4): full pixel-owner scan. Detect the anomalous-dark
    /// bbox (six scanlines), sample the required named points (rectangle
    /// center / each edge midpoint / just-inside / just-outside / the four
    /// envelope corners / far outside), attribute every pixel to its HWND
    /// owner, then emit PIXEL_OWNER_MATCH + the CONCLUSION naming the producer.
    /// </summary>
    public void RunP41OwnerScan(string stateTag)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!GetWindowRect(hwnd, out RECT wr)) return;
            if (!GetClientRect(hwnd, out RECT cr)) cr = default;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) { origin.X = wr.Left; origin.Y = wr.Top; }

            double s = GetDpiForWindow(hwnd) / 96.0;
            int pillW = (int)Math.Round(_compactPillW * s);
            int pillY0 = (int)Math.Round(_dashboardHeightDip * s);
            int pillY1 = (int)Math.Round((_dashboardHeightDip + _stripHeightDip) * s);
            int pillX0 = origin.X, pillX1 = origin.X + pillW;
            int pillCY = origin.Y + (pillY0 + pillY1) / 2;
            int pillCX = origin.X + pillW / 2;

            int regX0 = _regValid ? origin.X + _regX0 : -1;
            int regY0 = _regValid ? origin.Y + _regY0 : -1;
            int regX1 = _regValid ? regX0 + _regW : -1;
            int regY1 = _regValid ? regY0 + _regH : -1;

            // Detect the anomalous-dark bounding box (window pixels that are
            // dark but NOT pill/region — i.e., the rectangle's own pixels).
            int bX0 = int.MaxValue, bY0 = int.MaxValue, bX1 = int.MinValue, bY1 = int.MinValue, bCount = 0;
            void ScanLine(bool horizontal, int fixedCoord, int from, int to)
            {
                for (int v = from; v <= to; v += 4)
                {
                    int x = horizontal ? v : fixedCoord;
                    int y = horizontal ? fixedCoord : v;
                    uint c = PixelAt(x, y);
                    int mx = Math.Max(R(c), Math.Max(G(c), B(c)));
                    if (mx < 70)
                    {
                        string cls = Classify(x, y, wr, cr, origin, regX0, regY0, regX1, regY1, pillX0, pillX1, pillY0, pillY1);
                        if (cls is "windowOutsideRegion" or "outsideWindow")
                        {
                            bX0 = Math.Min(bX0, x); bX1 = Math.Max(bX1, x);
                            bY0 = Math.Min(bY0, y); bY1 = Math.Max(bY1, y);
                            bCount++;
                        }
                    }
                }
            }
            ScanLine(true, pillCY, wr.Left - 24, wr.Right + 24);
            ScanLine(true, origin.Y + pillY0 - 10, wr.Left - 24, wr.Right + 24);
            ScanLine(true, origin.Y + pillY1 + 10, wr.Left - 24, wr.Right + 24);
            ScanLine(false, pillCX, wr.Top - 24, wr.Bottom + 24);
            ScanLine(false, pillX0 - 10, wr.Top - 24, wr.Bottom + 24);
            ScanLine(false, pillX1 + 10, wr.Top - 24, wr.Bottom + 24);

            int rectCX = bCount > 0 ? (bX0 + bX1) / 2 : pillCX;
            int rectCY = bCount > 0 ? (bY0 + bY1) / 2 : pillCY;

            // Named sample points — the task's required set.
            var samples = new List<(string tag, int x, int y)>
            {
                ("farOutside", wr.Left - 400, pillCY),
                ("envelopeCornerTL", wr.Left + 4, wr.Top + 4),
                ("envelopeCornerTR", wr.Right - 4, wr.Top + 4),
                ("envelopeCornerBL", wr.Left + 4, wr.Bottom - 4),
                ("envelopeCornerBR", wr.Right - 4, wr.Bottom - 4),
            };
            if (bCount > 0)
            {
                samples.Add(("rectCenter", rectCX, rectCY));
                samples.Add(("rectEdgeCenterTop", rectCX, bY0 + 2));
                samples.Add(("rectEdgeCenterBottom", rectCX, bY1 - 2));
                samples.Add(("rectEdgeCenterLeft", bX0 + 2, rectCY));
                samples.Add(("rectEdgeCenterRight", bX1 - 2, rectCY));
                samples.Add(("justOutsideTop", rectCX, bY0 - 8));
                samples.Add(("justOutsideBottom", rectCX, bY1 + 8));
                samples.Add(("justOutsideLeft", bX0 - 8, rectCY));
                samples.Add(("justOutsideRight", bX1 + 8, rectCY));
                samples.Add(("justInsideTop", rectCX, bY0 + 8));
                samples.Add(("justInsideBottom", rectCX, bY1 - 8));
                samples.Add(("justInsideLeft", bX0 + 8, rectCY));
                samples.Add(("justInsideRight", bX1 - 8, rectCY));
            }
            samples.Add(("pillCenter", pillCX, pillCY));

            // Attribute every sample + count owner classes.
            var ownerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (tag, x, y) in samples)
            {
                uint c = PixelAt(x, y);
                string owner = P40OwnerClass(x, y, hwnd, out IntPtr h, out uint pid, out string clsName);
                ownerCounts.TryGetValue(owner, out int n);
                ownerCounts[owner] = n + 1;
                IntPtr root = h != IntPtr.Zero ? GetAncestor(h, GA_ROOT) : IntPtr.Zero;
                Helpers.Logger.Info($"[P41-SAMPLE] tag={tag} state={stateTag} coord=({x},{y}) rgb=({R(c)},{G(c)},{B(c)}) " +
                                    $"owner={owner} hwnd=0x{h.ToInt64():X} root=0x{root.ToInt64():X} class={clsName} pid={pid}");
            }

            string dominant = "none";
            int top = -1;
            foreach (var kv in ownerCounts)
                if (kv.Value > top) { top = kv.Value; dominant = kv.Key; }

            // PIXEL_OWNER_MATCH: which candidate rect contains the rectangle.
            string match = "none";
            if (bCount > 0)
            {
                if (ContainsRgn(wr.Left, wr.Top, wr.Right, wr.Bottom, bX0, bY0, bX1, bY1)) match = "mainWindowRect";
                else if (cr.Left != 0 || cr.Top != 0)
                    if (ContainsRgn(origin.X, origin.Y, origin.X + (cr.Right - cr.Left), origin.Y + (cr.Bottom - cr.Top), bX0, bY0, bX1, bY1)) match = "clientRect";
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT ext, Marshal.SizeOf<RECT>()) == 0)
                    if (ContainsRgn(ext.Left, ext.Top, ext.Right, ext.Bottom, bX0, bY0, bX1, bY1)) match = "extendedFrame";
                if (match == "none" && _regValid && ContainsRgn(regX0, regY0, regX1, regY1, bX0, bY0, bX1, bY1)) match = "region";
                if (match == "none" && ContainsRgn(pillX0, origin.Y + pillY0, pillX1, origin.Y + pillY1, bX0, bY0, bX1, bY1)) match = "pill";
                uint myPid = (uint)Environment.ProcessId;
                EnumWindows((h, l) =>
                {
                    GetWindowThreadProcessId(h, out uint wpid);
                    if (wpid != myPid || h == hwnd) return true;
                    if (GetWindowRect(h, out RECT r) && ContainsRgn(r.Left, r.Top, r.Right, r.Bottom, bX0, bY0, bX1, bY1))
                        match = $"haloHwnd0x{h.ToInt64():X}";
                    return true;
                }, IntPtr.Zero);
                App.WindowService.LogP41WindowCensus(bX0, bY0, bX1, bY1);
            }

            var ownerParts = new StringBuilder();
            foreach (var kv in ownerCounts)
            {
                if (ownerParts.Length > 0) ownerParts.Append(',');
                ownerParts.Append($"{kv.Key}:{kv.Value}");
            }
            Helpers.Logger.Info($"[P41-OWNER] state={stateTag} anomalousDark={bCount} " +
                                $"bbox=({bX0},{bY0})-({bX1},{bY1}) PIXEL_OWNER_MATCH={match} " +
                                $"dominantSampleOwner={dominant} owners=[{ownerParts}]");
            Helpers.Logger.Info($"[P41-OWNER] CONCLUSION=the black rectangle pixels are produced by owner '{dominant}' (rectangle matches {match}) — {ExplanatoryFor(dominant, match)}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P41-SCAN] failed", ex);
        }
    }

    // ── PASS 42 — corrected pixel-owner forensics (diagnostic only) ────────
    // PASS 41's samples were taken OUTSIDE the Halo HWND (its scanlines ran
    // from wr.Left-24 to wr.Right+24 and wr.Top-24 to wr.Bottom+24), so its
    // "dominantSampleOwner" was Chrome/taskbar pixels, not Halo's own. Pass 42
    // fixes the coordinate system: EVERY sample is strictly inside
    // GetWindowRect(haloHwnd); any sample outside that rectangle is marked
    // INVALID and excluded from owner classification. The black rectangle is
    // located by scanning ONLY the Halo HWND bounds, and every candidate pixel
    // is attributed to ownerHwnd/childHwnd/rootHwnd/class, with the wallpaper
    // reference captured immediately outside the window used so a merely-dark
    // wallpaper behind a transparent envelope is never misclassified as the
    // rectangle (STEP 2's "do NOT classify merely because it is dark").

    /// <summary>
    /// PASS 42 (GOAL 1/2/3): corrected in-bounds pixel-owner scan.
    /// STEP 1 — every sample inside GetWindowRect(haloHwnd); outside ⇒ INVALID.
    /// STEP 2 — auto-locate the black rectangle by scanning only the Halo HWND
    /// bounds (coarse full-envelope grid + fine near-edge lines); dark pixels
    /// OUTSIDE the visible shape (region) and DARKER than the wallpaper ref are
    /// the rectangle's pixels; per-pixel P42-PIXEL records owner attribution.
    /// STEP 3 — full HWND enumeration with rect-vs-bbox intersection check via
    /// LogP41WindowCensus (children + owned + helpers included).
    /// </summary>
    public void RunP42OwnerScan(string stateTag)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetWindowRect(hwnd, out RECT wr)) return;
        if (!GetClientRect(hwnd, out RECT cr)) cr = default;
        POINT origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) { origin.X = wr.Left; origin.Y = wr.Top; }

        int clientX0 = origin.X, clientY0 = origin.Y;
        int clientX1 = origin.X + (cr.Right - cr.Left) - 1;
        int clientY1 = origin.Y + (cr.Bottom - cr.Top) - 1;

        int regX0 = _regValid ? origin.X + _regX0 : -1;
        int regY0 = _regValid ? origin.Y + _regY0 : -1;
        int regX1 = _regValid ? regX0 + _regW : -1;
        int regY1 = _regValid ? regY0 + _regH : -1;

        double s = GetDpiForWindow(hwnd) / 96.0;
        int pillW = (int)Math.Round(_compactPillW * s);
        int pillY0 = (int)Math.Round(_dashboardHeightDip * s);
        int pillY1 = (int)Math.Round((_dashboardHeightDip + _stripHeightDip) * s);
        int pillX0 = origin.X, pillX1 = origin.X + pillW;
        int pillCY = origin.Y + (pillY0 + pillY1) / 2;
        int pillCX = origin.X + pillW / 2;

        // STEP 1: the Halo rectangle. For this machine: x=0..999, y=190..1079.
        bool InHalo(int x, int y) => x >= wr.Left && x < wr.Right && y >= wr.Top && y < wr.Bottom;
        bool Similar(uint a, uint b, int tol = 24)
            => Math.Abs(R(a) - R(b)) <= tol && Math.Abs(G(a) - G(b)) <= tol && Math.Abs(B(a) - B(b)) <= tol;

        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            // Wallpaper reference: immediately OUTSIDE the window (the ONLY
            // allowed out-of-bounds samples; INVALID for classification).
            var wallRefs = new List<(string tag, int x, int y, uint c)>();
            foreach (var (tag, x, y) in new (string, int, int)[]
            {
                ("wallTop", pillCX, wr.Top - 16),
                ("wallBottom", pillCX, wr.Bottom + 16),
                ("wallLeft", wr.Left - 16, pillCY),
                ("wallRight", wr.Right + 16, pillCY),
            })
                wallRefs.Add((tag, x, y, GetPixel(dc, x, y)));
            uint wallBase = 0;
            bool wallOk = false;
            foreach (var (tag, x, y, c) in wallRefs)
            {
                Helpers.Logger.Info($"[P42-WALL] tag={tag} screen=({x},{y}) rgb=({R(c)},{G(c)},{B(c)}) INVALID(no owner-classification — outside halo hwnd bounds)");
                if (R(c) > 250 && G(c) > 250 && B(c) > 250) continue; // CLR_INVALID / off-screen
                if (!wallOk) { wallBase = c; wallOk = true; }
                if (tag == "wallRight") { wallBase = c; break; } // same strip as the pill
            }
            if (!wallOk) wallBase = 0;

            // STEP 2: locate the black rectangle INSIDE the Halo HWND bounds.
            int bX0 = int.MaxValue, bY0 = int.MaxValue, bX1 = int.MinValue, bY1 = int.MinValue;
            int envelopeDark = 0, envelopeWallpaperLike = 0, shapeDark = 0, totalScanned = 0;
            var envelopeOwners = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<(int, int)>();
            void Consider(int x, int y)
            {
                if (!InHalo(x, y)) return;
                if (!seen.Add((x, y))) return;
                totalScanned++;
                uint c = GetPixel(dc, x, y);
                int mx = Math.Max(R(c), Math.Max(G(c), B(c)));
                bool inShape = regX0 <= x && x <= regX1 && regY0 <= y && y <= regY1;
                if (inShape)
                {
                    if (mx < 70) shapeDark++;
                    return;
                }
                if (mx < 70)
                {
                    string owner = P40OwnerClass(x, y, hwnd, out _, out _, out _);
                    envelopeOwners.TryGetValue(owner, out int n);
                    envelopeOwners[owner] = n + 1;
                    envelopeDark++;
                    // Dark AND different from the wallpaper behind ⇒ painted.
                    if (Similar(c, wallBase, 24))
                    {
                        envelopeWallpaperLike++;
                    }
                    else
                    {
                        bX0 = Math.Min(bX0, x); bX1 = Math.Max(bX1, x);
                        bY0 = Math.Min(bY0, y); bY1 = Math.Max(bY1, y);
                    }
                }
            }
            void ScanHorz(int y, int step) { for (int x = wr.Left; x < wr.Right; x += step) Consider(x, y); }
            void ScanVert(int x, int step) { for (int y = wr.Top; y < wr.Bottom; y += step) Consider(x, y); }

            for (int y = wr.Top; y < wr.Bottom; y += 8)
                for (int x = wr.Left; x < wr.Right; x += 8)
                    Consider(x, y);
            ScanHorz(wr.Top + 1, 2);
            ScanHorz(wr.Bottom - 2, 2);
            ScanHorz(Math.Max(wr.Top + 1, origin.Y + pillY0 - 24), 2); // just above the pill
            ScanHorz(Math.Min(wr.Bottom - 1, origin.Y + pillY1 + 24), 2); // just below the pill
            ScanVert(wr.Left + 1, 2);
            ScanVert(wr.Right - 2, 2);

            bool detected = bX0 <= bX1;
            if (!detected)
            {
                bX0 = wr.Left; bY0 = wr.Top; bX1 = wr.Right - 1; bY1 = wr.Bottom - 1;
            }

            // STEP 2 record: per-pixel owner attribution at named + border points.
            var samples = new List<(string tag, int x, int y)>
            {
                ("haloTopLeft", wr.Left, wr.Top),
                ("haloTopRight", wr.Right - 1, wr.Top),
                ("haloBottomLeft", wr.Left, wr.Bottom - 1),
                ("haloBottomRight", wr.Right - 1, wr.Bottom - 1),
                ("haloCenter", (wr.Left + wr.Right) / 2, (wr.Top + wr.Bottom) / 2),
                ("envelopeTopMid", pillCX, wr.Top + 2),
                ("envelopeLeftMid", wr.Left + 2, pillCY),
                ("envelopeRightMid", wr.Right - 3, pillCY),
                ("pillCenter", pillCX, pillCY),
            };
            if (detected)
            {
                samples.Add(("borderTop", (bX0 + bX1) / 2, bY0));
                samples.Add(("borderBottom", (bX0 + bX1) / 2, bY1));
                samples.Add(("borderLeft", bX0, (bY0 + bY1) / 2));
                samples.Add(("borderRight", bX1, (bY0 + bY1) / 2));
                void DenseEdge(string tagBase, int x0, int y0, int x1, int y1)
                {
                    int len = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
                    int step = Math.Max(1, len / 20);
                    int i = 0;
                    for (int t = 0; t <= len; t += step)
                    {
                        int px = x0 + (x1 - x0) * t / len;
                        int py = y0 + (y1 - y0) * t / len;
                        LogP42Pixel($"{tagBase}#{i++}", px, py, hwnd, origin, regX0, regY0, regX1, regY1, clientX0, clientY0, clientX1, clientY1, stateTag);
                    }
                }
                DenseEdge("borderTop", bX0, bY0, bX1, bY0);
                DenseEdge("borderBottom", bX0, bY1, bX1, bY1);
                DenseEdge("borderLeft", bX0, bY0, bX0, bY1);
                DenseEdge("borderRight", bX1, bY0, bX1, bY1);
            }
            foreach (var (tag, x, y) in samples)
                LogP42Pixel(tag, x, y, hwnd, origin, regX0, regY0, regX1, regY1, clientX0, clientY0, clientX1, clientY1, stateTag);

            // STEP 3: enumerate EVERY Halo HWND and test each rect against the
            // detected rectangle (children + owned + helpers, overlaps flagged).
            App.WindowService.LogP41WindowCensus(bX0, bY0, bX1, bY1);

            var ownerParts = new StringBuilder();
            foreach (var kv in envelopeOwners)
            {
                if (ownerParts.Length > 0) ownerParts.Append(',');
                ownerParts.Append($"{kv.Key}:{kv.Value}");
            }
            string dominant = "none";
            int top = -1;
            foreach (var kv in envelopeOwners)
                if (kv.Value > top) { top = kv.Value; dominant = kv.Key; }

            string match = "none";
            if (detected)
            {
                if (bX0 <= wr.Left + 8 && bY0 <= wr.Top + 8 && bX1 >= wr.Right - 8 && bY1 >= wr.Bottom - 8) match = "fullWindowRect";
                else if (bX0 >= wr.Left + 4 && bX1 <= wr.Right - 4 && bY0 >= wr.Top + 4 && bY1 <= wr.Bottom - 4) match = "innerWindowSurface";
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT ext, Marshal.SizeOf<RECT>()) == 0)
                    if (ContainsRgn(ext.Left, ext.Top, ext.Right, ext.Bottom, bX0, bY0, bX1, bY1)) match = "extendedFrame";
            }

            Helpers.Logger.Info($"[P42-BBOX] state={stateTag} totalScanned={totalScanned} envelopeDark={envelopeDark} " +
                                $"envelopeWallpaperLike={envelopeWallpaperLike} shapeDark={shapeDark} " +
                                $"detected={detected} bbox=({bX0},{bY0})-({bX1},{bY1}) " +
                                $"wallRef=({R(wallBase)},{G(wallBase)},{B(wallBase)}) " +
                                $"owners=[{ownerParts}] dominantOwner={dominant} MATCH={match}");
            if (detected)
            {
                Helpers.Logger.Info($"[P42-CONCLUSION] black rectangle DETECTED INSIDE the Halo HWND bounds (bbox=({bX0},{bY0})-({bX1},{bY1})) " +
                                    $"dominantOwner='{dominant}' MATCH={match} — {ExplanatoryFor(dominant, match)}");
            }
            else
            {
                Helpers.Logger.Info($"[P42-CONCLUSION] NO black-rectangle pixels INSIDE the Halo HWND bounds " +
                                    $"(envelopeDark={envelopeDark} wallpaperLike={envelopeWallpaperLike} wallRef=({R(wallBase)},{G(wallBase)},{B(wallBase)})) " +
                                    $"— the visible rectangle is NOT painted on Halo's window surface; it is OUTSIDE the Halo HWND (another HWND / DWM) or the surrounding desktop is itself that dark.");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P42-SCAN] failed", ex);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    /// <summary>
    /// PASS 42 (STEP 2): records one P42-PIXEL owner sample. Coordinates are
    /// screen-space. Samples outside the Halo HWND bounds are marked INVALID and
    /// excluded from owner classification.
    /// </summary>
    private void LogP42Pixel(string tag, int x, int y, IntPtr hwnd, POINT origin,
        int regX0, int regY0, int regX1, int regY1,
        int clientX0, int clientY0, int clientX1, int clientY1, string stateTag)
    {
        if (!GetWindowRect(hwnd, out RECT wr)) return;
        if (x < wr.Left || x >= wr.Right || y < wr.Top || y >= wr.Bottom)
        {
            Helpers.Logger.Info($"[P42-PIXEL] tag={tag} state={stateTag} screen=({x},{y}) INVALID(outside halo hwnd bounds) — excluded from owner classification");
            return;
        }
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            uint c = GetPixel(dc, x, y);
            IntPtr owner = WindowFromPoint(new POINT { X = x, Y = y });
            IntPtr child = ChildWindowFromPoint(hwnd, new POINT { X = x - origin.X, Y = y - origin.Y });
            IntPtr root = owner != IntPtr.Zero ? GetAncestor(owner, GA_ROOT) : IntPtr.Zero;
            IntPtr gwlParent = owner != IntPtr.Zero ? GetWindowLongPtr(owner, GWLP_HWNDPARENT) : IntPtr.Zero;
            string cls = "";
            if (owner != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                GetClassName(owner, sb, sb.Capacity);
                cls = sb.ToString();
            }
            string ownerStr = P40OwnerClass(x, y, hwnd, out _, out _, out _);
            bool inWindow = x >= wr.Left && x < wr.Right && y >= wr.Top && y < wr.Bottom;
            bool inClient = x >= clientX0 && x <= clientX1 && y >= clientY0 && y <= clientY1;
            bool inShape = _regValid && x >= regX0 && x <= regX1 && y >= regY0 && y <= regY1;
            Helpers.Logger.Info(
                $"[P42-PIXEL] tag={tag} state={stateTag} screen=({x},{y}) rgb=({R(c)},{G(c)},{B(c)}) " +
                $"ownerHwnd=0x{owner.ToInt64():X} childHwnd=0x{child.ToInt64():X} rootHwnd=0x{root.ToInt64():X} " +
                $"gwlParent=0x{gwlParent.ToInt64():X} class={cls} owner={ownerStr} " +
                $"insideHaloRegion={inWindow} insideClient={inClient} insideVisibleShape={inShape}");
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    // ── PASS 43 — self-contained binary tests (finish the P42 diagnosis) ──
    // P42 left the diagnosis incomplete: the interventions (bridge hide / DWM
    // attrs / empty content) were applied at startup with no before/after pixel
    // comparison, and the [P42-BBOX]/[P42-CONCLUSION] scan only ran under
    // HALO_P42_PIXEL_OWNER. Pass 43 makes each test SELF-CONTAINED and
    // sequential: baseline desktop pixels INSIDE the window → apply the layer
    // change → force a compositor/window redraw WITHOUT changing geometry
    // (InvalidateRect/RedrawWindow/DwmFlush) → wait ~350 ms for composition to
    // settle → sample the EXACT same pixels again → log [P42-*-RESULT] with
    // beforeRgb/afterRgb/changedPixels/rectangleGone → restore the layer so the
    // next test starts from a clean baseline. Real GDI GetPixel only (never
    // RenderTargetBitmap); every sample strictly inside GetWindowRect(haloHwnd);
    // before every test the pixel owner is verified (WindowFromPoint +
    // GetAncestor(GA_ROOT) + GetWindowRect + GetClassName). Ends with one
    // machine-readable [P42-CONCLUSION]. Diagnosis only — no production change.

    /// <summary>
    /// PASS 43: runs every enabled binary test (EMPTY_CONTENT /
    /// NO_CONTENT_BRIDGE / NO_DWM_FRAME) as baseline → apply → redraw → wait →
    /// re-sample → restore, then prints the aggregated [P42-CONCLUSION].
    /// </summary>
    public async Task RunP42BinaryTests()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!GetWindowRect(hwnd, out RECT wr)) return;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return;

            // Sample points — strictly INSIDE the Halo HWND bounds (the 1000×890
            // envelope on this machine: x=0..999, y=190..1079). All in the
            // upper envelope, far from the bottom pill strip.
            var points = new List<(string tag, int x, int y)>();
            foreach (int fx in new[] { 15, 50, 85 })
                foreach (int fy in new[] { 10, 30, 55 })
                    points.Add(($"env{fx}x{fy}", wr.Left + w * fx / 100, wr.Top + h * fy / 100));
            points.Add(("envCenter", wr.Left + w / 2, wr.Top + h / 2));
            points.Add(("edgeLeft", wr.Left + 2, wr.Top + h / 2));
            points.Add(("edgeRight", wr.Right - 3, wr.Top + h / 2));
            points.Add(("edgeTop", wr.Left + w / 2, wr.Top + 2));

            // Wallpaper references captured immediately OUTSIDE the window —
            // references only (never counted as samples, never classified).
            var wallRefs = new (string tag, int x, int y)[]
            {
                ("wallRight", wr.Right + 16, wr.Top + h * 10 / 100),
                ("wallRightMid", wr.Right + 16, wr.Top + h / 2),
                ("wallRightLow", wr.Right + 16, wr.Top + h * 55 / 100),
            };

            // phaseTag "BASE" also logs the owner verification (rule 6) for
            // every point: WindowFromPoint + GetAncestor(GA_ROOT) + GetWindowRect
            // + GetClassName. Returns (tag, x, y, rgb) for samples AND wall refs.
            List<(string tag, int x, int y, uint c)> SampleAll(string phaseTag)
            {
                var list = new List<(string, int, int, uint)>();
                IntPtr dc = GetDC(IntPtr.Zero);
                try
                {
                    foreach (var (tag, x, y) in points)
                    {
                        uint c = GetPixel(dc, x, y);
                        list.Add((tag, x, y, c));
                        if (phaseTag == "BASE")
                        {
                            IntPtr owner = WindowFromPoint(new POINT { X = x, Y = y });
                            IntPtr root = owner != IntPtr.Zero ? GetAncestor(owner, GA_ROOT) : IntPtr.Zero;
                            string cls = "";
                            if (owner != IntPtr.Zero)
                            {
                                var sb = new StringBuilder(256);
                                GetClassName(owner, sb, sb.Capacity);
                                cls = sb.ToString();
                            }
                            Helpers.Logger.Info($"[P42-BASE] tag={tag} state={P40StateString()} coord=({x},{y}) rgb=({R(c)},{G(c)},{B(c)}) " +
                                                $"ownerHwnd=0x{owner.ToInt64():X} rootHwnd=0x{root.ToInt64():X} class={cls} " +
                                                $"winRect=({wr.Left},{wr.Top},{wr.Right - wr.Left}x{wr.Bottom - wr.Top})");
                        }
                        else
                        {
                            Helpers.Logger.Info($"[P42-AFTER] tag={tag} state={P40StateString()} coord=({x},{y}) rgb=({R(c)},{G(c)},{B(c)})");
                        }
                    }
                    foreach (var (tag, x, y) in wallRefs)
                    {
                        uint c = GetPixel(dc, x, y);
                        list.Add(("WALL-" + tag, x, y, c));
                        Helpers.Logger.Info($"[P42-WALL] tag={tag} coord=({x},{y}) rgb=({R(c)},{G(c)},{B(c)}) (reference only — outside window)");
                    }
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, dc);
                }
                return list;
            }

            string LogP42TestResult(string test, List<(string tag, int x, int y, uint c)> before, List<(string tag, int x, int y, uint c)> after)
            {
                int suspBefore = 0, suspAfter = 0, changedPixels = 0, afterLight = 0;
                var beforeRgb = new StringBuilder();
                var afterRgb = new StringBuilder();
                foreach (var b in before)
                {
                    if (b.tag.StartsWith("WALL-", StringComparison.Ordinal)) continue;
                    var a = after.First(s => s.tag == b.tag);
                    int mb = Math.Max(R(b.c), Math.Max(G(b.c), B(b.c)));
                    int ma = Math.Max(R(a.c), Math.Max(G(a.c), B(a.c)));
                    if (mb < 70) suspBefore++;
                    if (ma < 70) suspAfter++;
                    if (Math.Abs(R(b.c) - R(a.c)) > 16 || Math.Abs(G(b.c) - G(a.c)) > 16 || Math.Abs(B(b.c) - B(a.c)) > 16) changedPixels++;
                    if (ma >= 70) afterLight++;
                    if (beforeRgb.Length > 0) beforeRgb.Append(';');
                    beforeRgb.Append($"{b.tag}:{R(b.c)},{G(b.c)},{B(b.c)}");
                    if (afterRgb.Length > 0) afterRgb.Append(';');
                    afterRgb.Append($"{a.tag}:{R(a.c)},{G(a.c)},{B(a.c)}");
                }
                bool gone = suspBefore > 0 && suspAfter == 0;
                string status = suspBefore == 0 ? "inconclusive" : (gone ? "gone" : "persists");
                Helpers.Logger.Info($"[P42-{test}-RESULT] beforeRgb=[{beforeRgb}] afterRgb=[{afterRgb}] " +
                                    $"suspiciousBefore={suspBefore} suspiciousAfter={suspAfter} changedPixels={changedPixels} " +
                                    $"afterLight={afterLight} rectangleGone={gone} status={status}");
                return status;
            }

            var conclusion = new Dictionary<string, string>();

            // ── TEST 1: EMPTY_CONTENT — transparent XAML content only ──────
            if (Helpers.MotionDiagnostics.EnableP42EmptyContent)
            {
                var before = SampleAll("BASE");
                UIElement? originalContent = Content;
                Helpers.Logger.Info("[P42-SEQ] EMPTY: swapping Window.Content to a transparent Grid");
                try
                {
                    Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                }
                catch (Exception ex)
                {
                    Helpers.Logger.Error("[P42-SEQ] EMPTY content swap failed", ex);
                }
                App.WindowService.ForceP42Redraw(hwnd);
                await Task.Delay(350);
                var after = SampleAll("AFTER");
                conclusion["emptyContent"] = LogP42TestResult("EMPTY", before, after);
                try { Content = originalContent; }
                catch (Exception ex) { Helpers.Logger.Error("[P42-SEQ] EMPTY content restore failed", ex); }
                App.WindowService.ForceP42Redraw(hwnd);
                await Task.Delay(300);
            }

            // ── TEST 2: NO_CONTENT_BRIDGE — hide the DesktopChildSiteBridge ──
            if (Helpers.MotionDiagnostics.EnableP42NoContentBridge)
            {
                var before = SampleAll("BASE");
                App.WindowService.SetP42BridgeVisible(false);
                App.WindowService.ForceP42Redraw(hwnd);
                await Task.Delay(350);
                var after = SampleAll("AFTER");
                conclusion["bridge"] = LogP42TestResult("BRIDGE", before, after);
                App.WindowService.SetP42BridgeVisible(true);
                App.WindowService.ForceP42Redraw(hwnd);
                await Task.Delay(300);
            }

            // ── TEST 3: NO_DWM_FRAME — DWM non-client disabled ─────────────
            if (Helpers.MotionDiagnostics.EnableP42NoDwmFrame)
            {
                var before = SampleAll("BASE");
                App.WindowService.ApplyP42DwmNoFrame(true);
                App.WindowService.ForceP42Redraw(hwnd);
                await Task.Delay(350);
                var after = SampleAll("AFTER");
                conclusion["dwmFrame"] = LogP42TestResult("DWM", before, after);
                App.WindowService.ApplyP42DwmNoFrame(false);
                App.WindowService.ForceP42Redraw(hwnd);
                await Task.Delay(300);
            }

            // ── Aggregated machine-readable conclusion ─────────────────────
            string Get(string key) => conclusion.TryGetValue(key, out var v) ? v : "inconclusive";
            Helpers.Logger.Info($"[P42-CONCLUSION] bridge={Get("bridge")} emptyContent={Get("emptyContent")} dwmFrame={Get("dwmFrame")}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P42-SEQ] binary-test sequence failed", ex);
        }
    }

    // ── PASS 44 — DEFINITIVE pixel differential (halo present vs absent) ──
    // P42/P43's "owner" (WindowFromPoint → Chrome_RenderWidgetHostHWND) is only
    // hit-test attribution — because Halo is transparent outside its shaped
    // region, it does NOT prove Chrome paints the pixels. This is the ONE
    // decisive experiment. SAMPLE A (visible) → ShowWindow(SW_HIDE) the ENTIRE
    // main HWND (bridge/DWM/region/acrylic/geometry untouched) → DwmFlush +
    // 500 ms → SAMPLE B (hidden) → SW_SHOWNOACTIVATE → DwmFlush + 500 ms →
    // SAMPLE C (restored). Per-pixel Euclidean RGB distance > 8 = "changed".
    // Majority of suspicious-dark (max RGB < 70) points changing on hide ⇒
    // haloConfirmed=true; identical ⇒ false; a few ⇒ inconclusive. GDI GetPixel
    // only; fixed in-window coordinates; one outside reference (not an
    // ownership test). Restores production state exactly. NO fix in this pass.

    /// <summary>
    /// PASS 44: answers "does hiding Halo make the black rectangle disappear?"
    /// </summary>
    public async Task RunP44PixelDifferential()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!GetWindowRect(hwnd, out RECT wr)) return;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return;

            // Fixed coordinates INSIDE the suspicious envelope (the region above
            // the pill). Exact SAME coordinates for every sample. Clamped to the
            // real screen (never sampled off-screen).
            var points = new List<(string tag, int x, int y)>();
            foreach (int fx in new[] { 15, 50, 85 })
                foreach (int fy in new[] { 10, 30, 55 })
                    points.Add(($"{fx}x{fy}", Math.Max(0, wr.Left + w * fx / 100), Math.Max(0, wr.Top + h * fy / 100)));
            points.Add(("center", Math.Max(0, wr.Left + w / 2), Math.Max(0, wr.Top + h / 2)));

            // One wallpaper/background reference OUTSIDE the Halo window — a
            // reference only; NEVER used as an ownership test.
            (int x, int y) reference = (wr.Right + 16, Math.Max(0, wr.Top + h * 30 / 100));

            List<(string tag, int x, int y, uint c)> SampleAll(string phaseTag)
            {
                var list = new List<(string, int, int, uint)>();
                IntPtr dc = GetDC(IntPtr.Zero);
                try
                {
                    foreach (var (tag, x, y) in points)
                    {
                        uint c = GetPixel(dc, x, y);
                        list.Add((tag, x, y, c));
                        Helpers.Logger.Info($"[P44-{phaseTag}] tag={tag} coord=({x},{y}) rgb=({R(c)},{G(c)},{B(c)})");
                    }
                    uint rc = GetPixel(dc, reference.x, reference.y);
                    Helpers.Logger.Info($"[P44-REFERENCE] coord=({reference.x},{reference.y}) rgb=({R(rc)},{G(rc)},{B(rc)}) (wallpaper/background reference — NOT an ownership test)");
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, dc);
                }
                return list;
            }

            double Dist(uint a, uint b)
                => Math.Sqrt(Math.Pow(R(a) - R(b), 2) + Math.Pow(G(a) - G(b), 2) + Math.Pow(B(a) - B(b), 2));

            // SAMPLE A — halo visible.
            var visible = SampleAll("VISIBLE");

            // HIDE the entire main HWND (nothing else changes).
            App.WindowService.SetP44HaloVisible(false);
            DwmFlush();
            await Task.Delay(500);

            // SAMPLE B — halo hidden.
            var hidden = SampleAll("HIDDEN");

            // SHOW again (no focus steal) + settle.
            App.WindowService.SetP44HaloVisible(true);
            DwmFlush();
            await Task.Delay(500);

            // SAMPLE C — halo restored.
            var restored = SampleAll("RESTORED");

            int darkBefore = 0, changedA = 0, changedB = 0;
            var beforeRgb = new StringBuilder();
            var hiddenRgb = new StringBuilder();
            var restoredRgb = new StringBuilder();
            foreach (var v in visible)
            {
                var hid = hidden.First(s => s.tag == v.tag);
                var res = restored.First(s => s.tag == v.tag);
                int mx = Math.Max(R(v.c), Math.Max(G(v.c), B(v.c)));
                if (mx < 70) darkBefore++;
                if (Dist(v.c, hid.c) > 8) changedA++;
                if (Dist(hid.c, res.c) > 8) changedB++;
                if (beforeRgb.Length > 0) beforeRgb.Append(';');
                beforeRgb.Append($"{v.tag}:{R(v.c)},{G(v.c)},{B(v.c)}");
                if (hiddenRgb.Length > 0) hiddenRgb.Append(';');
                hiddenRgb.Append($"{hid.tag}:{R(hid.c)},{G(hid.c)},{B(hid.c)}");
                if (restoredRgb.Length > 0) restoredRgb.Append(';');
                restoredRgb.Append($"{res.tag}:{R(res.c)},{G(res.c)},{B(res.c)}");
            }

            // Conclusion logic: majority of suspicious-dark pixels changing on
            // hide ⇒ Halo paints them; identical ⇒ they were never Halo's.
            string halo;
            if (darkBefore == 0) halo = "notConfirmed";
            else if (changedA * 2 > darkBefore) halo = "confirmed";
            else if (changedA == 0) halo = "notConfirmed";
            else halo = "inconclusive";
            string haloBool = halo == "confirmed" ? "true" : (halo == "notConfirmed" ? "false" : "inconclusive");

            Helpers.Logger.Info($"[P44-RESULT] beforeRgb=[{beforeRgb}] hiddenRgb=[{hiddenRgb}] restoredRgb=[{restoredRgb}] " +
                                $"darkBefore={darkBefore} changedVisibleToHidden={changedA} changedHiddenToRestored={changedB} " +
                                $"haloConfirmed={haloBool}");
            Helpers.Logger.Info($"[P44-CONCLUSION] halo={halo}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P44-SEQ] pixel differential failed", ex);
        }
    }

    /// <summary>
    /// PASS 45: visual boundary isolation. P44 proved the dark envelope pixels
    /// equal the wallpaper reference (halo=notConfirmed), so the perceived
    /// "black rectangle" is NOT a Halo-painted fill. This run determines its
    /// GEOMETRY with three controlled visual states at IDENTICAL Halo geometry:
    ///   A — normal production surfaces (sampled: envelope + pill-region
    ///       perimeter seams + HWND-envelope perimeter seams + outside ref).
    ///   B — every surface transparent + all backdrop/acrylic disabled
    ///       (backdrop controller disposed, Content = transparent Grid).
    ///   C — Content = full-window magenta Grid clipped by the EXISTING region,
    ///       so the magenta exactly follows the visible pill/dashboard shape —
    ///       the user's screenshot answers whether the boundary hugs the shape
    ///       or the 1000×890 HWND envelope.
    /// Restores production state exactly (Content swapped back, backdrop
    /// re-armed via SetAcrylicBackdrop). GDI GetPixel only; fixed coordinates;
    /// WindowFromPoint/RGB darkness are never treated as ownership. No fix.
    /// </summary>
    public async Task RunP45BoundaryTest()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!GetWindowRect(hwnd, out RECT wr)) return;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return;

            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) { origin.X = wr.Left; origin.Y = wr.Top; }
            double s = GetDpiForWindow(hwnd) / 96.0;

            // Visible shape — compact = pill strip. The SetWindowRgn region IS
            // this box, so it is the exact clip the diagnostic surface follows.
            int pillW = (int)Math.Round(_compactPillW * s);
            int pillX0 = origin.X;
            int pillX1 = origin.X + pillW;
            int pillY0 = origin.Y + (int)Math.Round(_dashboardHeightDip * s);
            int pillY1 = origin.Y + (int)Math.Round((_dashboardHeightDip + _stripHeightDip) * s);
            int pillCX = origin.X + pillW / 2;
            int pillCY = (pillY0 + pillY1) / 2;

            // Envelope points — the same fixed coordinates as P44 (strictly
            // inside the HWND). Exact SAME coordinates for every sample.
            var points = new List<(string tag, int x, int y)>();
            foreach (int fx in new[] { 15, 50, 85 })
                foreach (int fy in new[] { 10, 30, 55 })
                    points.Add(($"{fx}x{fy}", Math.Max(0, wr.Left + w * fx / 100), Math.Max(0, wr.Top + h * fy / 100)));
            points.Add(("center", Math.Max(0, wr.Left + w / 2), Math.Max(0, wr.Top + h / 2)));

            // Shape-edge seams — just inside/outside the visible pill shape.
            points.AddRange(new (string, int, int)[]
            {
                ("shapeTopIn", pillCX, Math.Max(0, pillY0 + 2)),
                ("shapeTopOut", pillCX, Math.Max(0, pillY0 - 2)),
                ("shapeLeftIn", Math.Max(0, pillX0 + 2), pillCY),
                ("shapeLeftOut", Math.Max(0, pillX0 - 2), pillCY),
                ("shapeRightIn", pillX1 - 2, pillCY),
                ("shapeRightOut", pillX1 + 2, pillCY),
                ("shapeBottomIn", pillCX, pillY1 - 2),
                ("shapeBottomOut", pillCX, pillY1 + 2),
            });

            // Envelope-edge seams — just inside/outside the HWND rectangle.
            int winCX = (wr.Left + wr.Right) / 2;
            int winCY = (wr.Top + wr.Bottom) / 2;
            points.AddRange(new (string, int, int)[]
            {
                ("envTopIn", winCX, Math.Max(0, wr.Top + 2)),
                ("envTopOut", winCX, Math.Max(0, wr.Top - 2)),
                ("envLeftIn", Math.Max(0, wr.Left + 2), winCY),
                ("envLeftOut", Math.Max(0, wr.Left - 2), winCY),
                ("envRightIn", Math.Max(0, wr.Right - 3), winCY),
                ("envRightOut", wr.Right + 3, winCY),
            });

            // One wallpaper/background reference OUTSIDE the Halo window — a
            // reference only; NEVER used as an ownership test.
            (int x, int y) reference = (wr.Right + 16, Math.Max(0, wr.Top + h * 30 / 100));

            List<(string tag, int x, int y, uint c)> SampleAll(string phaseTag)
            {
                var list = new List<(string, int, int, uint)>();
                IntPtr dc = GetDC(IntPtr.Zero);
                try
                {
                    foreach (var (tag, x, y) in points)
                    {
                        uint c = GetPixel(dc, x, y);
                        list.Add((tag, x, y, c));
                        Helpers.Logger.Info($"[P45-{phaseTag}] tag={tag} state={P40StateString()} coord=({x},{y}) rgb=({R(c)},{G(c)},{B(c)})");
                    }
                    uint rc = GetPixel(dc, reference.x, reference.y);
                    list.Add(("WALL-REF", reference.x, reference.y, rc));
                    Helpers.Logger.Info($"[P45-REFERENCE] coord=({reference.x},{reference.y}) rgb=({R(rc)},{G(rc)},{B(rc)}) (wallpaper/background reference — NOT an ownership test)");
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, dc);
                }
                return list;
            }

            double Dist(uint a, uint b)
                => Math.Sqrt(Math.Pow(R(a) - R(b), 2) + Math.Pow(G(a) - G(b), 2) + Math.Pow(B(a) - B(b), 2));

            uint ByTag(List<(string tag, int x, int y, uint c)> list, string tag)
                => list.First(s => s.tag == tag).c;

            // ── STATE A — NORMAL production surfaces ──────────────────────
            Helpers.Logger.Info($"[P45-SEQ] STATE A — normal production surfaces region=({origin.X + _regX0},{origin.Y + _regY0},{_regW}x{_regH}) backdrop={(_acrylicController != null ? "armed" : "none")}");
            var a = SampleAll("A");

            // ── STATE B — fully transparent surfaces, no backdrop/acrylic ──
            UIElement? originalContent = Content;
            Helpers.Logger.Info("[P45-SEQ] STATE B — forcing FULL transparent backdrop (DesktopAcrylicController disposed, Content = transparent Grid; NO HWND/region/bridge/DWM/animation change)");
            try
            {
                _acrylicController?.Dispose();
                _acrylicController = null;
                _configuration = null;
            }
            catch (Exception ex) { Helpers.Logger.Error("[P45-SEQ] backdrop disposal failed", ex); }
            try
            {
                Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            }
            catch (Exception ex) { Helpers.Logger.Error("[P45-SEQ] transparent content swap failed", ex); }
            App.WindowService.ForceP42Redraw(hwnd);
            DwmFlush();
            await Task.Delay(500);
            var b = SampleAll("B");

            // ── STATE C — high-contrast diagnostic surface (region-clipped) ─
            Helpers.Logger.Info("[P45-SEQ] STATE C — magenta diagnostic surface clipped by the EXISTING region (visible pill/dashboard shape). VISUAL CHECK: does any dark seam hug the magenta shape, or does it hug the 1000x890 HWND envelope, or is there none?");
            try
            {
                Content = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x00, 0xFF)) };
            }
            catch (Exception ex) { Helpers.Logger.Error("[P45-SEQ] magenta content swap failed", ex); }
            App.WindowService.ForceP42Redraw(hwnd);
            DwmFlush();
            await Task.Delay(500);
            var c = SampleAll("C");

            // ── RESTORE production state exactly ──────────────────────────
            Helpers.Logger.Info("[P45-SEQ] restoring production state (Content swapped back, backdrop re-armed)");
            try { Content = originalContent; }
            catch (Exception ex) { Helpers.Logger.Error("[P45-SEQ] content restore failed", ex); }
            try { SetAcrylicBackdrop(); }
            catch (Exception ex) { Helpers.Logger.Error("[P45-SEQ] backdrop re-arm failed", ex); }
            App.WindowService.ForceP42Redraw(hwnd);
            DwmFlush();
            await Task.Delay(500);
            Helpers.Logger.Info($"[P45-SEQ] restored state={P40StateString()} backdrop={(_acrylicController != null ? "armed" : "none")}");

            // ── Conclusion ────────────────────────────────────────────────
            string[] envTags = { "15x10", "15x30", "15x55", "50x10", "50x30", "50x55", "85x10", "85x30", "85x55", "center" };
            int changedAB = 0;
            foreach (var t in envTags)
            {
                if (Dist(ByTag(a, t), ByTag(b, t)) > 8) changedAB++;
            }
            // surfaces dependent: transparenting all Halo surfaces changed the
            // envelope pixels ⇒ the boundary belonged to a Halo surface.
            string surfaceDependent = changedAB == 0 ? "false"
                : changedAB * 2 > envTags.Length ? "true"
                : "inconclusive";

            // Seams: shapeOut vs wallpaper ref = boundary hugging the shape;
            // envIn vs envOut = boundary at the HWND rectangle edge. Both A and
            // C contribute (A = production, C = magenta shape highlights edges).
            string[] shapeOutTags = { "shapeTopOut", "shapeLeftOut", "shapeRightOut", "shapeBottomOut" };
            string[] envSeamPairs = { "envTop", "envLeft", "envRight" };
            uint refA = ByTag(a, "WALL-REF"), refC = ByTag(c, "WALL-REF");
            int shapeSeam = 0, envSeam = 0;
            foreach (var t in shapeOutTags)
            {
                if (Dist(ByTag(a, t), refA) > 12) shapeSeam++;
                if (Dist(ByTag(c, t), refC) > 12) shapeSeam++;
            }
            foreach (var t in envSeamPairs)
            {
                if (Math.Abs(R(ByTag(a, t + "In")) - R(ByTag(a, t + "Out"))) > 12
                    || Math.Abs(G(ByTag(a, t + "In")) - G(ByTag(a, t + "Out"))) > 12
                    || Math.Abs(B(ByTag(a, t + "In")) - B(ByTag(a, t + "Out"))) > 12) envSeam++;
                if (Math.Abs(R(ByTag(c, t + "In")) - R(ByTag(c, t + "Out"))) > 12
                    || Math.Abs(G(ByTag(c, t + "In")) - G(ByTag(c, t + "Out"))) > 12
                    || Math.Abs(B(ByTag(c, t + "In")) - B(ByTag(c, t + "Out"))) > 12) envSeam++;
            }
            string boundaryFollows;
            if (envSeam > 0 && shapeSeam == 0) boundaryFollows = "envelope";
            else if (shapeSeam > 0 && envSeam == 0) boundaryFollows = "shape";
            else if (shapeSeam > 0 && envSeam > 0) boundaryFollows = "envelope"; // envelope encloses the shape
            else boundaryFollows = "desktop"; // no RGB seam at either edge — the dark pixels are the wallpaper through the transparent envelope

            Helpers.Logger.Info($"[P45-CONCLUSION] boundaryFollows={boundaryFollows} surfaceDependent={surfaceDependent} shapeSeam={shapeSeam} envSeam={envSeam} changedAB={changedAB}");
            Helpers.Logger.Info("[P45-NOTE] RGB seams are invisible on a uniform dark wallpaper — the STATE C screenshot is authoritative: dark outline around the magenta shape = shape; around the 1000x890 rectangle = envelope; no outline = desktop.");
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[P45-SEQ] boundary test failed", ex);
        }
    }

    /// <summary>
    /// PASS 41 (GOAL 2): classifies who owns the pixel at (x,y) — Halo's main
    /// HWND, another Halo HWND, a WinUI/compositor helper HWND, another app,
    /// DWM (pid 0), or the desktop (no window).
    /// </summary>
    private string P40OwnerClass(int x, int y, IntPtr haloHwnd, out IntPtr h, out uint pid, out string className)
    {
        h = WindowFromPoint(new POINT { X = x, Y = y });
        pid = 0;
        className = "";
        if (h == IntPtr.Zero) return "desktop";
        GetWindowThreadProcessId(h, out pid);
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        className = sb.ToString();
        uint myPid = (uint)Environment.ProcessId;
        if (h == haloHwnd) return "haloMain";
        if (pid == myPid)
        {
            if (className.Contains("WinUI", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Composition", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Content", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Island", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Window", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Input", StringComparison.OrdinalIgnoreCase))
                return "haloHelperWinUI";
            return "haloOther";
        }
        if (pid == 0) return "dwm";
        return $"otherApp(pid={pid})";
    }

    private static bool ContainsRgn(int rX0, int rY0, int rX1, int rY1, int bX0, int bY0, int bX1, int bY1)
        => bX0 >= rX0 - 4 && bX1 <= rX1 + 4 && bY0 >= rY0 - 4 && bY1 <= rY1 + 4;

    private static string ExplanatoryFor(string owner, string match)
    {
        if (owner == "desktop") return "no HWND owns the pixel — the rectangle is painted by the compositor/desktop surface, not a window";
        if (owner == "dwm") return "the pixels belong to a pid-0 window (DWM/Shell) — non-client/desktop rendering";
        if (owner == "haloMain") return "the pixels belong to Halo's own HWND surface (client or non-client area of the main window)";
        if (owner == "haloHelperWinUI") return "a WinUI/compositor helper HWND of this process renders the pixels";
        if (owner.StartsWith("otherApp")) return "another application's HWND renders the pixels over/under Halo";
        return $"owner '{owner}' with rectangle matching {match}";
    }

    private string P40StateString()
    {
        if (!_regValid) return "noreg";
        if (_clipTop < _dashboardHeightDip - 0.5) return "expanded";
        double s = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        bool popup = _regH > _stripHeightDip * s + 1;
        return popup ? "popup" : "compact";
    }

    private uint PixelAt(int x, int y)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 0xFFFFFFFF; // CLR_INVALID
        try { return GetPixel(dc, x, y); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static int R(uint c) => (int)(c & 0xFF);
    private static int G(uint c) => (int)((c >> 8) & 0xFF);
    private static int B(uint c) => (int)((c >> 16) & 0xFF);

    private static string Classify(int x, int y, RECT wr, RECT cr, POINT origin,
        int regX0, int regY0, int regX1, int regY1, int pillX0, int pillX1, int pillY0, int pillY1)
    {
        if (x >= pillX0 && x <= pillX1 && y >= origin.Y + pillY0 && y <= origin.Y + pillY1) return "inPill";
        if (x >= regX0 && x <= regX1 && y >= regY0 && y <= regY1) return "inRegionOutsidePill";
        if (x >= wr.Left && x <= wr.Right && y >= wr.Top && y <= wr.Bottom) return "windowOutsideRegion";
        return "outsideWindow";
    }

    private static string DescribeBrush(Brush? b)
    {
        if (b == null) return "null";
        switch (b)
        {
            case SolidColorBrush scb:
                return $"SolidColor(rgba={scb.Color.A},{scb.Color.R},{scb.Color.G},{scb.Color.B},opacity={scb.Opacity:F2})";
            case AcrylicBrush ab:
                return $"Acrylic(tint=({ab.TintColor.R},{ab.TintColor.G},{ab.TintColor.B}),tintOp={ab.TintOpacity:F2})";
            default:
                return b.GetType().Name;
        }
    }

    private static Brush? P40Background(DependencyObject o) => o switch
    {
        Panel p => p.Background,
        Border b => b.Background,
        Control c => c.Background,
        ContentPresenter cp => cp.Background,
        _ => null,
    };

    private static Brush? P40BorderBrush(DependencyObject o) => o switch
    {
        Border b => b.BorderBrush,
        Control c => c.BorderBrush,
        ContentPresenter cp => cp.BorderBrush,
        _ => null,
    };

    private static Thickness P40BorderThickness(DependencyObject o) => o switch
    {
        Border b => b.BorderThickness,
        Control c => c.BorderThickness,
        ContentPresenter cp => cp.BorderThickness,
        _ => default,
    };

    private static CornerRadius P40CornerRadius(DependencyObject o) => o switch
    {
        Border b => b.CornerRadius,
        Control c => c.CornerRadius,
        ContentPresenter cp => cp.CornerRadius,
        _ => default,
    };

    private static string ThicknessStr(Thickness t)
        => $"({t.Left},{t.Top},{t.Right},{t.Bottom})";

    private static string CornerRadiusStr(CornerRadius c)
        => $"({c.TopLeft},{c.TopRight},{c.BottomRight},{c.BottomLeft})";
}
