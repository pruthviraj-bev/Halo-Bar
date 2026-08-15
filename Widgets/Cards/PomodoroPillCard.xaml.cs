using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DynamicIsland.Interfaces;
using DynamicIsland.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace DynamicIsland.Widgets.Cards;

/// <summary>
/// Pomodoro ring card for the compact pill. A separate rounded-square tile
/// (noticeably darker than the pill background) showing a circular Azure
/// progress arc that fills clockwise from 12 o'clock as the Focus Session
/// counts down, with a small solid green dot static at the ring's center
/// that gently pulses ("beep" opacity loop, 1 s) while RUNNING and
/// disappears while PAUSED (the ring stays). The center is empty — no time,
/// no label. The card appears while a session is active (counting down OR
/// paused — it only hides on completion, reset, or a click). A single click
/// dismisses the card from the pill until the next session starts and is
/// swallowed so it never expands the dashboard.
/// </summary>
public sealed partial class PomodoroPillCard : UserControl, IPillCard, INotifyPropertyChanged
{
    // ── IPillCard ────────────────────────────────────────────────────────────
    private bool _shouldShow;
    public bool ShouldShow
    {
        get => _shouldShow;
        private set
        {
            if (_shouldShow == value) return;
            _shouldShow = value;
            OnPropertyChanged();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private double _tileSize = 40;
    public double CardWidth => _tileSize;

    public UserControl View => this;
    public event EventHandler? StateChanged;

    /// <summary>Square tile side length (DIP), set by the host (PillDashboard)
    /// so the tile matches the pill content height. Defaults to 40.</summary>
    public double TileSize
    {
        set
        {
            double size = Math.Max(value, 24);
            if (Math.Abs(size - _tileSize) < 0.5) return;
            _tileSize = size;
            if (TileRoot != null)
            {
                TileRoot.Width = size;
                TileRoot.Height = size;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Ring geometry ────────────────────────────────────────────────────────
    // 28×28 ring host: the arc Path is 24 DIP (the stroke centerline of a
    // 24 DIP ring with a 4 DIP stroke → outer edge 28, exactly the host edge).
    // Center 12 / radius 12 — the arc starts at 12 o'clock and sweeps
    // clockwise, same convention and technique (Path + ArcSegment) as the
    // dashboard's Focus ring. The green dot is static at the ring's center.
    private const double RingCenter = 12;
    private const double RingRadius = 12;
    private const double RingTopY = 0;      // Center − Radius
    private const double RingBottomY = 24;  // Center + Radius

    // ── Session state ────────────────────────────────────────────────────────
    private bool _dismissed;
    private bool _wasActive;

    // Gentle "beep" pulse: the GREEN DOT breathes 0.45 → 1.0 opacity, 1 s per
    // full cycle (500 ms each half, looping) while the session is RUNNING.
    private Storyboard? _pulse;

    public PomodoroPillCard()
    {
        InitializeComponent();
        FocusSessionBridge.StateChanged += OnFocusStateChanged;
        Refresh();
    }

    private void OnFocusStateChanged(object? sender, EventArgs e)
        => Refresh();

    private void Refresh()
    {
        bool active = FocusSessionBridge.IsActive;
        bool running = FocusSessionBridge.IsRunning;

        // A fresh session (was not active → now active) brings a dismissed
        // card back, so "click to dismiss" only hides it for the current
        // session. The card stays visible while PAUSED (IsActive covers
        // paused mid-session) and hides only on completion, reset, or click.
        if (active && !_wasActive)
            _dismissed = false;
        _wasActive = active;

        ShouldShow = active && !_dismissed;

        if (RingArc != null && TipDot != null)
        {
            UpdateRing(FocusSessionBridge.ProgressFraction);
            // Paused → the dot disappears (timer is paused); running → it
            // comes back and pulses as the active indicator.
            TipDot.Visibility = running
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (ShouldShow && running)
            StartPulse();
        else
            StopPulse();
    }

    private void StartPulse()
    {
        if (_pulse != null || TipDot == null) return;
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 0.45,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(500),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, TipDot);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        _pulse = storyboard;
        storyboard.Begin();
    }

    private void StopPulse()
    {
        _pulse?.Stop(); // reverts TipDot.Opacity to its base value (1.0)
        _pulse = null;
    }

    /// <summary>
    /// Draws the Azure arc from 12 o'clock clockwise for the given elapsed
    /// fraction (0.0–1.0), using the dashboard Focus ring's exact technique:
    /// a PathGeometry with an ArcSegment built directly from the fraction
    /// (start at the top, sweep clockwise). The arc is drawn by construction,
    /// so it reaches a full circle exactly at fraction 1.0 — no dash-pattern
    /// path-length dependence (the earlier Ellipse + StrokeDashArray wrapped
    /// and looked complete several seconds early). Handles the 0 (empty) and
    /// full-circle (two half-arcs, since a single ArcSegment cannot render a
    /// complete 360° cleanly) edge cases. The green dot stays static at the
    /// ring's center (declared in XAML).
    /// </summary>
    private void UpdateRing(double fraction)
    {
        double f = Math.Clamp(fraction, 0, 1);
        var geometry = new PathGeometry();

        // No visible arc at 0%.
        if (f <= 0)
        {
            RingArc.Data = geometry;
            return;
        }

        // A single ArcSegment cannot represent a complete 360° circle without
        // rendering artifacts — split the full ring into two half-arcs.
        if (f >= 1)
        {
            geometry.Figures.Add(CreateHalfArc(new Point(RingCenter, RingTopY), new Point(RingCenter, RingBottomY)));
            geometry.Figures.Add(CreateHalfArc(new Point(RingCenter, RingBottomY), new Point(RingCenter, RingTopY)));
            RingArc.Data = geometry;
            return;
        }

        double angle = 360.0 * f;
        double radians = angle * Math.PI / 180.0;
        var end = new Point(
            RingCenter + RingRadius * Math.Sin(radians),
            RingCenter - RingRadius * Math.Cos(radians));

        var figure = new PathFigure
        {
            StartPoint = new Point(RingCenter, RingTopY),
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(RingRadius, RingRadius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = angle > 180
        });
        geometry.Figures.Add(figure);
        RingArc.Data = geometry;
    }

    private static PathFigure CreateHalfArc(Point startPoint, Point endPoint)
    {
        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(RingRadius, RingRadius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        });
        return figure;
    }

    // ── Click handling ──────────────────────────────────────────────────────

    /// <summary>
    /// Swallows the pointer press so it never bubbles to MainWindow's island
    /// click toggle (same pattern as ClipboardWidget) — clicking the ring card
    /// must NOT expand the dashboard.
    /// </summary>
    private void OnTilePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            e.Handled = true;
    }

    /// <summary>
    /// Single click removes the card from the pill (dismissed until the next
    /// session starts, per Refresh()). Works while running OR paused — the
    /// card only stays until completed/reset/click. Tapping inside the ring
    /// does nothing else.
    /// </summary>
    private void OnTileTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!FocusSessionBridge.IsActive) return;
        _dismissed = true;
        ShouldShow = false;
    }
}
