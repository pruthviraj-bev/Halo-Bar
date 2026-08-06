namespace DynamicIsland.Services;

/// <summary>
/// Preferred anchor: positions the pill at a fixed offset from the monitor's
/// right edge, independent of Explorer's taskbar contents:
/// X = MonitorRight − EdgeMargin − PreferredWidth.
///
/// This strategy is intentionally dumb — it always returns the same preferred
/// position and width and performs no collision validation itself. Validation
/// and fallback are the responsibility of <see cref="ValidatingAnchorStrategy"/>.
/// </summary>
public sealed class ScreenEdgeAnchorStrategy : IAnchorStrategy
{
    /// <summary>Fixed offset from the monitor's right edge, in DIPs.</summary>
    public const double EdgeMargin = 20;

    private readonly double _edgeMargin;
    private readonly double _preferredWidth;

    /// <param name="edgeMargin">Distance from the monitor's right edge, in DIPs.</param>
    /// <param name="preferredWidth">Preferred pill width, in DIPs. Defaults to the controller's compact ideal width.</param>
    public ScreenEdgeAnchorStrategy(
        double edgeMargin = EdgeMargin,
        double preferredWidth = CompactLayoutController.CompactIdealWidth)
    {
        _edgeMargin = edgeMargin;
        _preferredWidth = preferredWidth;
    }

    /// <inheritdoc/>
    public AnchorResult Resolve(TaskbarSnapshot snapshot)
    {
        double x = snapshot.MonitorRight - _edgeMargin - _preferredWidth;
        return new AnchorResult(x, _preferredWidth);
    }
}
