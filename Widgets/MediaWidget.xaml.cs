using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.ViewModels;

namespace DynamicIsland.Widgets;

public sealed partial class MediaWidget : UserControl, IIslandWidget
{
    private DispatcherTimer? _visualizerTimer;
    private double _progressVal = 35;
    private int _tickCount = 0;

    // -1 = unknown, 0 = full, 1 = moderate (artist hidden), 2 = heavy (both hidden)
    private int _lastCollapsedTier = -1;

    // ── IIslandWidget ──────────────────────────────────────────────────────

    public WidgetPriority Priority => WidgetPriority.Media;

    public bool AutoExpand => false;

    public WindowProfile PreferredProfile => WindowProfile.Expanded;

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnSuspended() { }
    public void OnResumed() { }

    // ── Widget ─────────────────────────────────────────────────────────────

    public MediaWidgetViewModel ViewModel { get; } = new();

    public MediaWidget()
    {
        InitializeComponent();
        
        // Listen for track changes to reset progress smoothly
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.Title))
            {
                _progressVal = 0;
            }
        };

        StartVisualizer();
    }

    private void StartVisualizer()
    {
        _visualizerTimer = new DispatcherTimer();
        _visualizerTimer.Interval = TimeSpan.FromMilliseconds(120);
        _visualizerTimer.Tick += (s, e) =>
        {
            if (ViewModel.IsPlaying)
            {
                _tickCount++;
                if (WBar0 != null) WBar0.Height = 3 + 8 * Math.Abs(Math.Sin(_tickCount * 0.4 + 0));
                if (WBar1 != null) WBar1.Height = 3 + 10 * Math.Abs(Math.Sin(_tickCount * 0.3 + 1));
                if (WBar2 != null) WBar2.Height = 3 + 6 * Math.Abs(Math.Sin(_tickCount * 0.5 + 2));
                if (WBar3 != null) WBar3.Height = 3 + 11 * Math.Abs(Math.Sin(_tickCount * 0.35 + 3));
                if (WBar4 != null) WBar4.Height = 3 + 8 * Math.Abs(Math.Sin(_tickCount * 0.45 + 4));

                // Increment playback progress line smoothly when track plays
                _progressVal += 0.08;
                if (_progressVal > 100)
                {
                    _progressVal = 0;
                }
            }

            var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"];
            var muted = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255));

            if (WBar0 != null) WBar0.Background = _progressVal >= 20 ? accent : muted;
            if (WBar1 != null) WBar1.Background = _progressVal >= 40 ? accent : muted;
            if (WBar2 != null) WBar2.Background = _progressVal >= 60 ? accent : muted;
            if (WBar3 != null) WBar3.Background = _progressVal >= 80 ? accent : muted;
            if (WBar4 != null) WBar4.Background = _progressVal >= 95 ? accent : muted;
        };
        _visualizerTimer.Start();
    }

    // ── Animated text visibility ───────────────────────────────────────────

    /// <summary>
    /// Fades a TextBlock in (opacity 0→1, then Visibility.Visible) or
    /// out (opacity 1→0, then Visibility.Collapsed) over 200ms.
    /// </summary>
    private void AnimateTextBlock(TextBlock block, bool show)
    {
        if (block == null) return;

        var anim = new DoubleAnimation
        {
            To           = show ? 1.0 : 0.0,
            Duration     = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        if (show)
        {
            // Make visible immediately before fading in
            block.Visibility = Visibility.Visible;
        }
        else
        {
            // Collapse after the fade-out completes
            anim.Completed += (_, _) =>
            {
                block.Visibility = Visibility.Collapsed;
            };
        }

        Storyboard.SetTarget(anim, block);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void ApplyTier(int tier)
    {
        if (SongTitleTextBlock == null || SongArtistTextBlock == null) return;

        switch (tier)
        {
            case 0: // Full — both visible
                AnimateTextBlock(SongTitleTextBlock, show: true);
                AnimateTextBlock(SongArtistTextBlock, show: true);
                break;
            case 1: // Moderate — title visible, artist hidden
                AnimateTextBlock(SongTitleTextBlock, show: true);
                AnimateTextBlock(SongArtistTextBlock, show: false);
                break;
            case 2: // Heavy — both hidden
                AnimateTextBlock(SongTitleTextBlock, show: false);
                AnimateTextBlock(SongArtistTextBlock, show: false);
                break;
        }
    }

    /// <summary>
    /// Called by WindowService whenever the width tier changes (0=full, 1=moderate, 2=heavy).
    /// This is the authoritative tier setter — MediaWidget never re-derives the tier from
    /// its own width so it cannot oscillate across tier boundaries during spring settling.
    /// </summary>
    public void SetTier(int tier)
    {
        if (tier == _lastCollapsedTier) return;
        string tierName = tier == 0 ? "full" : tier == 1 ? "moderate" : "heavy";
        Logger.Info($"[WIDTH_TIER] MediaWidget.SetTier: tier={tier} ({tierName}), prev={_lastCollapsedTier}");
        _lastCollapsedTier = tier;
        ApplyTier(tier);
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double height = e.NewSize.Height;
        string state = height > 50 ? "ExpandedState" : "CollapsedState";
        VisualStateManager.GoToState(this, state, true);

        if (state == "ExpandedState")
        {
            // In ExpandedState always show both text blocks immediately (no animation needed)
            if (SongTitleTextBlock != null) { SongTitleTextBlock.Visibility = Visibility.Visible; SongTitleTextBlock.Opacity = 1; }
            if (SongArtistTextBlock != null) { SongArtistTextBlock.Visibility = Visibility.Visible; SongArtistTextBlock.Opacity = 1; }
            _lastCollapsedTier = 0;
        }
        // Note: tier-based visibility is NOT derived from width here — it is set exclusively
        // via SetTier() which is called from WindowService when the 150ms timer detects a change.
        // This prevents flickering caused by the spring animation crossing the tier threshold.
    }
}
