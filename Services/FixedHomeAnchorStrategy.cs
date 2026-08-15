using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Single, fixed home position for the pill: X = 4 (the screen/taskbar left
/// edge plus a 4 DIP offset, so the pill does not sit flush against the screen
/// wall). Only the width responds to the taskbar — it takes however much room
/// genuinely exists between the left edge and the start of the app-button
/// strip, minus <see cref="CompactLayoutController.SafetyMargin"/> and an
/// <see cref="UnmeasuredIconBuffer"/> for the Start/Search visuals that sit
/// left of the strip, floored at 0 and capped at
/// <see cref="CompactLayoutController.CompactIdealWidth"/>. The position never
/// moves.
///
/// The width is bounded by the app strip's <em>left</em> edge
/// (<see cref="TaskbarSnapshot.AppStripLeft"/>, i.e. MSTaskSwWClass.Left ==
/// ReBarWindow32.Left in the current layout). Start/Search are deliberately
/// <em>not</em> detected: icon mode renders them XAML-only with no HWND, which
/// made reserved-cluster detection flaky. The strip's left edge is measurable
/// in every layout and is stable, so it is the single boundary used here.
///
/// Width uses a "never claim a width you can't back up" clamp —
/// <c>Math.Min(ideal, Math.Max(0, available))</c> — with no lower floor, so a
/// crowded taskbar degrades honestly instead of the pill re-overlapping the
/// tray.
/// </summary>
public sealed class FixedHomeAnchorStrategy : IAnchorStrategy
{
    // Observed this session: Start/Search visuals begin ~33 DIP left of
    // AppStripLeft (368.8 − 336), and that gap is not reliably measurable (icon
    // mode exposes no HWND). Buffer 40 DIP on top of SafetyMargin so the pill can
    // never reach the Start icon even when the pill takes its full ideal width.
    // Pragmatic trade: a little pill width for guaranteed non-overlap.
    private const double UnmeasuredIconBuffer = 40;

    // Last values actually announced, so steady-state polls don't spam the log —
    // the same change-only pattern as WindowService.ApplyGeometry.
    private double _lastLoggedBoundary = double.NegativeInfinity;
    private double _lastLoggedWidth = double.NegativeInfinity;

    /// <inheritdoc/>
    public AnchorResult Resolve(TaskbarSnapshot snapshot)
    {
        // Home is X=4 (taskbar left edge + 4 DIP offset so the pill is not
        // glued to the screen wall). Width responds to how much room is free
        // until the app strip begins, less the margins (SafetyMargin for the
        // strip itself + UnmeasuredIconBuffer for the Start/Search visuals that
        // sit left of the strip but are not reliably measurable); position
        // never moves.
        const double LeftOffsetDip = 4;
        double boundary = snapshot.AppStripLeft;
        double available = boundary - CompactLayoutController.SafetyMargin - UnmeasuredIconBuffer;
        double width = Math.Min(CompactLayoutController.CompactIdealWidth, Math.Max(0, available));

        if (Math.Abs(boundary - _lastLoggedBoundary) >= 1.0
            || Math.Abs(width - _lastLoggedWidth) >= 1.0)
        {
            _lastLoggedBoundary = boundary;
            _lastLoggedWidth = width;
            Logger.Info($"[ANCHOR-FIXEDHOME] x={LeftOffsetDip} width={width:F1} available={available:F1} boundary={boundary:F1} buffer={UnmeasuredIconBuffer} source=appStripLeft");
        }

        return new AnchorResult(LeftOffsetDip, width);
    }
}
