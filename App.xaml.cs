using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using DynamicIsland.Services;
using DynamicIsland.Views;

namespace DynamicIsland;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;
    public static WindowService WindowService { get; private set; } = null!;
    public static CompactLayoutController CompactLayoutController { get; private set; } = null!;
    public static MediaService MediaService { get; } = new();
    public static ClipboardService ClipboardService { get; } = new();
    public static BatteryService BatteryService { get; } = new();
    public static VolumeService VolumeService { get; } = new();
    public static LocationService LocationService { get; } = new();
    public static WeatherService WeatherService { get; } = new();
    public static BluetoothService BluetoothService { get; } = new();
    public static FileShelfStore FileShelfStore { get; } = new();
    public static IslandController IslandController { get; private set; } = null!;
    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;

    // Holds the dashboard preload timer for the app's lifetime. A local
    // DispatcherQueueTimer can be GC'd before it fires (Pass 4 profiling showed
    // the dashboard was still being constructed on the FIRST CLICK path — the
    // ~180 ms construction cost sat between StartSizeAnimation and the first
    // frame). A static field guarantees the one-shot actually fires at +800 ms.
    private static DispatcherQueueTimer? _preloadTimer;

    // Pass 7 process-memory forensics: checkpoint timers held in static fields
    // so they cannot be GC'd before firing, plus the transition state used to
    // dedupe high-frequency events down to lifecycle granularity.
    private static DispatcherQueueTimer? _compactIdleTimer;  // T3: compact-idle baseline at +2 s
    private static DispatcherQueueTimer? _idleSampler;       // T9: 60 s idle/trend sampler
    private static int _p7ExpandCount;                       // drives the 20-cycle GC diagnostic
    private static bool _p7MediaActive;                      // dedupes ~1 Hz media events to transitions

    // Pass 16: deterministic expand/collapse cycle driver (HALO_P16_AUTOCYCLE=1).
    // Expand via the production click path (NotifyIslandClick), collapse via the
    // production focus-lost path (NotifyMouseLeave + NotifyFocusLost) so both
    // directions exercise real code. Rooted statically so the timer cannot be
    // GC'd before the cycles finish (same pattern as _preloadTimer).
    private static DispatcherQueueTimer? _p16CycleTimer;
    private static int _p16Step;
    private static int _p16MeasuredCycles;
    private const int P16CycleCount = 5;

    // Pass 27: HALO_P27_POPUP=1 drives deterministic popup open/close cycles
    // (file-shelf 340, clipboard 180) through the production StartSizeAnimation
    // path — the exact calls ExpandShelf/ClipboardWidget make — to validate the
    // stable-window pill-growth stage (clip + region), then exits.
    private static DispatcherQueueTimer? _p27PopupTimer;
    private static int _p27PopupStep;

    public App()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Helpers.Logger.Error($"[CRASH] AppDomain.UnhandledException: {ex?.GetType().FullName}: {ex?.Message}", ex);
        };

        this.UnhandledException += (s, e) =>
        {
            Helpers.Logger.Error($"[CRASH] Application.UnhandledException: {e.Exception?.GetType().FullName}: {e.Message}", e.Exception);
            e.Handled = true; // Attempt to recover
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Helpers.Logger.Error($"[CRASH] TaskScheduler.UnobservedTaskException: {e.Exception?.GetType().FullName}: {e.Exception?.Message}", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Helpers.Logger.Info("DynamicIsland application starting up...");

        // PASS 38 (GOAL 2): unpackaged WinUI 3 apps must OLE-initialize the UI
        // thread before Explorer drags can route into the window's XAML drop
        // target (WinUI's internal RegisterDragDrop fails without it, so the
        // File Shelf drag-hover never fires). Must run before the window is
        // created. Never throws.
        Helpers.Win32DragSupport.EnsureOleInitialized();

        // Pass 14 diagnostic (env-gated): HALO_P14_REFERENCE=1 launches ONLY a
        // plain reference window and measures the CompositionTarget.Rendering
        // delivery cadence an ordinary WinUI 3 window receives on this machine
        // (10 s static), then closes. None of the Halo Bar machinery is started
        // — no WindowService, taskbar ownership, acrylic, window styles, or
        // widgets — so the reference measurement cannot be influenced by any
        // Halo Bar configuration.
        if (Helpers.MotionDiagnostics.EnableP14Reference)
        {
            DispatcherQueue = DispatcherQueue.GetForCurrentThread();
            try
            {
                var refWindow = new Views.ReferenceWindow();
                refWindow.Place(); // size + optional Halo-style config (before show)
                refWindow.Activate();
                Helpers.MotionDiagnostics.ReferenceProbe.Start(DispatcherQueue, refWindow);
            }
            catch (Exception ex)
            {
                // Diagnostic only — never strand the app if a probe API throws.
                Helpers.Logger.Error($"[MOTION-P14] probe failed: {ex.Message}", ex);
                Microsoft.UI.Xaml.Application.Current?.Exit();
            }
            return;
        }

        // Pass 15 diagnostic (env-gated): HALO_P15_CONTROL=1..4 launches ONLY a
        // control window driving a real per-frame visual change — the
        // machine-level delivery control Pass 14's quiescent reference could not
        // provide. None of the Halo Bar machinery starts.
        if (Helpers.MotionDiagnostics.EnableP15Control)
        {
            DispatcherQueue = DispatcherQueue.GetForCurrentThread();
            try
            {
                var control = new Views.ControlWindow(Helpers.MotionDiagnostics.P15ControlMode);
                control.Place();
                control.Activate();
                Helpers.MotionDiagnostics.P15Probe.Start(control, 10000, "control" + Helpers.MotionDiagnostics.P15ControlMode, control.ProbeRect);
            }
            catch (Exception ex)
            {
                Helpers.Logger.Error($"[P15] control probe failed: {ex.Message}", ex);
                Microsoft.UI.Xaml.Application.Current?.Exit();
            }
            return;
        }

        // Pass 15 minimal stage: HALO_P15_MINIMAL=1 launches ONLY the Halo
        // window content (transparent root + acrylic) with none of the runtime —
        // no services, no IslandController, no WindowService, no polling.
        // Measures whether the window content/config alone sustains the ~60 Hz
        // render stream.
        if (Helpers.MotionDiagnostics.P15Minimal)
        {
            DispatcherQueue = DispatcherQueue.GetForCurrentThread();
            try
            {
                Window = new MainWindow();
                Window.Activate();
                Helpers.MotionDiagnostics.P15Probe.Start(Window, 6000, "minimal1", null);
            }
            catch (Exception ex)
            {
                Helpers.Logger.Error($"[P15] minimal probe failed: {ex.Message}", ex);
                Microsoft.UI.Xaml.Application.Current?.Exit();
            }
            return;
        }

        // Pass 7 T0: earliest process-level footprint before services initialize.
        Helpers.ProcessMemoryProfiler.Checkpoint("ProcessStarted");

        // Pass 6 [MEM] baseline: UI-thread managed allocations from service init
        // through window creation + first placement. GC.GetAllocatedBytesForCurrentThread
        // counts only the UI thread — background work (Bluetooth/Media/Logger) is not included.
        long startupAlloc = GC.GetAllocatedBytesForCurrentThread();

        // Capture UI dispatcher before any async work
        DispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Pass 15 diagnostic: HALO_P15_PROBE=1 + HALO_P15_PROBE_EARLY=1
        // subscribes the P15 probe BEFORE the window's first present — tests
        // whether an early CompositionTarget.Rendering subscription itself
        // induces the ~60 Hz callback pump on this machine (the P11.5 cadence
        // probe subscribed at startup; a late subscription saw 0 callbacks).
        if (Helpers.MotionDiagnostics.P15ProbeEnabled && Helpers.MotionDiagnostics.P15ProbeEarly)
            Helpers.MotionDiagnostics.P15Probe.SubscribeEarly();

        // Initialize services
        _ = MediaService.InitializeAsync();
        ClipboardService.Initialize();
        BatteryService.Initialize();
        VolumeService.Initialize();
        LocationService.Initialize();
        WeatherService.Initialize();
        BluetoothService.Initialize();

        // IslandController must be created BEFORE MainWindow so that
        // MainWindowViewModel can subscribe to ActiveControlChanged on construction.
        IslandController = new IslandController(DispatcherQueue);

        // Pass 7 T4/T5/T8: process memory at every expand/collapse. Also runs the
        // marked one-shot GC diagnostic at the 20th expand so the 20-cycle test
        // can separate garbage-awaiting-collection from genuinely retained memory.
        IslandController.IsExpandedChanged += (_, expanded) =>
        {
            if (expanded)
            {
                _p7ExpandCount++;
                Helpers.ProcessMemoryProfiler.Checkpoint("DashboardExpanded");
                // Deferred off the click path: the forced collection would otherwise
                // hitch the 20th expand and corrupt the very memory picture it measures.
                if (_p7ExpandCount == 20)
                    DispatcherQueue.TryEnqueue(() => Helpers.ProcessMemoryProfiler.GcDiagnostic("After20Cycles"));
            }
            else
            {
                Helpers.ProcessMemoryProfiler.Checkpoint("DashboardCollapsed");
            }
        };

        // Pass 7 T6/T7: media card appearing/disappearing. MediaStateChanged fires
        // ~1 Hz during playback (timeline ticks) with the SAME track; dedupe to
        // actual title-present transitions so this stays at lifecycle granularity.
        App.MediaService.MediaStateChanged += (_, state) =>
        {
            // Match the pill card's visibility rule exactly (it uses IsNullOrWhiteSpace).
            bool active = !string.IsNullOrWhiteSpace(state.Title);
            if (active == _p7MediaActive) return;
            _p7MediaActive = active;
            DispatcherQueue.TryEnqueue(() =>
                Helpers.ProcessMemoryProfiler.Checkpoint(active ? "MusicActive" : "MusicRemoved"));
        };

        // Create window and wire up WindowService.
        // CompactLayoutController is the sole authority for compact geometry and
        // is created first so WindowService can consume it passively.
        Window = new MainWindow();
        CompactLayoutController = new CompactLayoutController(Window);
        WindowService = new WindowService(Window, CompactLayoutController);

        // Wire the Pass 9 pill↔dashboard crossfade choreography. WindowService
        // is created AFTER MainWindow (it needs the window instance), so the
        // ViewModel's subscription happens here rather than in its constructor.
        Window.ViewModel.AttachWindowService();

        // Stable-window production (Pass 23/24 validated, Pass 25 promoted): the
        // HWND is pre-sized once and the animation runs on the visual stage
        // inside it. Must run before InitializeWindow's pre-size so the
        // clip/transform stage is ready.
        Window.ConfigureStableWindow();

        // Any mouse press outside the dock collapses the expanded island immediately.
        // Guarded inside NotifyFocusLost by the awake-hold, so open settings surfaces
        // (gear flyout, Focus settings) are never clobbered.
        WindowService.MouseClickedOutside += (_, _) => IslandController.NotifyFocusLost();

        // Apply all DWM/borderless/toolwindow/owner styling while the window is
        // still HIDDEN so its very first present (triggered by InitializeWindow's
        // MoveAndResize / Window.Activate) is already styled. Previously this ran
        // AFTER Activate(), causing a default-styled opaque first frame (black flash).
        WindowService.ApplyDwmAttributes(DispatcherQueue);

        // Measure the taskbar BEFORE the first placement so the window is created
        // anchored in the free zone (right of Start/Search) instead of at x=0.
        CompactLayoutController.Start();

        WindowService.InitializeWindow(CompactLayoutController.CompactIdealWidth, 40);

        // Apply the COMPACT window region to the pre-sized HWND BEFORE the first
        // present so the startup first frame is already pill-limited (no
        // default-styled opaque flash). The arm path re-applies the identical
        // rect after the first layout.
        Window.ApplyInitialRegion();

        // PASS 40 (forensic-only, env-gated): the binary content tests arm right
        // here, before the first present. HALO_P40_NO_WINDOW_CONTENT swaps
        // Window.Content for a transparent minimal Grid (geometry + region
        // machinery stay alive); HALO_P40_NUKE_XAML hooks first Loaded to make
        // every XAML surface transparent. Both prove whether the black rectangle
        // lives inside or outside the XAML visual tree.
        Window.ArmP40Modes();

        WindowService.FullscreenStateChanged += (s, isFullscreen) =>
        {
            Window.DispatcherQueue.TryEnqueue(() =>
            {
                Window.SetFullscreenSuppressed(isFullscreen);
            });
        };

        Window.Activate();

        // Pass 26: Activate() can reset the DWM non-client attributes set in
        // ApplyDwmAttributes, re-enabling the full-envelope shadow/border around
        // the fixed stable-window HWND (DWM draws frame decoration from the
        // window rect, not the SetWindowRgn region). Re-assert immediately after
        // the first present; the z-order guard re-asserts every 150 ms thereafter.
        WindowService.ReassertDwmAttributes();

        // PASS 47 (GOAL 2): arm the native OLE drop target AFTER the first
        // Activate so the DesktopChildSiteBridge child exists (the bridge is the
        // window the OLE hit-test resolves) and so this registration is the last
        // (winning) one on the top-level HWND. Explorer drags are routed into the
        // File Shelf here because the XAML AllowDrop pipeline never delivers them
        // on this window.
        WindowService.ArmOleDropTarget();

        // PASS 38 (GOAL 1) Test D: dump the REAL window state after the first
        // present — the shadowDelta (GetWindowRect vs DWMWA_EXTENDED_FRAME_BOUNDS)
        // proves whether DWM still renders a frame/shadow for the envelope.
        WindowService.LogWindowForensics("post-activate");

        // PASS 39 (GOAL 1): the [P39-SURFACE] pixel-source dump right after the
        // first present. All surface mechanisms are logged in one line (styles,
        // layered mode, region box, dwm frame/shadow deltas, corner preference,
        // presenter, backdrop + root backgrounds) so the black rectangle can be
        // attributed against the desktop recording without guesswork.
        WindowService.LogP39Surface("post-activate", Window.DescribeSurfaceContext());

        // PASS 42/43 (diagnostic-only, env-gated): the binary tests run as ONE
        // self-contained sequence — baseline desktop pixels INSIDE the window →
        // apply the layer change (EMPTY_CONTENT / NO_CONTENT_BRIDGE /
        // NO_DWM_FRAME) → force a redraw without changing geometry → wait for
        // composition → sample the SAME pixels → [P42-*-RESULT] with
        // beforeRgb/afterRgb/changedPixels/rectangleGone → restore the layer so
        // the next test starts clean. Ends with the machine-readable
        // [P42-CONCLUSION]. No production changes; each test proves one layer.
        if (Helpers.MotionDiagnostics.EnableP42EmptyContent
            || Helpers.MotionDiagnostics.EnableP42NoContentBridge
            || Helpers.MotionDiagnostics.EnableP42NoDwmFrame)
        {
            DispatcherQueue.TryEnqueue(async () => await Window.RunP42BinaryTests());
        }

        // PASS 44 (diagnostic-only, env-gated): the DEFINITIVE pixel differential.
        // HALO_P44_PIXEL_DIFFERENTIAL=1 samples the suspicious envelope INSIDE
        // the Halo HWND, hides the ENTIRE main HWND (ShowWindow SW_HIDE — nothing
        // destroyed, no DWM/region/acrylic/geometry change, z-order guard
        // suspended), DwmFlush + 500 ms, samples the SAME pixels, restores
        // (SW_SHOWNOACTIVATE), samples again, then emits [P44-RESULT] and
        // [P44-CONCLUSION] halo=<confirmed|notConfirmed|inconclusive>. This
        // answers whether Halo itself paints the black rectangle. No fix here.
        if (Helpers.MotionDiagnostics.EnableP44PixelDifferential)
        {
            DispatcherQueue.TryEnqueue(async () => await Window.RunP44PixelDifferential());
        }

        // PASS 45 (diagnostic-only, env-gated): VISUAL BOUNDARY ISOLATION.
        // HALO_P45_BOUNDARY_TEST=1 runs STATE A (normal) → STATE B (all surfaces
        // transparent, backdrop/acrylic disabled — no HWND/region/bridge/DWM/
        // animation change) → STATE C (magenta diagnostic surface clipped by the
        // EXISTING region) → restore production → [P45-CONCLUSION]
        // boundaryFollows=<shape|envelope|desktop|inconclusive>
        // surfaceDependent=<true|false|inconclusive>. Determines whether the
        // perceived black boundary hugs the Halo shape, the 1000×890 envelope,
        // or neither. No fix in this pass.
        if (Helpers.MotionDiagnostics.EnableP45BoundaryTest)
        {
            DispatcherQueue.TryEnqueue(async () => await Window.RunP45BoundaryTest());
        }

        // PASS 40 (forensic-only, env-gated):
        //  HALO_P40_AUDIT=1            live visual-tree surface dump ([P40-XAML]).
        //  HALO_P40_PIXEL_FORENSICS=1  real desktop GetPixel scan ([P40-SCAN]),
        //                              HWND attribution ([P40-HIT]) and process
        //                              window census ([P40-HWNDS]); re-runs on
        //                              compact→expanded→popup state changes.
        //  HALO_P41_OWNER_FORENSICS=1  PASS 41: per-pixel owner attribution
        //                              ([P41-SAMPLE]) + child-inclusive census
        //                              ([P41-HWNDS]) + PIXEL_OWNER_MATCH /
        //                              CONCLUSION ([P41-OWNER]).
        //  HALO_P42_PIXEL_OWNER=1      PASS 42: corrected in-bounds pixel-owner
        //                              scan ([P42-PIXEL]/[P42-BBOX]/
        //                              [P42-CONCLUSION]) — every sample strictly
        //                              inside GetWindowRect(haloHwnd).
        if (Helpers.MotionDiagnostics.EnableP40Audit)
            Window.XamlSurfaceAudit();

        if (Helpers.MotionDiagnostics.EnableP40PixelForensics
            || Helpers.MotionDiagnostics.EnableP41OwnerForensics
            || Helpers.MotionDiagnostics.EnableP42OwnerForensics)
        {
            var p40Timer = DispatcherQueue.CreateTimer();
            p40Timer.Interval = TimeSpan.FromMilliseconds(1500);
            p40Timer.IsRepeating = true;
            p40Timer.Tick += (_, _) => Window.RunP40ScanIfChanged();
            p40Timer.Start();
        }

        // PASS 38 (GOAL 1): HALO_P38_FORENSICS=1 repeats the state dump every
        // 2 s so the user can compare collapsed/expanded/popup states on the
        // real desktop without restarting.
        // PASS 39 (GOAL 1): HALO_P39_SURFACE=1 repeats the [P39-SURFACE] dump on
        // the same cadence (and turns the P38 dump on too) so the rectangle can
        // be watched across state transitions under an A/B mode.
        if (Helpers.MotionDiagnostics.EnableP38Forensics || Helpers.MotionDiagnostics.EnableP39SurfaceDump)
        {
            var forensicsTimer = DispatcherQueue.CreateTimer();
            forensicsTimer.Interval = TimeSpan.FromSeconds(2);
            forensicsTimer.IsRepeating = true;
            forensicsTimer.Tick += (_, _) =>
            {
                WindowService.LogWindowForensics("tick");
                if (Helpers.MotionDiagnostics.EnableP39SurfaceDump)
                    WindowService.LogP39Surface("tick", Window.DescribeSurfaceContext());
            };
            forensicsTimer.Start();
        }

        // Pass 2/5 perf: pre-construct the expanded dashboard shortly after startup
        // (off the click path) so the first click-to-expand is not delayed by full
        // dashboard construction. The click path still lazily creates it if the
        // user expands before this one-shot fires. Held in a static field so the
        // timer cannot be collected before it fires (see _preloadTimer).
        _preloadTimer = DispatcherQueue.CreateTimer();
        _preloadTimer.Interval = TimeSpan.FromMilliseconds(800);
        _preloadTimer.IsRepeating = false;
        _preloadTimer.Tick += (_, _) =>
        {
            IslandController.PreloadDashboard();
            // Pass 17: after the dashboard is constructed, warm its first real
            // measure/arrange (explicit 780×640) invisibly in a temporary
            // off-screen window, so the first user expansion no longer pays the
            // 94–125 ms first-layout stall (Pass 16). Runs on the UI thread
            // AFTER construction (same dispatcher queue, FIFO).
            IslandController.WarmupDashboardLayout();
        };
        _preloadTimer.Start();

        // Pass 7 checkpoints: T1 startup-UI-ready, T3 compact-idle baseline at +2 s
        // (after the initial pill animation settles), T9 idle/trend sampler at 60 s.
        Helpers.ProcessMemoryProfiler.Checkpoint("StartupUiReady");

        _compactIdleTimer = DispatcherQueue.CreateTimer();
        _compactIdleTimer.Interval = TimeSpan.FromSeconds(2);
        _compactIdleTimer.IsRepeating = false;
        _compactIdleTimer.Tick += (_, _) => Helpers.ProcessMemoryProfiler.Checkpoint("CompactIdle");
        _compactIdleTimer.Start();

        _idleSampler = DispatcherQueue.CreateTimer();
        _idleSampler.Interval = TimeSpan.FromSeconds(60);
        _idleSampler.IsRepeating = true;
        _idleSampler.Tick += (_, _) => Helpers.ProcessMemoryProfiler.Checkpoint("Idle60s");
        _idleSampler.Start();

        Helpers.Logger.Info($"[MEM] startup UI-thread managed allocation: {(GC.GetAllocatedBytesForCurrentThread() - startupAlloc) / 1024.0 / 1024.0:F1} MB (services + window + first placement)");

        // Pass 15 probe: HALO_P15_PROBE=1 injects a tiny rotating rect into the
        // real Halo window and drives it from inside CompositionTarget.Rendering
        // for 10 s — measures whether Halo's presentation path can exceed ~60 Hz
        // when forced to do continuous visible UI-thread work.
        if (Helpers.MotionDiagnostics.P15ProbeEnabled)
        {
            var probeElement = Helpers.MotionDiagnostics.InjectHaloProbeElement(Window);
            Helpers.MotionDiagnostics.P15Probe.Start(Window, 10000, "halo_probe_xaml", probeElement);
        }

        // Pass 16 diagnostic (env-gated): HALO_P16_AUTOCYCLE=1 drives
        // deterministic expand/collapse cycles over the production click /
        // focus-lost paths. The first cycle starts at +1.2 s, AFTER the +0.8 s
        // dashboard preload, so one session measures the cold first expand
        // (attach + first layout of the preloaded tree), warm repeats, and the
        // collapse control. HALO_P16_WARMUP=1 adds one warm-up pair before the
        // measured cycles (Mode D: construction + layout already done before the
        // first measured expand). Cycles run on a 1.2 s step — transitions
        // (300/250 ms) always settle between steps, so segments never stack.
        if (Helpers.MotionDiagnostics.P16AutoCycle)
        {
            _p16CycleTimer = DispatcherQueue.CreateTimer();
            // First tick always fires at +1.2 s (after the +0.8 s dashboard
            // preload/warm-up) so the measured cycles start on the warm tree.
            _p16CycleTimer.Interval = TimeSpan.FromMilliseconds(1200);
            _p16CycleTimer.IsRepeating = true;
            _p16CycleTimer.Tick += (_, _) =>
            {
                int targetPairs = (Helpers.MotionDiagnostics.P16Warmup ? 1 : 0)
                    + (Helpers.MotionDiagnostics.AutoCycleCycles > 0
                        ? Helpers.MotionDiagnostics.AutoCycleCycles
                        : P16CycleCount);
                int pair = _p16Step / 2;
                if (pair >= targetPairs)
                {
                    _p16CycleTimer.Stop();
                    Helpers.Logger.Info($"[MOTION-P16] AUTOCYCLE complete pairs={pair} measuredCycles={_p16MeasuredCycles} — exiting.");
                    Microsoft.UI.Xaml.Application.Current?.Exit();
                    return;
                }

                bool isExpand = (_p16Step % 2) == 0;
                bool isWarmup = Helpers.MotionDiagnostics.P16Warmup && pair == 0;
                if (isExpand)
                {
                    if (!isWarmup) _p16MeasuredCycles++;
                    Helpers.MotionDiagnostics.P16SegmentTag = isWarmup
                        ? "warmup"
                        : (_p16MeasuredCycles == 1 ? "cold" : "warm");
                    Helpers.Logger.Info($"[MOTION-P16] CYCLE measured={_p16MeasuredCycles} phase=expand warmup={isWarmup} tag={Helpers.MotionDiagnostics.P16SegmentTag}");
                    App.IslandController.NotifyIslandClick();
                }
                else
                {
                    Helpers.MotionDiagnostics.P16SegmentTag = "collapse";
                    Helpers.Logger.Info($"[MOTION-P16] CYCLE pair={pair} phase=collapse warmup={isWarmup}");
                    // Focus-lost collapse: establish the same state the pointer
                    // exit path does (cursor not over the island) so
                    // NotifyFocusLost always collapses, then drive the real
                    // production collapse path.
                    App.IslandController.NotifyMouseLeave();
                    App.IslandController.NotifyFocusLost();
                }
                _p16Step++;

                // Pass 17: HALO_P16_FAST=1 shortens the step AFTER the first
                // tick, so expand/collapse retarget mid-animation (rapid-
                // reversal stress) while still starting on the warmed tree.
                if (Helpers.MotionDiagnostics.P16Fast
                    && _p16Step == 1
                    && _p16CycleTimer.Interval > TimeSpan.FromMilliseconds(120))
                {
                    _p16CycleTimer.Interval = TimeSpan.FromMilliseconds(120);
                }
            };
            _p16CycleTimer.Start();
        }

        // Pass 27 diagnostic (env-gated): HALO_P27_POPUP=1 cycles the pill
        // between popup heights (340 shelf, 180 clipboard) and the compact strip
        // over the PRODUCTION StartSizeAnimation path. First tick at +1.0 s
        // (after the +0.8 s dashboard preload/warm-up) so the stage starts on
        // the warmed tree. Steps settle within the 1.0 s interval (300/250 ms).
        if (Helpers.MotionDiagnostics.P27PopupTest)
        {
            _p27PopupTimer = DispatcherQueue.CreateTimer();
            _p27PopupTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _p27PopupTimer.IsRepeating = true;
            _p27PopupTimer.Tick += (_, _) =>
            {
                int[] popupHeights = { 340, 48, 180, 48 };
                if (_p27PopupStep >= popupHeights.Length)
                {
                    _p27PopupTimer.Stop();
                    Helpers.Logger.Info("[MOTION-P27] POPUP complete — exiting.");
                    Microsoft.UI.Xaml.Application.Current?.Exit();
                    return;
                }
                var (popW, _) = App.WindowService.CompactSize;
                int popH = popupHeights[_p27PopupStep];
                Helpers.Logger.Info($"[MOTION-P27] POPUP step={_p27PopupStep} target={popW}x{popH}");
                App.WindowService.StartSizeAnimation(popW, popH);
                _p27PopupStep++;
            };
            _p27PopupTimer.Start();
        }
    }
}
