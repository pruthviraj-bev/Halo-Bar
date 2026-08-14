using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace DynamicIsland.Helpers;

/// <summary>
/// Pass 10 frame-pacing forensics (temporary, instrumentation-only — no
/// animation/geometry/easing behavior is modified; the sole exception is
/// <see cref="FreshV0"/>, which sets the production ease-out initial
/// velocity — Pass 20 jitter fix).
///
/// Collects per-frame samples of the window-geometry animation into a fixed
/// in-memory ring buffer (NO per-frame log writes — the normal synchronous
/// logger could itself perturb timing), then computes and dumps aggregate
/// frame-pacing + geometry-cost statistics once the animation settles (or is
/// interrupted by a retarget).
///
/// Debug-only diagnostic switch (read once from environment variables at
/// startup so the mode can be exercised from the same build):
///   HALO_P10_NOCHOREO=1 → DisableContentChoreography (geometry-only mode)
/// </summary>
public static class MotionDiagnostics
{
    public const int MaxSamples = 1000;

    public static readonly bool DisableContentChoreography =
        Environment.GetEnvironmentVariable("HALO_P10_NOCHOREO") == "1";

    /// <summary>Pass 12: HALO_NOOWNER=1 skips GWLP_HWNDPARENT taskbar ownership
    /// (diagnostic isolation of the composition rate — the window then inherits
    /// no taskbar composition path). No behavior change by default.</summary>
    public static readonly bool DisableTaskbarOwnership =
        Environment.GetEnvironmentVariable("HALO_NOOWNER") == "1";

    /// <summary>
    /// Pass 13: HALO_P13_YOFFSET (DIPs) displaces the window's final Y position
    /// vertically so the composition cadence can be measured with the HWND off
    /// the taskbar strip (taskbar-region position forensics). Only the final
    /// diagnostic Y is changed — width/height/X-anchor/easing/duration/geometry
    /// math are untouched. OFF by default: unset → no offset, no P13 logging.
    /// Presence of the variable (even "0", the control) enables the
    /// [MOTION-P13] POSITION/REGION logs for the A/B run.
    /// </summary>
    public static readonly bool EnableP13 =
        Environment.GetEnvironmentVariable("HALO_P13_YOFFSET") != null;

    public static readonly int PositionYOffsetDips =
        int.TryParse(Environment.GetEnvironmentVariable("HALO_P13_YOFFSET"), out int v) ? v : 0;

    // ── Pass 47B — OLE drag routing diagnostics (all OFF by default) ───────
    //   HALO_DRAG_ROUTE=1        continuous [DRAG-ROUTE] hit-test logger in
    //                            WindowService (50 ms), active only while the
    //                            cursor is over the Halo envelope — proves which
    //                            HWND Windows reports under the cursor during a
    //                            real Explorer drag.
    //   HALO_OLE_MAIN_ONLY=1     register the IDropTarget ONLY on the main Halo
    //                            HWND (OLE routing A/B test).
    //   HALO_OLE_BRIDGE_ONLY=1   register the IDropTarget ONLY on the
    //                            DesktopChildSiteBridge (OLE routing A/B test).
    //                            Default (neither): both, as in production.
    public static readonly bool EnableDragRoute = EnvFlag("HALO_DRAG_ROUTE");
    public static readonly bool OleMainOnly = EnvFlag("HALO_OLE_MAIN_ONLY");
    public static readonly bool OleBridgeOnly = EnvFlag("HALO_OLE_BRIDGE_ONLY");

    //   Pass 47B STEP 6 (temporary, OFF by default — do NOT ship enabled):
    //   HALO_OLE_CLEAR_LAYERED=1      clear WS_EX_LAYERED on the main Halo HWND
    //                                  at arm time (per-pixel alpha probe).
    //   HALO_OLE_CLEAR_NOACTIVATE=1    clear WS_EX_NOACTIVATE on the main Halo
    //                                  HWND at arm time (focus-steal probe).
    //   Proves whether either extended style blocks OLE routing to the HWND.
    public static readonly bool OleClearLayered = EnvFlag("HALO_OLE_CLEAR_LAYERED");
    public static readonly bool OleClearNoActivate = EnvFlag("HALO_OLE_CLEAR_NOACTIVATE");

    // ── Pass 48 — final drag hit-test REGION forensics (OFF by default) ─────
    //   HALO_DRAG_REGION=1   extend the [DRAG-ROUTE] tick with a live
    //                        [DRAG-REGION] dump (MainWindow.DescribeRegionHitTest):
    //                        the LIVE GetWindowRgn type/box (vs the assumed region
    //                        bookkeeping), the VISIBLE XAML pill rect (rendered via
    //                        TransformToVisual — never assumed to equal the region),
    //                        the assumed pill rect, dashboard rect, hwnd rect,
    //                        cursor point, live PtInRegion, point-in-visible-pill,
    //                        and the Halo-vs-taskbar z-order. Proves whether the
    //                        native region actually covers the visible pill.
    public static readonly bool EnableDragRegion = EnvFlag("HALO_DRAG_REGION");

    //   HALO_DRAG_AUTOPROBE=1   one-shot OLE hit-test probe (requires
    //                           HALO_DRAG_ROUTE=1 and HALO_DRAG_REGION=1 for the
    //                           full picture): moves the cursor to the compact
    //                           pill center, waits ~800 ms, logs a [DRAG-PROBE]
    //                           line (WindowFromPoint + the full [DRAG-REGION]
    //                           dump) at the pill, then at a control point beside
    //                           it, then restores the cursor. Guarantees an
    //                           inside-pill sample without manual hovering.
    public static readonly bool EnableDragAutoProbe = EnvFlag("HALO_DRAG_AUTOPROBE");

    /// <summary>
    /// Pass 14: HALO_P14_REFERENCE=1 launches ONLY a plain reference window
    /// (no Halo Bar machinery — no WindowService, ownership, acrylic, styles or
    /// widgets) and measures the CompositionTarget.Rendering delivery cadence an
    /// ordinary WinUI 3 window receives on this machine. OFF by default.
    /// </summary>
    public static readonly bool EnableP14Reference =
        Environment.GetEnvironmentVariable("HALO_P14_REFERENCE") is "1" or "2";

    // ── Pass 15 — continuous-presentation / render-sustainer forensics ─────
    // Environment-gated (all OFF by default → byte-identical production):
    //   HALO_P15_PROBE=1     inject a tiny XAML rect into the real Halo window
    //                        and drive a visible rotation from inside every
    //                        CompositionTarget.Rendering callback (10 s) —
    //                        measures whether Halo's presentation path can
    //                        exceed ~60 Hz when forced to do continuous visible
    //                        UI-thread work.
    //   HALO_P15_CONTROL=1..4 launch ONLY a ControlWindow (opaque plain /
    //                        opaque+styles / transparent+styles /
    //                        transparent+styles+acrylic) with the same per-frame
    //                        visual-driver loop — the machine-level delivery
    //                        control Pass 14's quiescent reference could not
    //                        provide.
    //   HALO_P15_MINIMAL=1   launch ONLY the Halo window content (transparent
    //                        root + acrylic) with none of the runtime.
    //   HALO_P15_NOZGUARD / HALO_P15_NOMOUSE / HALO_P15_NOCOMPACT /
    //   HALO_P15_NOVOLUME=1  disable one idle-sustainer candidate in the full
    //                        app (bisect). Off by default.

    public static readonly bool P15ProbeEnabled = EnvFlag("HALO_P15_PROBE");
    public static readonly bool P15ProbeEarly = EnvFlag("HALO_P15_PROBE_EARLY");
    public static readonly bool P15NoDriver = EnvFlag("HALO_P15_NODRIVER");
    public static readonly bool P15Minimal = EnvFlag("HALO_P15_MINIMAL");
    public static readonly bool P15NoZGuard = EnvFlag("HALO_P15_NOZGUARD");
    public static readonly bool P15NoMouseHook = EnvFlag("HALO_P15_NOMOUSE");
    public static readonly bool P15NoCompactPoll = EnvFlag("HALO_P15_NOCOMPACT");
    public static readonly bool P15NoVolumePoll = EnvFlag("HALO_P15_NOVOLUME");
    public static readonly int P15ControlMode =
        int.TryParse(Environment.GetEnvironmentVariable("HALO_P15_CONTROL"), out int cm) ? cm : 0;
    public static readonly bool EnableP15Control = P15ControlMode >= 1;

    private static bool EnvFlag(string name) => Environment.GetEnvironmentVariable(name) == "1";

    private static double ParseDouble(string? s, double fallback)
        => double.TryParse(s, System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : fallback;

    // ── Pass 38 — rectangle forensics / drag-log switches ──────────────────
    // Environment-gated (all OFF by default → byte-identical production):
    //   HALO_P38_FORENSICS=1  repeat the [P38] window-state dump (Test D) every
    //                         2 s so collapsed/expanded/popup states can be
    //                         compared on the real desktop.
    //   HALO_P38_TESTE=1       bright magenta diagnostic surface (Test E) so a
    //                         dark envelope rectangle is unmistakable on any
    //                         wallpaper.
    //   HALO_P38_DRAGLOG=1     verbose [DRAG] instrumentation in PillDashboard
    //                         (full format list every DragOver).
    public static readonly bool EnableP38Forensics = EnvFlag("HALO_P38_FORENSICS");
    public static readonly bool EnableP38TestE = EnvFlag("HALO_P38_TESTE");
    public static readonly bool EnableP38DragLog = EnvFlag("HALO_P38_DRAGLOG");

    // ── Pass 39 — surface-layer A/B isolation + drag hit-test forensics ────
    // Environment-gated (all OFF by default → byte-identical production):
    //   HALO_P39_NO_BACKDROP=1  MODE A: disable ONLY the window-level system
    //                           backdrop. XAML content, WS_EX_LAYERED, region
    //                           and presenter config are all kept — the
    //                           DesktopAcrylicController branch is skipped and
    //                           the shaped in-app brush stays (it IS the XAML
    //                           content surface). Purpose: prove whether the
    //                           window-level DWM material owns the black
    //                           rectangle pixels.
    //   HALO_P39_NO_LAYERED=1   MODE B: disable ONLY WS_EX_LAYERED. Everything
    //                           else unchanged. Purpose: prove whether the
    //                           layered style / per-pixel-alpha path owns the
    //                           rectangle.
    //   HALO_P39_RAW_WINDOW=1   MODE C: disable BOTH the system backdrop and
    //                           WS_EX_LAYERED (backdrop → shaped in-app brush).
    //   HALO_P39_SURFACE=1      repeat the [P39-SURFACE] dump every 2 s so the
    //                           collapsed/expanded/popup states can be compared.
    //   HALO_P39_DRAGLOG=1      verbose [P39-DRAG] per-DragOver logging.
    // None of these modes are intended to ship — they exist only to attribute
    // the rectangular pixels to a mechanism on the user's desktop.
    public static readonly bool EnableP39NoBackdrop = EnvFlag("HALO_P39_NO_BACKDROP");
    public static readonly bool EnableP39NoLayered = EnvFlag("HALO_P39_NO_LAYERED");
    public static readonly bool EnableP39RawWindow = EnvFlag("HALO_P39_RAW_WINDOW");
    public static readonly bool EnableP39SurfaceDump = EnvFlag("HALO_P39_SURFACE");
    public static readonly bool EnableP39DragLog = EnvFlag("HALO_P39_DRAGLOG");
    public static readonly bool EnableP39AnySurfaceMode =
        EnableP39NoBackdrop || EnableP39NoLayered || EnableP39RawWindow || EnableP39SurfaceDump;

    // ── Pass 40 — black-rectangle PIXEL/source forensics (diagnostic only) ─
    // P39's A/B toggles (backdrop / layered / both) all left the rectangle
    // present on the real desktop, so the mechanism class is NOT one of those
    // single flags. Pass 40 stops toggling flags and instead PROVES which
    // surface paints the pixels: real desktop GetPixel scans, HWND attribution,
    // and two all-or-nothing content tests.
    // Environment-gated (all OFF by default → byte-identical production):
    //   HALO_P40_AUDIT=1            [P40-XAML] dump the live visual-tree surface
    //                               state (element/bounds/background/border/
    //                               corner/shadow/visibility) for the top-level
    //                               surfaces.
    //   HALO_P40_PIXEL_FORENSICS=1  [P40-SCAN] real desktop pixel reads (GDI
    //                               GetPixel) along scanlines through the pill
    //                               and envelope, classified by which surface
    //                               owns each pixel (pill/region/window/desktop);
    //                               plus [P40-HIT] WindowFromPoint attribution
    //                               and [P40-HWNDS] a full top-level-window
    //                               census of this process. Re-samples on
    //                               compact→expanded state changes.
    //   HALO_P40_NUKE_XAML=1        make EVERY XAML surface in the visual tree
    //                               completely transparent (backgrounds, borders,
    //                               shadows, corner radius) + no backdrop. The
    //                               binary test: rectangle present ⇒ outside the
    //                               XAML tree; absent ⇒ inside it.
    //   HALO_P40_NO_WINDOW_CONTENT=1 replace Window.Content with a transparent
    //                               minimal Grid; HWND geometry + region + hover
    //                               machinery stay alive. The binary test:
    //                               rectangle present ⇒ window/compositor/
    //                               non-client/another-HWND; absent ⇒ current
    //                               visual tree.
    // None ship; they exist only to identify the exact painting source.
    public static readonly bool EnableP40Audit = EnvFlag("HALO_P40_AUDIT");
    public static readonly bool EnableP40PixelForensics = EnvFlag("HALO_P40_PIXEL_FORENSICS");
    public static readonly bool EnableP40NukeXaml = EnvFlag("HALO_P40_NUKE_XAML");
    public static readonly bool EnableP40NoWindowContent = EnvFlag("HALO_P40_NO_WINDOW_CONTENT");
    public static readonly bool EnableP40Any =
        EnableP40Audit || EnableP40PixelForensics || EnableP40NukeXaml || EnableP40NoWindowContent;

    // ── Pass 41 — black-rectangle PIXEL-OWNER forensics (diagnostic only) ──
    // Pass 40's flags were deliberately NOT toggled again. Pass 41 answers the
    // one open question — "which HWND produces the black pixels?" — by
    // attributing EVERY sampled rectangle pixel to its exact owner:
    //   HALO_P41_OWNER_FORENSICS=1
    //     [P41-SAMPLE]  named sample points (rectangle center / edge midpoints /
    //                   just-inside / just-outside / the 4 envelope corners /
    //                   far outside) each logged as coord + RGB + owner class
    //                   (haloMain / haloOther / haloHelperWinUI / otherApp /
    //                   dwm / desktop) + hwnd + root + class + pid.
    //     [P41-HWNDS]   full process census INCLUDING child windows
    //                   (EnumChildWindows), each logged with its overlap vs the
    //                   suspicious rectangle.
    //     [P41-OWNER]   PIXEL_OWNER_MATCH=… — the candidate rect (mainWindowRect /
    //                   clientRect / extendedFrame / region / pill / a sibling
    //                   Halo HWND) that contains the anomalous-dark bounding
    //                   box, plus the dominant sample owner and the CONCLUSION
    //                   line naming the pixel producer.
    // All OFF by default → byte-identical production. No rendering/geometry/
    // interaction change of any kind.
    public static readonly bool EnableP41OwnerForensics = EnvFlag("HALO_P41_OWNER_FORENSICS");

    // ── Pass 42 — black-rectangle FINAL OWNER isolation (diagnostic only) ──
    // P41's samples were taken OUTSIDE the Halo HWND, so its owners were
    // Chrome/taskbar pixels, not Halo's. Pass 42 (a) fixes the coordinate
    // system — every sample INSIDE GetWindowRect(haloHwnd) only, any sample
    // outside marked INVALID and excluded from owner classification — and
    // (b) runs three hard binary tests that each eliminate one layer of the
    // stack (XAML content → DesktopChildSiteBridge → DWM non-client).
    // Environment-gated (all OFF by default → byte-identical production):
    //   HALO_P42_PIXEL_OWNER=1        [P42-PIXEL] corrected in-bounds pixel
    //                                 sampling + automatic black-rectangle
    //                                 bbox detection + per-pixel owner
    //                                 (ownerHwnd/rootHwnd/childHwnd/class/
    //                                 insideHaloRegion/insideClient/
    //                                 insideVisibleShape) vs a wallpaper ref
    //                                 taken just outside the window.
    //   HALO_P42_EMPTY_CONTENT=1      Window.Content = transparent Grid only;
    //                                 HWND + region stay. Rectangle gone ⇒
    //                                 WinUI/compositor content; remains ⇒
    //                                 outside XAML.
    //   HALO_P42_NO_CONTENT_BRIDGE=1  hide the Microsoft.UI.Content.
    //                                 DesktopChildSiteBridge child of the
    //                                 main HWND (ShowWindow SW_HIDE; nothing
    //                                 destroyed). Rectangle gone ⇒ the bridge/
    //                                 compositor surface; remains ⇒ top-level
    //                                 HWND/DWM/non-client.
    //   HALO_P42_NO_DWM_FRAME=1       apply DWMWA_NCRENDERING_POLICY=DISABLED,
    //                                 CORNER_PREFERENCE=DONOTROUND,
    //                                 BORDER_COLOR=NONE; before/after values
    //                                 logged. Rectangle gone ⇒ DWM non-client.
    // No production behavior changes; diagnosis only.
    public static readonly bool EnableP42OwnerForensics = EnvFlag("HALO_P42_PIXEL_OWNER");
    public static readonly bool EnableP42EmptyContent = EnvFlag("HALO_P42_EMPTY_CONTENT");
    public static readonly bool EnableP42NoContentBridge = EnvFlag("HALO_P42_NO_CONTENT_BRIDGE");
    public static readonly bool EnableP42NoDwmFrame = EnvFlag("HALO_P42_NO_DWM_FRAME");

    // ── Pass 44 — DEFINITIVE pixel differential (halo present vs absent) ──
    // P42/P43's attribution (WindowFromPoint → Chrome_RenderWidgetHostHWND) is
    // only hit-test attribution: because Halo is transparent outside its shaped
    // region, it does NOT prove Chrome paints the pixels. This is the ONE
    // decisive experiment: sample the suspicious envelope INSIDE the Halo HWND,
    // hide the ENTIRE main HWND (ShowWindow SW_HIDE — nothing destroyed, no
    // DWM/region/acrylic/geometry change), DwmFlush + 500 ms, sample the SAME
    // pixels, restore (SW_SHOWNOACTIVATE), sample again. If the dark pixels
    // change when Halo is hidden ⇒ Halo paints them; identical ⇒ they were never
    // Halo's. GDI GetPixel only; fixed in-window coordinates; one outside
    // reference (never an ownership test). Conclusion: [P44-CONCLUSION]
    // halo=<confirmed|notConfirmed|inconclusive>. NO fix in this pass.
    // Environment-gated (OFF by default → byte-identical production):
    //   HALO_P44_PIXEL_DIFFERENTIAL=1
    public static readonly bool EnableP44PixelDifferential = EnvFlag("HALO_P44_PIXEL_DIFFERENTIAL");

    // ── Pass 45 — VISUAL BOUNDARY ISOLATION (diagnostic only) ─────────────
    // P44's result was halo=notConfirmed: every suspicious-dark sample in the
    // 1000×890 envelope EQUALED the wallpaper reference RGB=(24,24,24), and
    // hiding/restoring the whole HWND changed nothing — so the dark rectangle
    // is NOT a Halo-painted surface. Pass 45 stops treating it as one and
    // determines the GEOMETRY of the perceived boundary instead, changing NO
    // HWND architecture, DesktopChildSiteBridge, WS_EX_LAYERED, region, DWM,
    // XAML tree, animation, hover or drag/drop code:
    //   HALO_P45_BOUNDARY_TEST=1
    //   STATE A — normal production surfaces; GDI GetPixel samples of the
    //             suspicious envelope + the pill-region perimeter (±2 px, the
    //             visible-shape seam) + the HWND-envelope perimeter (±2 px,
    //             the window-edge seam) + one outside reference.
    //   STATE B — ALL backdrop/acrylic effects disabled and every Halo surface
    //             transparent (DesktopAcrylicController disposed, Window.Content
    //             swapped to a transparent Grid; nothing else touched). Same
    //             samples: does the boundary survive fully transparent surfaces?
    //   STATE C — Window.Content = full-window solid magenta Grid; the EXISTING
    //             SetWindowRgn region clips it to EXACTLY the visible pill/
    //             dashboard shape. Visually: does a dark seam hug the magenta
    //             shape (shape), the 1000×890 HWND rectangle (envelope), or
    //             neither (desktop)?
    //   Restore production state exactly (Content swapped back, backdrop
    //   re-armed via SetAcrylicBackdrop).
    // Conclusion: [P45-CONCLUSION] boundaryFollows=<shape|envelope|desktop|
    // inconclusive> surfaceDependent=<true|false|inconclusive>. WindowFromPoint
    // and RGB darkness alone are NEVER treated as pixel ownership. NO fix.
    public static readonly bool EnableP45BoundaryTest = EnvFlag("HALO_P45_BOUNDARY_TEST");

    // ── Pass 16 — expand first-frame / dashboard construction forensics ─────
    // Env-gated (OFF by default → byte-identical production):
    //   HALO_P16_AUTOCYCLE=1  App drives deterministic expand/collapse cycles
    //                         over the PRODUCTION click / focus-lost paths
    //                         (expand → +1.2 s collapse ×5), so one session
    //                         yields the cold first expand (dashboard attach +
    //                         first layout), warm repeats, and the collapse
    //                         control.
    //   HALO_P16_WARMUP=1     run one warm-up expand/collapse pair before the
    //                         measured cycles (Mode D: construction + layout
    //                         already done before the first measured expand).
    //   HALO_P16_EMPTY=1      IslandController.EnsureDashboard returns a trivial
    //                         Grid+Rectangle instead of ExpandedDashboard
    //                         (Mode B: isolates dashboard visual/layout cost).
    //   HALO_P16_NODATA=1     ExpandedDashboard keeps its full visual tree but
    //                         skips widget data initialization — clipboard
    //                         population, weather, bluetooth, system stats,
    //                         tasks, and the 1 s timer (Mode C: separates data
    //                         cost from visual/layout cost).
    public static readonly bool P16AutoCycle = EnvFlag("HALO_P16_AUTOCYCLE");
    public static readonly bool P16Warmup = EnvFlag("HALO_P16_WARMUP");
    public static readonly bool P16EmptyDashboard = EnvFlag("HALO_P16_EMPTY");
    public static readonly bool P16NoData = EnvFlag("HALO_P16_NODATA");
    public static readonly bool P16Enabled =
        P16AutoCycle || P16Warmup || P16EmptyDashboard || P16NoData;

    // Pass 17: HALO_P16_FAST=1 drives the auto-cycle at 120 ms steps so
    // expand/collapse retarget mid-animation (rapid-reversal stress) instead of
    // settling between steps; HALO_P17_DISABLE=1 turns OFF the dashboard
    // warm-layout so the cold first-expand path can be A/B measured against
    // the warm path in the same build.
    public static readonly bool P16Fast = EnvFlag("HALO_P16_FAST");
    public static readonly bool P17DisableWarmup = EnvFlag("HALO_P17_DISABLE");

    // Pass 27: HALO_P27_POPUP=1 drives deterministic popup open/close cycles
    // (file-shelf 340, clipboard 180) through the production StartSizeAnimation
    // path so the stable-window pill-growth stage can be validated headlessly.
    public static readonly bool P27PopupTest = EnvFlag("HALO_P27_POPUP");

    // Pass 20 — video-confirmed jitter fix. Screen-recording forensics showed
    // the front-loaded cubic ease-out (v0 = 3) moving the window edges
    // 100-280 px per frame at the ~60 Hz effective cadence (early-phase
    // strobing + post-hold jumps). HALO_V0=1.5..3.0 overrides the
    // fresh-segment initial velocity; the production default 1.8 is a gentler
    // start (~40% smaller early steps) with identical duration, monotonicity,
    // reversal velocity-matching, anchors and choreography. Clamped to [0,3]
    // (the range over which the generalized cubic stays monotonic) so a
    // malformed override cannot break the curve.
    public static readonly double FreshV0 = Math.Clamp(
        ParseDouble(Environment.GetEnvironmentVariable("HALO_V0"), 1.8), 0.0, 3.0);

    // HALO_P16_CYCLES=N overrides the P16 auto-cycle PAIR count so the 50–200
    // cycle stability soak runs in one session (0 = off → the app's default
    // pair count). Test harness only.
    public static readonly int AutoCycleCycles =
        int.TryParse(Environment.GetEnvironmentVariable("HALO_P16_CYCLES"), out int cyc)
            ? Math.Max(0, cyc)
            : 0;

    // Anchor (absolute ms) of the current expand/collapse segment's request,
    // set by P16BeginSegment; every [MOTION-P16] elapsedMs is relative to it.
    public static long P16ExpandStartMs = -1;

    // Set by the auto-cycle driver before each transition so the generic
    // [MOTION-P11] FRAME_STATS dump carries the cold/warm/collapse label.
    public static string P16SegmentTag = "";

    /// <summary>Anchors a new expand/collapse segment and logs the request.</summary>
    public static void P16BeginSegment(string dir)
    {
        if (!P16Enabled) return;
        P16ExpandStartMs = Environment.TickCount64;
        Logger.Info($"[MOTION-P16] {dir}Requested dir={dir} elapsedMs=0 tMs={P16ExpandStartMs} tag={P16SegmentTag}");
    }

    /// <summary>Logs a [MOTION-P16] lifecycle marker with elapsed-from-request.</summary>
    public static void P16Mark(string name, string dir = "", string extra = "")
    {
        if (!P16Enabled) return;
        long now = Environment.TickCount64;
        string elapsed = P16ExpandStartMs < 0 ? "n/a" : (now - P16ExpandStartMs).ToString();
        string dirPart = string.IsNullOrEmpty(dir) ? "" : $" dir={dir}";
        string extraPart = string.IsNullOrEmpty(extra) ? "" : $" {extra}";
        Logger.Info($"[MOTION-P16] {name}{dirPart} elapsedMs={elapsed} tMs={now}{extraPart}");
    }

    /// <summary>Pass 17 one-shot marker for the dashboard warm-layout lifecycle.</summary>
    public static void P17Log(string message)
        => Logger.Info($"[MOTION-P17] {message}");

    /// <summary>True while an animation segment is being sampled.</summary>
    public static bool IsSampling { get; private set; }

    // Content state published by MainWindowViewModel each choreography frame so
    // every sample carries the opacity/scale the user actually saw.
    public static double DashboardOpacity;
    public static double PillOpacity;
    public static double DashboardScale;

    public struct FrameSample
    {
        public long TimestampMs;
        public double FrameDeltaMs;
        public double ElapsedMs;
        public double T;
        public double Eased;
        public int X;
        public int Y;
        public int W;
        public int H;
        public int TargetW;
        public int TargetH;
        public double DashOpacity;
        public double PillOpacityValue;
        public double DashScale;
        // Pass 11: whether THIS frame's geometry application actually called
        // MoveAndResize (written back by RecordGeometryCall) and its sync cost.
        public bool MoveResized;
        public double MoveMs;
    }

    private static readonly FrameSample[] _samples = new FrameSample[MaxSamples];
    private static int _count;
    private static long _lastFrameMs;
    private static string _dir = "";
    private static int _applyCalls;      // ApplyGeometry invocations during sampling
    private static int _moveResized;     // MoveAndResize calls that actually ran
    private static readonly List<double> _geomTimes = new(128);
    private static double _geomTotalMs;
    private static double _geomMaxMs;

    /// <summary>Starts a fresh sample buffer for a new segment. Dumps any still
    /// pending previous segment as reason=interrupted first.</summary>
    public static void BeginSegment(string dir)
    {
        if (IsSampling) Dump("interrupted");
        _dir = dir;
        _count = 0;
        _lastFrameMs = Environment.TickCount64;
        _applyCalls = 0;
        _moveResized = 0;
        _geomTimes.Clear();
        _geomTotalMs = 0;
        _geomMaxMs = 0;
        IsSampling = true;
    }

    /// <summary>Records one applied animation frame (called from OnRendering,
    /// after the eased geometry is computed).</summary>
    public static void RecordFrame(long nowMs, long segmentStartMs, double t, double eased,
        int x, int y, int w, int h, int targetW, int targetH)
    {
        if (_count >= MaxSamples) return; // cap — never wrap (animation ≪ 1000 frames)

        long delta = _count == 0 ? 0 : nowMs - _lastFrameMs;
        _lastFrameMs = nowMs;

        var s = new FrameSample
        {
            TimestampMs = nowMs,
            FrameDeltaMs = delta,
            ElapsedMs = nowMs - segmentStartMs,
            T = t,
            Eased = eased,
            X = x, Y = y, W = w, H = h,
            TargetW = targetW, TargetH = targetH,
            DashOpacity = DashboardOpacity,
            PillOpacityValue = PillOpacity,
            DashScale = DashboardScale,
        };
        _samples[_count++] = s;

        // Live compact hitch marker (16.67 ms threshold). Aggregate stats are
        // dumped at settle; this line pinpoints the exact hitch frame.
        if (delta > 16.67)
        {
            Logger.Info($"[MOTION-P10] HITCH mode={ModeLabel()} dir={_dir} elapsedMs={(int)s.ElapsedMs} frameDeltaMs={delta:F1} pct={(int)(eased * 100)} eased={eased:F3} width={w} height={h}");
        }
    }

    /// <summary>Records one ApplyGeometry call (timed) — called from ApplyGeometry.
    /// Writes the resize flag back into the most recent frame sample (called
    /// after RecordFrame for the same frame) so gap correlation can attribute
    /// each frame gap to a native resize.</summary>
    public static void RecordGeometryCall(double applyMs, bool resized, double moveMs)
    {
        LastFrameResized = resized;
        LastFrameMoveMs = moveMs;
        if (!IsSampling) return;
        _applyCalls++;
        if (_count > 0)
        {
            _samples[_count - 1].MoveResized = resized;
            _samples[_count - 1].MoveMs = moveMs;
        }
        if (resized)
        {
            _moveResized++;
            _geomTimes.Add(moveMs);
            _geomTotalMs += moveMs;
            if (moveMs > _geomMaxMs) _geomMaxMs = moveMs;
        }
    }

    /// <summary>Computes + logs aggregate frame-pacing and geometry statistics.</summary>
    public static void EndSegment(string reason)
    {
        if (!IsSampling) return;
        Dump(reason);
    }

    private static string ModeLabel()
        => DisableContentChoreography ? "geomOnly" : "full";

    private static void Dump(string reason)
    {
        IsSampling = false;
        if (_count == 0) return;

        string mode = ModeLabel();

        // Frame pacing — deltas excluding the first (setup) frame. The display
        // is 144 Hz, so the thresholds are 144 Hz frame multiples:
        //   1 frame ≈ 6.94 ms, 2 ≈ 13.89 ms, 3 ≈ 20.83 ms, 5 ≈ 33.33 ms, 7 ≈ 48.61 ms
        var deltas = new List<double>(_count);
        double total = 0;
        double maxDelta = 0;
        int h7 = 0, h14 = 0, h21 = 0, h33 = 0, h50 = 0;
        for (int i = 1; i < _count; i++)
        {
            double d = _samples[i].FrameDeltaMs;
            if (d <= 0) continue;
            deltas.Add(d);
            total += d;
            if (d > maxDelta) maxDelta = d;
            if (d > 50) h50++;
            if (d > 33.33) h33++;
            if (d > 20.83) h21++;
            if (d > 13.89) h14++;
            if (d > 6.94) h7++;
        }

        var sorted = deltas.OrderBy(v => v).ToArray();
        double durationMs = _samples[_count - 1].ElapsedMs;
        double avg = deltas.Count > 0 ? total / deltas.Count : 0;

        Logger.Info(
            $"[MOTION-P11] FRAME_STATS mode={mode} dir={_dir} reason={reason} " +
            $"frames={_count} durationMs={durationMs:F0} avgMs={avg:F2} " +
            $"p50Ms={Percentile(sorted, 0.50):F2} p90Ms={Percentile(sorted, 0.90):F2} " +
            $"p95Ms={Percentile(sorted, 0.95):F2} p99Ms={Percentile(sorted, 0.99):F2} maxMs={maxDelta:F2} " +
            $"h7={h7} h14={h14} h21={h21} h33={h33} h50={h50} " +                $"exp144={durationMs / 6.9444:F1} p16={P16SegmentTag}");

        // Pass 11 Phase 3 — gap correlation: for every frame gap > 2 frames
        // (13.89 ms), record whether the previous and next frames resized, the
        // sync cost, and the geometry delta across the gap.
        for (int i = 1; i < _count; i++)
        {
            double d = _samples[i].FrameDeltaMs;
            if (d <= 13.89) continue;
            var prev = _samples[i - 1];
            var cur = _samples[i];
            Logger.Info(
                $"[MOTION-P11] GAP gapMs={d:F1} beforeResize={(prev.MoveResized ? "YES" : "no")} " +
                $"afterResize={(cur.MoveResized ? "YES" : "no")} " +
                $"syncCostMs={Math.Max(prev.MoveMs, cur.MoveMs):F2} " +
                $"dW={Math.Abs(cur.W - prev.W)} dH={Math.Abs(cur.H - prev.H)} " +
                $"elapsedMs={(int)cur.ElapsedMs} pct={(int)(cur.Eased * 100)}");
        }

        // Geometry cost. Note: MoveAndResize returns before the compositor/DWM
        // finishes the resize, so moveMs under-reports true per-frame cost —
        // the FRAME_STATS deltas are the ground truth for pacing.
        double gAvg = _geomTimes.Count > 0 ? _geomTotalMs / _geomTimes.Count : 0;
        double gP95 = _geomTimes.Count > 0 ? Percentile(_geomTimes.OrderBy(v => v).ToArray(), 0.95) : 0;
        Logger.Info(
            $"[MOTION-P10] GEOMETRY_STATS mode={mode} dir={_dir} " +
            $"applyCalls={_applyCalls} moveResized={_moveResized} " +
            $"avgMs={gAvg:F3} p95Ms={gP95:F3} maxMs={_geomMaxMs:F3}");
    }

    private static double Percentile(double[] sorted, double p)
    {
        int count = sorted.Length;
        if (count == 0) return 0;
        int idx = (int)Math.Ceiling(p * count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= count) idx = count - 1;
        return sorted[idx];
    }

    // ── Pass 11.5 — real-interaction render-cadence probe (debug-only) ─────
    // Environment HALO_P11_5=1 enables a passive observer of
    // CompositionTarget.Rendering that measures the RAW cadence while the
    // window is IDLE-COMPACT and STATIC-EXPANDED — the two states the
    // per-animation ring buffer cannot see (the animation loop only runs
    // during transitions). Pure observation: it never touches geometry,
    // opacity, or animation state. The DispatcherQueue timers below are PROBE
    // SCHEDULING ONLY — no animation is driven by them.

    // WindowService publishes these every rendering frame so the probe can
    // correlate each frame gap with native resize and animation state.
    public static bool AnimatingFlag;
    public static int WinWFlag;
    public static int WinHFlag;
    public static bool LastFrameResized;
    public static double LastFrameMoveMs;

    /// <summary>Starts the cadence probe if HALO_P11_5=1 (UI thread). No-op otherwise.</summary>
    public static void EnableCadenceProbe()
    {
        if (!CadenceProbe.EnabledByEnv()) return;
        CadenceProbe.Enable();
    }

    /// <summary>Reorders the cadence probe after the animation's Rendering handler
    /// (called by WindowService each time it subscribes). No-op unless enabled.</summary>
    public static void OnAnimationSubscribed()
        => CadenceProbe.OnAnimationSubscribed();

    private static class CadenceProbe
    {
        private const int MaxSamples = 8192;
        private const long IdleWindowStartMs = 2500;  // Test A: idle compact, untouched
        private const long IdleWindowEndMs = 7500;
        private const long StaticWindowMs = 5000;     // Test B: 5 s after the first expand settles
        private const long FinalDumpMs = 43000;       // Test C: animation aggregate at end
        // Expanded profile height is 664 DIPs → ≥664 physical px at any scale;
        // compact is ~48-96 px. 300 cleanly separates the two, so compact-width
        // adaptations can never masquerade as a dashboard settle.
        private const int ExpandedHeightPxThreshold = 300;

        private struct Sample
        {
            public long T;
            public double Dt;
            public bool Anim;
            public int W, H;
            public bool Resized;
            public double MoveMs;
        }

        private static readonly Sample[] _samples = new Sample[MaxSamples];
        private static int _count;
        private static long _lastT;
        private static bool _enabled;
        private static int _idleStartIndex = -1;
        private static int _staticStartIndex = -1;
        private static bool _armedSettleDetect;
        // All probe timers are rooted in fields: DispatcherQueueTimer is not
        // retained by the queue itself and an unreferenced one can be GC'd
        // before it fires (the +43 s Animation dump was observed missing).
        private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _markTimer;
        private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleEndTimer;
        private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _staticDumpTimer;
        private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _finalTimer;

        public static bool EnabledByEnv()
            => Environment.GetEnvironmentVariable("HALO_P11_5") == "1";

        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

            _markTimer = OneShot(dq, IdleWindowStartMs, () => { _idleStartIndex = _count; });
            _idleEndTimer = OneShot(dq, IdleWindowEndMs, () =>
            {
                DumpRange("IdleCompact", _idleStartIndex, _count);
                _armedSettleDetect = true; // next animation settle starts the static window
            });
            _finalTimer = OneShot(dq, FinalDumpMs, () =>
            {
                DumpAnimation();
                _enabled = false;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            });
            Logger.Info("[MOTION-P11.5] PROBE enabled (idle=2.5-7.5s static=settle+5s final=43s)");
        }

        /// <summary>
        /// Called by WindowService immediately after it subscribes its animation
        /// handler. CompositionTarget.Rendering invokes handlers in subscription
        /// order, and the animation handler re-subscribes for every segment — so
        /// the probe must be re-appended to the invocation list each time to run
        /// AFTER it and read the CURRENT frame's Anim/W/H/resize state instead of
        /// the stale values the animation handler published on frame N-1.
        /// (A probe that ran first would also never see Anim=true on the settle
        /// frame and would mis-attribute the first post-settle frame.)
        /// </summary>
        public static void OnAnimationSubscribed()
        {
            if (!_enabled) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        }

        private static Microsoft.UI.Dispatching.DispatcherQueueTimer OneShot(
            Microsoft.UI.Dispatching.DispatcherQueue dq, long ms, Action act)
        {
            var t = dq.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.IsRepeating = false;
            t.Tick += (_, _) => act();
            t.Start();
            return t;
        }

        private static void OnRendering(object? sender, object e)
        {
            long now = Environment.TickCount64;
            // Record EVERY delivered frame (the first one is the delta baseline
            // with Dt=0 and is skipped by the stats). NOTE: the gate must not
            // depend on _count being > 0 — the original `_count > 0 &&` guard
            // kept _count at 0 forever, so no sample was ever recorded.
            if (_count < MaxSamples)
            {
                _samples[_count] = new Sample
                {
                    T = now,
                    Dt = _count == 0 ? 0 : now - _lastT,
                    Anim = AnimatingFlag,
                    W = WinWFlag,
                    H = WinHFlag,
                    Resized = AnimatingFlag && LastFrameResized,
                    MoveMs = AnimatingFlag ? LastFrameMoveMs : 0,
                };
                _count++;
            }
            _lastT = now;

            // Settle detection: a frame where Anim went true -> false means a
            // transition just ended; measure the following static window. Gated
            // on the post-settle frame being EXPANDED height (compact-width
            // adaptations also set Anim=true and must never start the static
            // window measurement).
            if (_armedSettleDetect && _staticStartIndex < 0
                && _count >= 2 && _samples[_count - 2].Anim && !_samples[_count - 1].Anim
                && _samples[_count - 1].H > ExpandedHeightPxThreshold)
            {
                _staticStartIndex = _count - 1;
                _armedSettleDetect = false;
                var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                _staticDumpTimer = OneShot(dq, StaticWindowMs, () =>
                {
                    DumpRange("StaticExpanded", _staticStartIndex, _count);
                    _staticStartIndex = -2; // dump once
                });
            }
        }

        private static void DumpRange(string tag, int startIdx, int endIdx)
            => DumpStats(tag, startIdx, endIdx, filterAnim: false);

        private static void DumpAnimation()
            => DumpStats("Animation", 0, _count, filterAnim: true);

        private static void DumpStats(string tag, int startIdx, int endIdx, bool filterAnim)
        {
            var rows = new List<(double dt, Sample s)>();
            for (int i = Math.Max(0, startIdx); i < endIdx && i < _count; i++)
            {
                if (_samples[i].Dt <= 0) continue;
                if (filterAnim && !_samples[i].Anim) continue;
                rows.Add((_samples[i].Dt, _samples[i]));
            }
            if (rows.Count == 0)
            {
                Logger.Info($"[MOTION-P11.5] CADENCE tag={tag} samples=0 (no frames delivered in window)");
                return;
            }

            var deltas = rows.Select(r => r.dt).OrderBy(v => v).ToArray();
            double minMs = deltas[0];
            double maxMs = deltas[deltas.Length - 1];
            double p50 = Percentile(deltas, 0.50);
            double p95 = Percentile(deltas, 0.95);
            double p99 = Percentile(deltas, 0.99);
            int b8 = 0, b14 = 0, b20 = 0, b33 = 0, b33p = 0;
            foreach (double d in deltas)
            {
                if (d < 8) b8++;
                else if (d < 13.89) b14++;
                else if (d < 20) b20++;
                else if (d <= 33.33) b33++;
                else b33p++;
            }
            double dur = rows[rows.Count - 1].s.T - rows[0].s.T;
            Logger.Info(
                $"[MOTION-P11.5] CADENCE tag={tag} samples={rows.Count} durMs={dur:F0} " +
                $"minMs={minMs:F2} p50Ms={p50:F2} p95Ms={p95:F2} p99Ms={p99:F2} maxMs={maxMs:F2} " +
                $"b8={b8} b14={b14} b20={b20} b33={b33} b33p={b33p}");

            // Resize correlation: every gap > 13.89 ms with the state of the
            // previous and current frame (resize, sync cost, anim, dimensions).
            // Only emitted for the Animation window — in the Idle/Static windows
            // every 16 ms frame exceeds 13.89 ms, so emitting per-gap lines there
            // would spam the log with ~300 identical rows (Pass 11.5 finding).
            if (tag == "Animation")
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].dt <= 13.89) continue;
                    var cur = rows[i];
                    var prev = i > 0 ? rows[i - 1] : cur;
                    Logger.Info(
                        $"[MOTION-P11.5] GAP tag={tag} dtMs={cur.dt:F1} " +
                        $"prevResize={(prev.s.Resized ? "YES" : "no")} curResize={(cur.s.Resized ? "YES" : "no")} " +
                        $"prevMoveMs={prev.s.MoveMs:F2} curMoveMs={cur.s.MoveMs:F2} " +
                        $"anim={(cur.s.Anim ? "YES" : "no")} w={cur.s.W} h={cur.s.H}");
                }
            }
        }
    }

    // ── Refresh-rate confirmation ──────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public uint dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT pRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    /// <summary>
    /// Logs the primary monitor's current refresh rate once at startup so the
    /// frame-pacing thresholds can be interpreted correctly (144 Hz → 6.94 ms).
    /// </summary>
    public static void LogRefreshRate()
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, -1, ref dm)) // ENUM_CURRENT_SETTINGS = -1
            Logger.Info($"[MOTION-P11] REFRESH hz={dm.dmDisplayFrequency} res={dm.dmPelsWidth}x{dm.dmPelsHeight}");
        else
            Logger.Info("[MOTION-P11] REFRESH hz=unknown");
    }

    // ── Pass 14 — reference-window / DWM presentation cadence (debug-only) ──
    // HALO_P14_REFERENCE=1: App.OnLaunched launches ONLY a plain ReferenceWindow
    // and calls ReferenceProbe.Start(). The probe measures the raw
    // CompositionTarget.Rendering delivery cadence of that ordinary window for
    // 10 static seconds, dumps aggregate stats, then closes the window (which
    // ends the app). Pure observation of the delivery rate — no geometry,
    // opacity, or animation state is touched. The per-frame InvalidateArrange is
    // a MEASUREMENT DRIVER only (a fully static XAML window goes quiescent and
    // fires no Rendering callbacks at all) — the same mechanism WindowService
    // already uses to keep deduped animation frames alive; it changes no visual
    // state.

    /// <summary>Logs refresh rate, DPI/scale, display bounds and WinUI version.</summary>
    public static void LogP14Environment(Microsoft.UI.Xaml.Window? window)
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, -1, ref dm)) // ENUM_CURRENT_SETTINGS = -1
            Logger.Info($"[MOTION-P14] REFRESH hz={dm.dmDisplayFrequency} res={dm.dmPelsWidth}x{dm.dmPelsHeight}");
        else
            Logger.Info("[MOTION-P14] REFRESH hz=unknown");

        if (window != null)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            uint dpi = GetDpiForWindow(hwnd);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var da = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            Logger.Info($"[MOTION-P14] SCALE dpi={dpi} scale={dpi / 96.0:F2}");
            Logger.Info($"[MOTION-P14] DISPLAY bounds={da.OuterBounds.X},{da.OuterBounds.Y},{da.OuterBounds.Width}x{da.OuterBounds.Height} workarea={da.WorkArea.X},{da.WorkArea.Y},{da.WorkArea.Width}x{da.WorkArea.Height}");
        }
        // The WinUI assembly version is a placeholder-looking 3.0.0.0; the
        // ProductVersion carries the actual Windows App SDK version (e.g.
        // 1.8.260317003).
        var winuiAssembly = typeof(Microsoft.UI.Xaml.Window).Assembly;
        string winuiVersion = winuiAssembly.GetName().Version?.ToString() ?? "unknown";
        try
        {
            string? loc = winuiAssembly.Location;
            if (!string.IsNullOrEmpty(loc) && System.IO.File.Exists(loc))
            {
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(loc);
                if (!string.IsNullOrEmpty(fvi.ProductVersion)) winuiVersion = fvi.ProductVersion;
            }
        }
        catch { /* best effort — fall back to assembly version */ }
        Logger.Info($"[MOTION-P14] WINUI {winuiVersion}");
    }

    public static class ReferenceProbe
    {
        public const int MeasureMs = 10000; // 10 s static measurement window
        private static readonly List<double> _deltas = new(2048);
        private static long _lastT;
        private static bool _active;
        private static bool _haveBaseline;
        private static long _totalCallbacks;
        private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _endTimer;
        private static Microsoft.UI.Xaml.Window? _owner;

        public static void Start(Microsoft.UI.Dispatching.DispatcherQueue dq, Microsoft.UI.Xaml.Window owner)
        {
            if (_active) return;
            _active = true;
            _owner = owner;
            _lastT = Environment.TickCount64;
            _deltas.Clear();
            _haveBaseline = false;
            _totalCallbacks = 0;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            Logger.Info("[MOTION-P14] TEST_START mode=reference");
            LogP14Environment(owner);
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            Logger.Info($"[MOTION-P14] VISIBLE visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)}");
            DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)); // DWMWA_CLOAKED
            Logger.Info($"[MOTION-P14] CLOAKED cloak={cloaked}");
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
                Logger.Info($"[MOTION-P14] PLACEMENT showCmd={wp.showCmd} rect=({wp.rcNormalPosition.Left},{wp.rcNormalPosition.Top},{wp.rcNormalPosition.Right - wp.rcNormalPosition.Left}x{wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top})");
            if (GetWindowRect(hwnd, out RECT rc))
                Logger.Info($"[MOTION-P14] WINDOW rect={rc.Left},{rc.Top},{rc.Right - rc.Left}x{rc.Bottom - rc.Top}");
            // Kick-start the presentation loop: a freshly shown static XAML
            // window presents its first frame and then goes quiescent (no
            // invalidation → no further Rendering callbacks — observed: 0 frames
            // in the first Pass 14 run). Re-invalidating after subscribing gives
            // the loop a start; each subsequent Rendering callback re-invalidates,
            // keeping it self-sustaining while no visual state changes.
            if (owner.Content is Microsoft.UI.Xaml.UIElement el) el.InvalidateArrange();
            _endTimer = OneShot(dq, MeasureMs, End);
        }

        private static Microsoft.UI.Dispatching.DispatcherQueueTimer OneShot(
            Microsoft.UI.Dispatching.DispatcherQueue dq, int ms, Action act)
        {
            var t = dq.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.IsRepeating = false;
            t.Tick += (_, _) => act();
            t.Start();
            return t;
        }

        private static bool _firstFrameLogged;

        private static void OnRendering(object? sender, object e)
        {
            _totalCallbacks++;
            long now = Environment.TickCount64;
            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                Logger.Info($"[MOTION-P14] FIRST_FRAME at {now}");
            }
            double dt = now - _lastT;
            _lastT = now;
            // Same baseline-gate bug fix as P15Probe: `_deltas.Count > 0` never
            // becomes true, so Pass 14's "quiescent" readings were invalid.
            if (_haveBaseline) _deltas.Add(dt);
            _haveBaseline = true;
            // Measurement driver — keep the compositor presenting so the DELIVERY
            // cadence is observable. No visual state is changed.
            (_owner?.Content as Microsoft.UI.Xaml.UIElement)?.InvalidateArrange();
        }

        private static void End()
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            _active = false;
            if (_owner is Views.ReferenceWindow rw)
            {
                try
                {
                    var state = rw.SustainStoryboard.GetCurrentState();
                    Logger.Info($"[MOTION-P14] STORYBOARD state={state}");
                }
                catch (Exception ex)
                {
                    Logger.Info($"[MOTION-P14] STORYBOARD state=error ({ex.Message})");
                }
            }
            Dump();
            Logger.Info("[MOTION-P14] TEST_END mode=reference");
            _owner?.Close();
            Microsoft.UI.Xaml.Application.Current?.Exit();
        }

        private static void Dump()
        {
            if (_deltas.Count == 0)
            {
                Logger.Info($"[MOTION-P14] CADENCE mode=reference samples=0 callbacks={_totalCallbacks} (no deltas)");
                return;
            }
            var sorted = _deltas.Where(d => d > 0).OrderBy(d => d).ToArray();
            if (sorted.Length == 0)
            {
                Logger.Info($"[MOTION-P14] CADENCE mode=reference samples=0 callbacks={_totalCallbacks} (no deltas)");
                return;
            }

            double mean = sorted.Average();
            double variance = sorted.Sum(d => (d - mean) * (d - mean)) / sorted.Length;
            double stddev = Math.Sqrt(variance);
            int b8 = 0, b14 = 0, b16 = 0, b33 = 0;
            foreach (double d in sorted)
            {
                if (d >= 33.33) b33++;
                if (d >= 16.67) b16++;
                if (d >= 13.89) b14++;
                if (d >= 6.94) b8++;
            }
            Logger.Info(
                $"[MOTION-P14] CADENCE mode=reference samples={sorted.Length} durMs={sorted.Sum():F0} " +
                $"p50={Percentile(sorted, 0.50):F2} p95={Percentile(sorted, 0.95):F2} p99={Percentile(sorted, 0.99):F2} " +
                $"max={sorted[sorted.Length - 1]:F2} mean={mean:F2} stddev={stddev:F2} " +
                $"b8={b8} b14={b14} b16={b16} b33={b33}");
        }
    }

    // ── Pass 15 — measurement probe with optional per-frame visual driver ────

    /// <summary>
    /// Pass 15 measurement probe: subscribes CompositionTarget.Rendering on the
    /// UI thread, optionally drives a REAL per-frame visual change (XAML
    /// RenderTransform rotation — a no-op invalidation does NOT sustain the
    /// loop, Pass 14), then dumps [P15] CADENCE stats after the configured
    /// window and exits the app. The visual driver keeps the same UI-thread
    /// render-loop pattern Halo Bar's animation uses, so the measured cadence is
    /// directly comparable. The window itself stays fixed — no geometry changes.
    /// </summary>
    public static class P15Probe
    {
        private static readonly List<double> _deltas = new(8192);
        private static long _lastT;
        private static bool _active;
        private static bool _haveBaseline;
        private static bool _earlySubscribed;
        private static long _preMeasureCallbacks;
        private static long _totalCallbacks;
        private static Microsoft.UI.Xaml.Window? _owner;
        private static Microsoft.UI.Xaml.UIElement? _driver;
        private static string _tag = "";
        private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _endTimer;

        /// <summary>
        /// Pass 15: subscribes the probe handler BEFORE the window's first
        /// present (called from App before Activate). Tests whether an early
        /// CompositionTarget.Rendering subscription itself induces the
        /// continuous ~60 Hz callback pump on this machine — the P11.5 cadence
        /// probe subscribed at startup and streamed 60 Hz; the P15 late probe
        /// saw 0 callbacks. Subscription timing may be the differentiator.
        /// </summary>
        public static void SubscribeEarly()
        {
            if (_earlySubscribed) return;
            _earlySubscribed = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            Logger.Info("[P15] PROBE early subscription installed (before first present).");
        }

        public static void Start(Microsoft.UI.Xaml.Window owner, int measureMs, string tag,
            Microsoft.UI.Xaml.UIElement? visualDriver)
        {
            if (_active) return;
            _active = true;
            _owner = owner;
            if (P15NoDriver) visualDriver = null; // pure observer mode
            _driver = visualDriver;
            _tag = tag;
            _preMeasureCallbacks = _totalCallbacks;
            _lastT = Environment.TickCount64;
            _deltas.Clear();
            _haveBaseline = false;
            if (!_earlySubscribed)
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            Logger.Info($"[P15] PROBE_START tag={tag} seconds={measureMs / 1000.0} driver={(visualDriver != null ? "xaml-rotation" : "none")} early={_earlySubscribed}");
            // Kick-start: a freshly shown window presents its first frame; from
            // then on the visual driver (or an app-side sustainer) keeps the
            // loop alive. A fully static window would go quiescent (Pass 14).
            (owner.Content as Microsoft.UI.Xaml.UIElement)?.InvalidateArrange();
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _endTimer = dq.CreateTimer();
            _endTimer.Interval = TimeSpan.FromMilliseconds(measureMs);
            _endTimer.IsRepeating = false;
            _endTimer.Tick += (_, _) => End();
            _endTimer.Start();
        }

        private static void OnRendering(object? sender, object e)
        {
            _totalCallbacks++;
            if (!_active) return; // pre-measure callbacks are counted, not sampled

            long now = Environment.TickCount64;
            double dt = now - _lastT;
            _lastT = now;
            // NOTE: the baseline gate must NOT be `_deltas.Count > 0` — that
            // never becomes true (the first sample is never added, so the list
            // stays empty forever). Use an explicit flag (this exact bug
            // invalidated Pass 14's "quiescent" readings and the first P15 run).
            if (_haveBaseline) _deltas.Add(dt);
            _haveBaseline = true;

            // Measurement driver: real visible XAML change every frame — the
            // per-frame dirty that keeps the compositor presenting (mirrors the
            // per-frame geometry work Halo Bar's animation does).
            if (_driver != null)
            {
                var ct = _driver.RenderTransform as Microsoft.UI.Xaml.Media.CompositeTransform;
                if (ct == null)
                {
                    ct = new Microsoft.UI.Xaml.Media.CompositeTransform();
                    _driver.RenderTransform = ct;
                }
                ct.Rotation = (ct.Rotation + 1.0) % 360.0;
            }
        }

        private static void End()
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            _active = false;
            Dump();
            Logger.Info($"[P15] PROBE_END tag={_tag}");
            _owner?.Close();
            Microsoft.UI.Xaml.Application.Current?.Exit();
        }

        private static void Dump()
        {
            long measured = _totalCallbacks - _preMeasureCallbacks;
            if (_deltas.Count == 0)
            {
                Logger.Info($"[P15] CADENCE tag={_tag} samples=0 callbacks={measured} preMeasureCallbacks={_preMeasureCallbacks} (no deltas)");
                return;
            }
            var sorted = _deltas.Where(d => d > 0).OrderBy(d => d).ToArray();
            if (sorted.Length == 0)
            {
                Logger.Info($"[P15] CADENCE tag={_tag} samples=0 callbacks={measured} preMeasureCallbacks={_preMeasureCallbacks} (no deltas)");
                return;
            }
            // Same-ms duplicate deliveries (dt == 0): WinUI 3 can invoke the
            // Rendering handler several times within one millisecond per UI
            // frame; only the positive inter-frame deltas represent real frames.
            int sameMs = _deltas.Count - sorted.Length;
            double mean = sorted.Average();
            double variance = sorted.Sum(d => (d - mean) * (d - mean)) / sorted.Length;
            double stddev = Math.Sqrt(variance);
            int b8 = 0, b14 = 0, b16 = 0, b33 = 0, b33p = 0;
            foreach (double d in sorted)
            {
                if (d < 8) b8++;
                else if (d < 13.89) b14++;
                else if (d < 20) b16++;
                else if (d <= 33.33) b33++;
                else b33p++;
            }
            Logger.Info(
                $"[P15] CADENCE tag={_tag} samples={sorted.Length} callbacks={measured} sameMs={sameMs} durMs={sorted.Sum():F0} " +
                $"min={sorted[0]:F2} p50={Percentile(sorted, 0.50):F2} p95={Percentile(sorted, 0.95):F2} " +
                $"p99={Percentile(sorted, 0.99):F2} max={sorted[sorted.Length - 1]:F2} mean={mean:F2} stddev={stddev:F2} " +
                $"b8={b8} b14={b14} b16={b16} b33={b33} b33p={b33p}");
        }
    }

    /// <summary>
    /// Pass 15: injects the diagnostic probe element (20×20 rotating rect) into
    /// the Halo window's root grid. Measurement-only; exists solely under
    /// HALO_P15_PROBE=1.
    /// </summary>
    public static Microsoft.UI.Xaml.UIElement? InjectHaloProbeElement(Microsoft.UI.Xaml.Window window)
    {
        if (window.Content is not Microsoft.UI.Xaml.Controls.Grid grid) return null;
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 20,
            Height = 20,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 120, 40)),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
            Margin = new Microsoft.UI.Xaml.Thickness(4),
            RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform(),
        };
        grid.Children.Add(rect);
        return rect;
    }
}
