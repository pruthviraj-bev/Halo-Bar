using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Composite anchor implementing the "Preferred → Validate → Fallback" design.
/// Resolves the preferred strategy's candidate, checks whether it would collide
/// with the app-button strip (left edge) or the system tray (right edge), and
/// falls back to the fallback strategy when either collision is detected. Every
/// resolution is logged so the chosen branch is traceable in app.log without a
/// debugger.
/// </summary>
public sealed class ValidatingAnchorStrategy : IAnchorStrategy
{
    private readonly IAnchorStrategy _preferred;
    private readonly IAnchorStrategy _fallback;
    private readonly double _haloGap;
    private readonly double _safetyMargin;

    /// <param name="preferred">Strategy whose position is used when no collision is detected.</param>
    /// <param name="fallback">Strategy used when the preferred position collides with the app-button strip or the tray.</param>
    /// <param name="haloGap">Clearance (in DIPs) the pill must keep from the app-button strip's right edge.</param>
    /// <param name="safetyMargin">Clearance (in DIPs) the pill must keep from the system tray's left edge.</param>
    public ValidatingAnchorStrategy(IAnchorStrategy preferred, IAnchorStrategy fallback, double haloGap, double safetyMargin)
    {
        _preferred = preferred;
        _fallback = fallback;
        _haloGap = haloGap;
        _safetyMargin = safetyMargin;
    }

    /// <inheritdoc/>
    public AnchorResult Resolve(TaskbarSnapshot snapshot)
    {
        AnchorResult candidate = _preferred.Resolve(snapshot);

        // Left collision: the preferred pill's left edge would land inside or
        // before the app-button strip's right edge + gap.
        bool collidesLeft = candidate.X < snapshot.AppStripRight + _haloGap;

        // Right collision: the preferred pill's right edge would reach into or
        // past the tray minus its safety margin.
        bool collidesRight = candidate.X + candidate.MaxAvailableWidth > snapshot.TrayLeft - _safetyMargin;

        bool collides = collidesLeft || collidesRight;

        AnchorResult chosen = collides ? _fallback.Resolve(snapshot) : candidate;
        string used = collides ? "appStrip" : "screenEdge";

        Logger.Info($"[ANCHOR] preferredX={candidate.X:F1} preferredW={candidate.MaxAvailableWidth:F1} collidesLeft={collidesLeft} collidesRight={collidesRight} collision={collides} using={used}");
        return chosen;
    }
}
