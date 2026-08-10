namespace DynamicIsland.Interfaces;

/// <summary>
/// Contract for a single ambient card in the PillDashboard card host.
/// Each card owns its visibility, width, and view.
/// Cards know nothing about adjacent cards or the host.
/// </summary>
public interface IPillCard
{
    /// <summary>
    /// Whether this card should currently be included in the pill layout.
    /// The PillDashboard host shows/hides and animates based on this value.
    /// </summary>
    bool ShouldShow { get; }

    /// <summary>
    /// The desired width of this card in DIPs when visible.
    /// The host sums all visible card widths to compute total pill width.
    /// </summary>
    double CardWidth { get; }

    /// <summary>
    /// The UserControl that renders this card's content.
    /// </summary>
    Microsoft.UI.Xaml.Controls.UserControl View { get; }

    /// <summary>
    /// Raised on the UI thread whenever ShouldShow or CardWidth changes.
    /// The host re-evaluates layout on every raise.
    /// </summary>
    event EventHandler? StateChanged;
}
