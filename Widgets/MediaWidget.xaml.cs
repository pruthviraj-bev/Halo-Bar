using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using DynamicIsland.ViewModels;

namespace DynamicIsland.Widgets;

public sealed partial class MediaWidget : UserControl, IIslandWidget
{
    private DispatcherTimer? _visualizerTimer;
    private double _progressVal = 35;

    private int _tickCount = 0;

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

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        string state = e.NewSize.Height > 50 ? "ExpandedState" : "CollapsedState";
        VisualStateManager.GoToState(this, state, true);
    }
}
