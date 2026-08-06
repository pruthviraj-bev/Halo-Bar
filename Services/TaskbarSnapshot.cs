namespace DynamicIsland.Services;

/// <summary>
/// Raw taskbar measurements consumed by <see cref="IAnchorStrategy"/>. All
/// values are in DIPs, converted from physical pixels by
/// CompactLayoutController using the shared Scale. Pure data — no behavior.
/// </summary>
/// <param name="AppStripLeft">Left edge of the app-button strip (MSTaskSwWClass / ReBarWindow32, equal in the current layout) in DIPs — the single, stable boundary a left-anchored (X=0) pill must stay left of.</param>
public readonly record struct TaskbarSnapshot(double AppStripLeft);
