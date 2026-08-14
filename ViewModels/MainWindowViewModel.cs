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

    [ObservableProperty]
    public partial double DashboardScale { get; set; } = 0.97;

    // Choreography state: re-based from the CURRENT rendered values at each
    // segment start so reversals continue without a content jump.
    private bool _motionSegmentActive;
    private bool _motionExpanding;
    private double _contentFromPill;
    private double _contentFromDash;
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
        // PASS 36: the reveal is a PURE bottom-anchored clip — no scale
        // animation (the ScaleTransform stays pinned at 1.0). Only the
        // dashboard opacity crossfades during the reveal.
        DashboardScale = 1.0;
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
            // PASS 37: the pill disappears on click. The dashboard reveals
            // upward as a bottom-anchored band (clip top 664→0, bottom fixed
            // at the pill's top / taskbar top). The pill fades out during the
            // transition and is collapsed at full expansion — the expanded
            // state contains ONLY the dashboard, and the taskbar below is
            // never covered.
            PillVisibility = Visibility.Visible;
            ExpandedVisibility = Visibility.Visible;
            DashboardScale = 1.0;
            Logger.Info("[MOTION-P9] PillExitStarted");
            Logger.Info("[MOTION-P9] DashboardEntranceStarted");
        }
        else
        {
            // Reverse: the dashboard band closes toward the pill's top edge,
            // then the pill fades back in at its exact original position.
            PillVisibility = Visibility.Visible;
            ExpandedVisibility = Visibility.Visible;
            DashboardScale = 1.0;
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
                // PASS 37: the pill fades away as the dashboard takes over
                // (PASS 36: no scale — pure bottom-anchored clip reveal).
                PillOpacity = _contentFromPill * (1.0 - k);
                DashboardScale = 1.0;
            }
            else
            {
                DashboardOpacity = _contentFromDash * (1.0 - k);
                // PASS 37: the pill fades back in at its exact original
                // position as the dashboard band closes.
                PillOpacity = _contentFromPill + (1.0 - _contentFromPill) * k;
                DashboardScale = 1.0;
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
                DashboardScale = 1.0; // pinned — no scale animation (PASS 36)
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
