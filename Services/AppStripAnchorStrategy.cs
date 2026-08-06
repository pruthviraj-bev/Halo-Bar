using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Anchors the pill immediately right of the app-button strip
/// (MSTaskListWClass): X = stripRight + gap, choosing the widest gap tier that
/// still leaves at least <see cref="CompactLayoutController.CompactMinWidth"/>
/// DIPs of free room ahead of the tray. When no app strip is present, the
/// reserved-cluster right edge is used instead (ReservedRight + gap). If even
/// the narrowest tier cannot fit CompactMinWidth, the pill degrades honestly to
/// whatever space genuinely remains — possibly narrower than CompactMinWidth,
/// floored at 0 — rather than claiming a width it can't back up and overlapping
/// the tray.
/// </summary>
public sealed class AppStripAnchorStrategy : IAnchorStrategy
{
    private readonly double _safetyMargin;

    // Widest-to-narrowest visual breathing room between the app strip and the
    // pill. The final tier is the configured halo gap (CompactLayoutController.
    // HaloGap, 16 DIP by default) — the historical minimum. A named local array
    // is enough clarity here; there is no design-token class yet to migrate
    // these into.
    private readonly double[] _gaps;

    public AppStripAnchorStrategy(double haloGap, double safetyMargin)
    {
        _safetyMargin = safetyMargin;
        _gaps = new[] { 48, 40, 24, haloGap };
    }

    /// <inheritdoc/>
    public AnchorResult Resolve(TaskbarSnapshot snapshot)
    {
        double gap = SelectGap(snapshot, out double width);
        double haloX = snapshot.HasAppStrip
            ? snapshot.AppStripRight + gap
            : snapshot.ReservedRight + gap;

        return new AnchorResult(haloX, width);
    }

    /// <summary>
    /// Picks the widest gap tier whose anchor still leaves at least
    /// <see cref="CompactLayoutController.CompactMinWidth"/> DIPs before the
    /// tray. If no tier fits, uses the narrowest gap and takes whatever space
    /// genuinely remains, floored at 0 — never a width that pushes past the
    /// tray. Logs the chosen gap, anchor X, resulting width, and which tier
    /// decided the layout.
    /// </summary>
    private double SelectGap(TaskbarSnapshot snapshot, out double width)
    {
        double stripRight = snapshot.HasAppStrip ? snapshot.AppStripRight : snapshot.ReservedRight;

        foreach (double gap in _gaps)
        {
            double x = stripRight + gap;
            double available = snapshot.TrayLeft - x - _safetyMargin;
            if (available >= CompactLayoutController.CompactMinWidth)
            {
                width = Math.Min(CompactLayoutController.CompactIdealWidth, available);
                Logger.Info($"[ANCHOR-STRIP] gap={gap} x={x:F1} width={width:F1} tier=preferred");
                return gap;
            }
        }

        // No tier fit CompactMinWidth cleanly. Use the narrowest gap and take
        // whatever space genuinely remains — floor at 0, never claim a width
        // that would push past the tray. This can legitimately be narrower than
        // CompactMinWidth on extremely crowded taskbars.
        double lastGap = _gaps[^1];
        double lastX = stripRight + lastGap;
        width = Math.Max(0, snapshot.TrayLeft - lastX - _safetyMargin);
        Logger.Info($"[ANCHOR-STRIP] gap={lastGap} x={lastX:F1} width={width:F1} tier=minimal-fit");
        return lastGap;
    }
}
