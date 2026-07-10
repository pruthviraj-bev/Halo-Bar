using DynamicIsland.Helpers;

namespace DynamicIsland.Interfaces;

/// <summary>
/// Lifecycle contract for all Dynamic Island widgets.
///
/// IslandController calls these methods at the correct moments during widget
/// stack transitions. Widgets must never call WindowService directly.
/// </summary>
public interface IIslandWidget
{
    /// <summary>
    /// Sort order. Higher = shown first. Defined in <see cref="Helpers.WidgetPriority"/>.
    ///   Media=10, Battery=15, Clipboard=20, Alert=30
    /// </summary>
    Helpers.WidgetPriority Priority { get; }

    /// <summary>
    /// When true, IslandController immediately expands the window upon activation
    /// regardless of hover state. Use for transient notifications (Clipboard, Alerts).
    /// When false, the window only expands when the user hovers (Media).
    /// </summary>
    bool AutoExpand { get; }

    /// <summary>Window profile requested when this widget is the active foreground widget.</summary>
    WindowProfile PreferredProfile { get; }

    /// <summary>Called once when this widget becomes the foreground widget.</summary>
    void OnActivated();

    /// <summary>Called once when this widget is permanently removed from the stack.</summary>
    void OnDeactivated();

    /// <summary>Called when a higher-priority widget temporarily covers this widget.</summary>
    void OnSuspended();

    /// <summary>Called when this widget is uncovered and becomes the foreground widget again.</summary>
    void OnResumed();
}
