using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WinRT;

namespace DynamicIsland.Views;

/// <summary>
/// Pass 15 control window: an ordinary WinUI 3 window that drives a REAL
/// per-frame visual change — a rotating rectangle (XAML RenderTransform) —
/// from inside every CompositionTarget.Rendering callback. This is the same
/// UI-thread render-loop pattern Halo Bar's animation uses, so the measured
/// delivery cadence is directly comparable. Pass 14's reference window was
/// quiescent (a static window presents one frame then nothing), so it never
/// measured what cadence a continuously-rendering plain window receives;
/// this window is that control.
///
/// The window itself stays FIXED — no geometry changes, no resizing, no
/// MoveAndResize during measurement.
///
/// Modes (HALO_P15_CONTROL):
///   1  opaque plain window (default WinUI config — machine-level control)
///   2  opaque + Halo window configuration (styles + DWM + topmost + z-guard)
///   3  transparent + Halo config (transparency without acrylic)
///   4  transparent + Halo config + acrylic backdrop (Halo's full presentation)
///
/// The 2×2 isolates: opaque-vs-transparent content and plain-vs-Halo window
/// configuration, plus the acrylic backdrop — the last untested variable from
/// Pass 14 (its styled reference was opaque and quiescent, so neither
/// transparency nor the backdrop was ever exercised by a rendering window).
/// </summary>
public sealed class ControlWindow : Window
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zGuardTimer;
    private Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController? _acrylic;
    private Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration? _backdropConfig;
    private readonly Rectangle _probeRect;

    /// <summary>The per-frame visual driver (rotated by the P15 probe).</summary>
    public Rectangle ProbeRect => _probeRect;

    public ControlWindow(int mode)
    {
        Title = "Halo Bar — P15 Control";
        bool transparent = mode >= 3;

        var root = new Grid
        {
            Background = transparent
                ? new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) // fully transparent
                : new SolidColorBrush(Color.FromArgb(255, 44, 46, 52)),
        };
        _probeRect = new Rectangle
        {
            Width = 100,
            Height = 100,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 140, 60)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new CompositeTransform(),
        };
        root.Children.Add(_probeRect);
        Content = root;

        if (mode >= 2) ApplyHaloStyleConfig();
        if (mode == 4) SetAcrylicBackdrop();
    }

    /// <summary>
    /// One-time plain placement: 400×300 DIP (× scale) at a normal desktop
    /// location, away from the taskbar strip. Called once BEFORE Activate (so
    /// styles can apply while hidden, like Halo Bar); no further geometry work
    /// happens during measurement.
    /// </summary>
    public void Place()
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        double scale = GetDpiForWindow(hwnd) / 96.0;
        int w = (int)Math.Round(400 * scale);
        int h = (int)Math.Round(300 * scale);
        appWindow.MoveAndResize(new RectInt32(240, 160, w, h));
    }

    private void SetAcrylicBackdrop()
    {
        if (!Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()) return;
        _acrylic = new Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController();
        _backdropConfig = new Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration
        {
            IsInputActive = true, // never go solid gray on deactivation (Halo behavior)
        };
        var target = this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
        _acrylic.AddSystemBackdropTarget(target);
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void ApplyHaloStyleConfig()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMinimizable = false;
            p.IsMaximizable = false;
            p.IsAlwaysOnTop = true;
        }
        int corner = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
        DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        int borderNone = -2; // DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = -2
        DwmSetWindowAttribute(hwnd, 34, ref borderNone, sizeof(int));
        int noShadow = 1;  // DWMWA_NCRENDERING_POLICY = 2, DWMNCRP_DISABLED = 1
        DwmSetWindowAttribute(hwnd, 2, ref noShadow, sizeof(int));
        int style = GetWindowLong(hwnd, -16); // GWL_STYLE
        SetWindowLong(hwnd, -16, (style & ~0x00CF0000) | unchecked((int)0x80000000)); // WS_POPUP
        int ex = GetWindowLong(hwnd, -20);    // GWL_EXSTYLE
        SetWindowLong(hwnd, -20, ex | 0x00000080 | 0x08000000); // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE

        // Halo Bar's z-order guard: re-assert HWND_TOPMOST every 150 ms.
        _zGuardTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _zGuardTimer.Interval = TimeSpan.FromMilliseconds(150);
        _zGuardTimer.IsRepeating = true;
        _zGuardTimer.Tick += (_, _) => SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
        _zGuardTimer.Start();

        Helpers.Logger.Info($"[P15] STYLED mode=haloStyle transparent={(GetWindowLong(hwnd, -20) & 0x08000000) != 0}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
