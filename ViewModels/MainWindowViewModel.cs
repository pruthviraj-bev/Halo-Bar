using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicIsland.Helpers;
using DynamicIsland.Services;

namespace DynamicIsland.ViewModels;

/// <summary>
/// Thin binding adapter between IslandController and the MainWindow XAML.
/// This VM is a read-only projection of controller state — it never mutates the
/// controller and holds no source-of-truth data of its own.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty]
    public partial UserControl? ActiveWidget { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial UserControl? Dashboard { get; set; }

    // Observable Visibility projections of IsExpanded, so the XAML never needs a
    // function x:Bind (which does not track its observable arguments).
    [ObservableProperty]
    public partial Visibility ExpandedVisibility { get; set; }

    [ObservableProperty]
    public partial Visibility PillVisibility { get; set; }

    // Pass 9 content choreography — driven by WindowService's shared motion
    // progress so pill/dashboard never drift from the geometry animation.
    [ObservableProperty]
    public partial double PillOpacity { get; set; } = 1.0;

    [ObservableProperty]
    public partial double DashboardOpacity { get; set; } = 0.0;

    // PASS 19 (flyout): the dashboard arrives as ONE coherent surface —
    // opacity crossfade + scale 0.97→1 anchored at the pill's bottom edge
    // (CenterY=640), all driven by the shared eased progress. The bottom edge
    // is pinned by the scale anchor, so the surface grows UPWARD from the
    // taskbar-top without the bottom stepping during the transition.
    // DashboardTranslateY stays 0 (a rise made the bottom edge sit below its
    // final position mid-animation and read as a glitch on desktop).
    [ObservableProperty]
    public partial double DashboardScale { get; set; } = 0.97;

    [ObservableProperty]
    public partial double DashboardTranslateY { get; set; } = 0.0;

    // Choreography state: re-based from the CURRENT rendered values at each
    // segment start so reversals continue without a content jump.
    private bool _motionSegmentActive;
    private bool _motionExpanding;
    private double _contentFromPill;
    private double _contentFromDash;
    private double _contentFromScale;
    private bool _motionFirstProgress;
    private bool _motionMidpointLogged;

    partial void OnIsExpandedChanged(bool value)
    {
        // Expansion state projection only (Pass 9). The instant visibility swap
        // was removed — pill/dashboard visibility and opacity are now driven by
        // the WindowService motion-segment choreography (shared geometry
        // progress), so the two surfaces crossfade instead of hard-swapping.
    }

    public MainWindowViewModel()
    {
        InitVisibilities();

        Subscribe();
        SyncFromController();
    }

    private void InitVisibilities()
    {
        ExpandedVisibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        PillVisibility    = IsExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Subscribe()
    {
        // Pass 15 minimal diagnostic mode launches MainWindow without an
        // IslandController — a no-op in production, where the controller is
        // always created before MainWindow (App.OnLaunched ordering).
        if (App.IslandController == null) return;

        App.IslandController.ActiveControlChanged += (_, control) =>
            _dispatcherQueue.TryEnqueue(() =>
            {
                ActiveWidget = control;
            });

        App.IslandController.IsExpandedChanged += (_, expanded) =>
            _dispatcherQueue.TryEnqueue(() =>
            {
                Logger.Info($"[PROFILE] VM handler IsExpandedChanged start ms={Environment.TickCount64} expanded={expanded}");
                IsExpanded = expanded;
                // Single dashboard instance owned by IslandController (preloaded
                // off the click path); never constructs a second copy here.
                if (expanded)
                {
                    MotionDiagnostics.P16Mark("DashboardAttachStarted");
                    Dashboard = App.IslandController.EnsureDashboard();
                    MotionDiagnostics.P16Mark("DashboardAttachCompleted");
                }
                Logger.Info($"[PROFILE] VM handler IsExpandedChanged end ms={Environment.TickCount64}");
            });

        // Pass 9: content crossfade choreography follows the geometry animation's
        // shared eased progress (single source: WindowService's render loop).
        // Handlers run on the UI thread (Rendering / StartSizeAnimation context),
        // so no dispatcher round-trip is needed. NOT subscribed here:
        // App.WindowService is created after MainWindow in App.OnLaunched (it
        // needs the window instance), so it is still null during this ctor —
        // App calls AttachWindowService() once WindowService exists.
    }

    /// <summary>
    /// Wires the Pass 9 pill↔dashboard crossfade choreography to WindowService's
    /// shared motion events. WindowService is constructed AFTER MainWindow in
    /// App.OnLaunched (it needs the window instance), so the VM cannot subscribe
    /// in its constructor. App calls this immediately after creating
    /// WindowService. Motion segments before that point (the startup pill
    /// settle) are gated out by OnMotionSegmentStarted anyway, so no
    /// choreography is missed.
    /// </summary>
    public void AttachWindowService()
    {
        // Defensive: same null-guard pattern IslandController uses for the
        // WindowService-is-null-during-startup window.
        if (App.WindowService == null) return;

        App.WindowService.MotionSegmentStarted += OnMotionSegmentStarted;
        App.WindowService.MotionProgressChanged += OnMotionProgressChanged;
    }

    // ── Pass 9 content choreography ────────────────────────────────────────

    /// <summary>
    /// A geometry segment started (expand, collapse, or a retarget of either).
    /// Re-bases the crossfade from the CURRENT rendered values so reversals
    /// continue without a content jump, then publishes both surfaces (pill
    /// fading out, dashboard fading in) for the transition's duration.
    /// Compact-width tweaks and the legacy clipboard preview are not dashboard
    /// transitions and are ignored.
    /// </summary>
    private void OnMotionSegmentStarted(WindowService.MotionSegment seg)
    {
        // Only choreograph real dashboard transitions: profile-driven AND a
        // height change (startup settle and compact→compact tweaks are 48→48).
        if (!seg.IsDashboardTransition || seg.FromHeight == seg.TargetHeight) return;

        _motionSegmentActive = true;
        _motionExpanding = seg.Expanding;
        _contentFromPill = PillOpacity;
        _contentFromDash = DashboardOpacity;
        // PASS 19 (flyout): re-base the surface transform from the CURRENT
        // rendered values so reversals continue without a jump. The dashboard
        // arrives as one surface — opacity + scale 0.97→1 (pill anchor) + a
        // slight rise (TranslateY +10→0), all on the shared eased progress.
        _contentFromScale = DashboardScale;
        _motionFirstProgress = true;
        _motionMidpointLogged = false;

        // Pass 10 diagnostic (debug-only): DisableContentChoreography runs the
        // exact same geometry animation with NO opacity/scale choreography — the
        // surfaces swap instantly at the segment boundaries (pre-Pass 9
        // behavior). Isolates content cost from geometry cost.
        if (MotionDiagnostics.DisableContentChoreography)
        {
            // Diagnostic instant-swap: terminal surface model of production
            // (PASS 37: expanded state is ONLY the dashboard; the pill
            // disappears on click and reappears on collapse).
            PillVisibility = seg.Expanding ? Visibility.Collapsed : Visibility.Visible;
            ExpandedVisibility = seg.Expanding ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (seg.Expanding)
        {
            // PASS 37: the pill disappears on click. PASS 19 (flyout): the
            // dashboard is ONE surface from frame one — the clip is snapped
            // fully open by MainWindow, and this VM animates opacity 0→1,
            // scale 0.97→1 (pill anchor) and a slight rise. The pill fades
            // out during the transition and is collapsed at full expansion —
            // the expanded state contains ONLY the dashboard, and the taskbar
            // below is never covered.
            PillVisibility = Visibility.Visible;
            ExpandedVisibility = Visibility.Visible;
            Logger.Info("[MOTION-P9] PillExitStarted");
            Logger.Info("[MOTION-P9] DashboardEntranceStarted");
        }
        else
        {
            // Reverse: the dashboard flies out (scale down toward the pill,
            // slight drop, fade), the band closes, then the pill fades back
            // in at its exact original position.
            PillVisibility = Visibility.Visible;
            ExpandedVisibility = Visibility.Visible;
            Logger.Info("[MOTION-P9] PillEntranceStarted");
            Logger.Info("[MOTION-P9] DashboardExitStarted");
        }
    }

    private void OnMotionProgressChanged(double progress)
    {
        if (!_motionSegmentActive) return;

        // Pass 10: publish the current content state so every frame sample in
        // the motion ring buffer carries the opacity/scale the user saw.
        MotionDiagnostics.PillOpacity = PillOpacity;
        MotionDiagnostics.DashboardOpacity = DashboardOpacity;
        MotionDiagnostics.DashboardScale = DashboardScale;

        if (_motionFirstProgress)
        {
            _motionFirstProgress = false;
            Logger.Info("[MOTION-P9] FirstFrame");
        }
        if (!_motionMidpointLogged && progress >= 0.5)
        {
            _motionMidpointLogged = true;
            Logger.Info("[MOTION-P9] 50Percent");
        }

        double k = Math.Clamp(progress, 0.0, 1.0);
        // Pass 10 diagnostic: when choreography is disabled, opacity/scale stay
        // at their base values (surfaces already swapped at segment start); only
        // the terminal visibility flush below still runs.
        if (!MotionDiagnostics.DisableContentChoreography)
        {
            if (_motionExpanding)
            {
                DashboardOpacity = _contentFromDash + (1.0 - _contentFromDash) * k;
                PillOpacity = _contentFromPill * (1.0 - k);
                // PASS 19 (flyout-in): one surface arriving — scale from the
                // pill anchor to full (bottom edge pinned by the scale center).
                DashboardScale = _contentFromScale + (1.0 - _contentFromScale) * k;
                DashboardTranslateY = 0.0;
            }
            else
            {
                DashboardOpacity = _contentFromDash * (1.0 - k);
                // PASS 19 (flyout-out): reverse — slight scale-down toward the
                // pill, fade.
                PillOpacity = _contentFromPill + (1.0 - _contentFromPill) * k;
                DashboardScale = _contentFromScale + (0.97 - _contentFromScale) * k;
                DashboardTranslateY = 0.0;
            }
        }

        if (k >= 1.0)
        {
            if (_motionExpanding)
            {
                // PASS 37: at full expansion ONLY the dashboard exists — the
                // pill has faded out and is collapsed (it was the origin, not
                // part of the expanded surface).
                PillVisibility = Visibility.Collapsed;
                PillOpacity = 0;
                Logger.Info("[MOTION-P9] PillExitCompleted");
                Logger.Info("[MOTION-P9] DashboardEntranceCompleted");
            }
            else
            {
                ExpandedVisibility = Visibility.Collapsed;
                DashboardOpacity = 0;
                // PASS 19 (flyout): terminal rest state — the next expand
                // re-bases from these values (0.97 scale, translate pinned 0).
                DashboardScale = 0.97;
                DashboardTranslateY = 0.0;
                Logger.Info("[MOTION-P9] DashboardExitCompleted");
                Logger.Info("[MOTION-P9] PillEntranceCompleted");
            }
            Logger.Info("[MOTION-P9] MotionCompleted");
            _motionSegmentActive = false;
        }
    }

    /// <summary>
    /// Pulls the controller's current state once. Run only after Subscribe(). Reconciles
    /// anything the controller published before this VM existed (e.g. the default widget
    /// pushed in the IslandController constructor). Extend here as the projected state
    /// grows (ActiveWidget, IsExpanded, Priority, badges, ...) — one place to update.
    /// </summary>
    private void SyncFromController()
    {
        if (App.IslandController == null) return; // Pass 15 minimal mode
        var current = App.IslandController.CurrentControl;
        ActiveWidget = current;
    }
}
