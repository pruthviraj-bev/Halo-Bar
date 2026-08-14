namespace DynamicIsland.Helpers;

/// <summary>
/// PASS 1 (V1 REDESIGN — main dashboard container only): the expanded Halo
/// surface contract. Single source of truth for the outer-container geometry so
/// the envelope (WindowService), the visual stage (MainWindow), and the warm-up
/// path (IslandController) can never drift apart again.
///
/// Contract (validated in DesignRules.md §4.1/§3.2 as the redesign's starting
/// values):
///  - Dashboard surface: 620 × 640 DIP.
///  - Outer radius: 16 DIP (dashboard). The compact pill keeps its 24 DIP
///    radius — the pill is NOT part of this redesign.
///  - Edge clearance: ~3 DIP (2–4 spec) from the left edge and from the
///    bottom/taskbar boundary, so the expanded dashboard no longer touches
///    either edge. The pill stays flush at X=0 (unchanged).
///  - Dark tint ≈15% — a black @ 0.15 acrylic tint over the existing backdrop
///    (applied in MainWindow.SetAcrylicBackdrop; starting value, tuned per
///    wallpaper, not immutable).
///
/// Window envelope: the fixed HWND spans the dashboard plus the clearance on
/// the left/bottom; the taskbar strip (pill home) is added by
/// WindowService.InitializeWindow on top of <see cref="ExpandedEnvelopeHeightDip"/>.
/// </summary>
public static class HaloGeometry
{
    // ── Dashboard surface (the expanded Halo surface itself) ──────────────
    /// <summary>Dashboard surface width in DIP — PASS 1 (was 780 inside an 800 envelope).</summary>
    public const double DashboardWidthDip = 620;

    /// <summary>Dashboard surface height in DIP — PASS 1 (unchanged from 640).</summary>
    public const double DashboardHeightDip = 640;

    // ── Edge clearance ─────────────────────────────────────────────────────
    /// <summary>
    /// Visual clearance between the dashboard and the screen left edge / the
    /// taskbar boundary (2–4 DIP spec; 3 chosen). Applied as a left + bottom
    /// inset of the dashboard rect inside the window. The compact pill is not
    /// inset — it stays flush at X=0 (pill is not redesigned).
    /// </summary>
    public const double EdgeClearanceDip = 3;

    // ── Radii ──────────────────────────────────────────────────────────────
    /// <summary>Outer radius of the expanded dashboard surface + region — PASS 1 (was 24).</summary>
    public const double DashboardRadiusDip = 16;

    /// <summary>Compact pill region radius — unchanged (the pill is not redesigned).</summary>
    public const double PillRadiusDip = 24;

    // ── Window envelope (derived) ──────────────────────────────────────────
    /// <summary>Fixed HWND width in DIP: dashboard + left clearance (620 + 3).</summary>
    public const double ExpandedEnvelopeWidthDip = DashboardWidthDip + EdgeClearanceDip;

    /// <summary>
    /// Fixed HWND base height in DIP: dashboard + bottom clearance (640 + 3).
    /// WindowService.InitializeWindow adds the taskbar strip on top of this to
    /// pre-size the stable HWND; the animation tween targets this height.
    /// </summary>
    public const double ExpandedEnvelopeHeightDip = DashboardHeightDip + EdgeClearanceDip;
}
