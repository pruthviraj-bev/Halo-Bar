using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;

namespace DynamicIsland.Views;

/// <summary>
/// Pass 14: intentionally boring diagnostic window. An ordinary WinUI 3
/// <see cref="Window"/> with a plain opaque background and a single TextBlock —
/// no acrylic backdrop, no taskbar ownership, no WS_EX_NOACTIVATE/TOOLWINDOW
/// styling, no Halo Bar widgets, no dashboard, no animation, no ongoing
/// geometry updates. Placed once at a fixed 400×300 DIP size in a normal
/// desktop location well away from the taskbar strip, then left completely
/// static while <see cref="Helpers.MotionDiagnostics.ReferenceProbe"/> measures
/// the CompositionTarget.Rendering delivery cadence it receives.
/// </summary>
public sealed class ReferenceWindow : Window
{
    // Rooted so the forever Storyboard cannot be GC'd mid-measurement.
    private readonly Storyboard _sustainStoryboard = new();

    // UI-thread invalidation driver (see ctor). Rooted so it cannot be GC'd.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sustainTimer;

    // Halo-style z-order guard (mode 2 bisect). Rooted so it cannot be GC'd.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zGuardTimer;

    public ReferenceWindow()
    {
        Title = "Halo Bar — P14 Reference";

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 44, 46, 52)),
        };
        root.Children.Add(new TextBlock
        {
            Text = "P14 reference window",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 210, 212, 216)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Content = root;

        // MEASUREMENT DRIVER ONLY (the window exists solely under
        // HALO_P14_REFERENCE=1): a plain static XAML window presents its first
        // frame and then goes fully quiescent — CompositionTarget.Rendering
        // fires zero times (observed). A forever no-op Storyboard (invisible
        // 0.99 → 1.0 opacity pulse) was the first attempt to keep the
        // compositor presenting. NOTE (observed, Pass 14): it does NOT wake
        // the loop — the storyboard reports Active yet zero Rendering
        // callbacks arrive; only a fresh UI-thread invalidation produces a
        // single frame. The storyboard is retained (not removed) as part of
        // the deliberate bisect so the finding is reproducible. No visible
        // change; nothing of this exists in the normal Halo Bar path.
        var anim = new DoubleAnimation
        {
            From = 0.99,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(anim, root);
        Storyboard.SetTargetProperty(anim, "Opacity");
        _sustainStoryboard.Children.Add(anim);
        _sustainStoryboard.Begin();

        // UI-thread invalidation driver. Empirically (Pass 14): a compositor-only
        // XAML animation does NOT wake CompositionTarget.Rendering for a plain
        // window (Storyboard state=Active yet 0 callbacks), while a fresh
        // UI-thread invalidation produces exactly one frame. To observe the
        // DELIVERY cadence the compositor gives this window, a fast UI-thread
        // timer requests layout each tick — the classic WinUI custom-render-loop
        // pattern. Measurement driver only; the window exists solely under
        // HALO_P14_REFERENCE=1 and no visual state changes.
        _sustainTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sustainTimer.Interval = TimeSpan.FromMilliseconds(4);
        _sustainTimer.IsRepeating = true;
        _sustainTimer.Tick += (_, _) => root.InvalidateArrange();
        _sustainTimer.Start();
    }

    /// <summary>Exposed for the reference probe's state diagnostics.</summary>
    public Storyboard SustainStoryboard => _sustainStoryboard;

    /// <summary>
    /// One-time plain placement: 400×300 DIP (× scale) at a normal desktop
    /// location, away from the taskbar strip. Called once BEFORE Activate (so
    /// styles can apply while hidden, like Halo Bar); no further geometry work
    /// happens during measurement.
    /// </summary>
    public void Place()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        double scale = GetDpiForWindow(hwnd) / 96.0;
        int w = (int)Math.Round(400 * scale);
        int h = (int)Math.Round(300 * scale);
        appWindow.MoveAndResize(new RectInt32(240, 160, w, h));

        // Pass 14 bisect: HALO_P14_REFERENCE=2 applies Halo Bar's window
        // configuration (styles + DWM attributes + always-on-top presenter + the
        // 150 ms z-order guard) to the reference window, to identify which
        // property turns on the continuous 60 Hz Rendering stream Halo Bar
        // receives.
        if (Environment.GetEnvironmentVariable("HALO_P14_REFERENCE") == "2")
            ApplyHaloStyleConfig(hwnd, appWindow);
    }

    private void ApplyHaloStyleConfig(IntPtr hwnd, Microsoft.UI.Windowing.AppWindow appWindow)
    {
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

        Helpers.Logger.Info("[MOTION-P14] STYLED mode=haloStyle (styles + DWM + topmost + z-guard 150ms)");
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
