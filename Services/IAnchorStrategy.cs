namespace DynamicIsland.Services;

/// <summary>
/// Resolves where the compact pill should be anchored given a
/// <see cref="TaskbarSnapshot"/>. Strategies are pure layout logic — they never
/// measure Windows themselves, they receive an already-DIP-converted snapshot
/// and return where the pill's left edge should go plus how much horizontal
/// room it may use. Swapping a strategy changes anchoring without touching
/// CompactLayoutController, WindowService, or any widget code.
/// </summary>
public interface IAnchorStrategy
{
    /// <summary>Computes the pill's anchor for the given taskbar measurements.</summary>
    AnchorResult Resolve(TaskbarSnapshot snapshot);
}

/// <summary>Anchoring decision for the compact pill, in DIPs.</summary>
public readonly record struct AnchorResult(double X, double MaxAvailableWidth);
