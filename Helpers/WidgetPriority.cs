namespace DynamicIsland.Helpers;

/// <summary>
/// Display priority for all island widgets.
/// Higher value = shown in front when multiple widgets are active simultaneously.
/// Adding a new widget: pick a value between existing ones; no existing code changes.
/// </summary>
public enum WidgetPriority
{
    Default   = 0,
    Media     = 10,
    Battery   = 15,
    Volume    = 18,
    Clipboard = 20,
    Alert     = 30,
}
