namespace DynamicIsland.Helpers;

/// <summary>
/// Immutable snapshot representing system output volume.
/// </summary>
public sealed record VolumeState(
    int VolumePercent,
    bool IsMuted
);

