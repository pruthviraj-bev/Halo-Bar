namespace DynamicIsland.Services;

/// <summary>
/// Raw taskbar measurements consumed by <see cref="IAnchorStrategy"/>. All
/// values are in DIPs, converted from physical pixels by
/// CompactLayoutController using the shared Scale. Pure data — no behavior.
/// </summary>
/// <param name="ReservedRight">Right edge of the reserved cluster (Start/Search/…) in DIPs.</param>
/// <param name="AppStripLeft">Left edge of the app-button strip (MSTaskSwWClass / ReBarWindow32, equal in the current layout) in DIPs — the single stable boundary a left-anchored (X=0) pill must stay left of.</param>
/// <param name="AppStripRight">Right edge of the app-button strip (MSTaskListWClass) in DIPs.</param>
/// <param name="TrayLeft">Left edge of the system tray (TrayNotifyWnd) in DIPs.</param>
/// <param name="MonitorRight">Right edge of the primary monitor's taskbar strip (Shell_TrayWnd) in DIPs — the screen-edge anchor boundary.</param>
/// <param name="HasAppStrip">True when a real app-button strip was found; false when only the reserved-cluster fallback is available.</param>
/// <param name="Scale">Device scale (physical / DIP) used to convert the raw rects.</param>
public readonly record struct TaskbarSnapshot(
    double ReservedRight,
    double AppStripLeft,
    double AppStripRight,
    double TrayLeft,
    double MonitorRight,
    bool HasAppStrip,
    double Scale);
