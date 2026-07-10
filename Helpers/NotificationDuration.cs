using System;

namespace DynamicIsland.Helpers;

/// <summary>
/// Centralised auto-dismiss durations for transient island notifications.
/// IslandController.Push() references these constants — no raw TimeSpan literals
/// should appear in event handlers.
/// </summary>
public static class NotificationDuration
{
    /// <summary>2 s — transient HUD notifications (Volume).</summary>
    public static readonly TimeSpan Short    = TimeSpan.FromSeconds(2);

    /// <summary>3 s — short-lived informational events (Clipboard).</summary>
    public static readonly TimeSpan Brief    = TimeSpan.FromSeconds(3);

    /// <summary>4 s — standard system events (charging started / stopped).</summary>
    public static readonly TimeSpan Standard = TimeSpan.FromSeconds(4);

    /// <summary>6 s — warnings that require user attention (low battery).</summary>
    public static readonly TimeSpan Extended = TimeSpan.FromSeconds(6);

    /// <summary>8 s — critical alerts (battery ≤ 10%).</summary>
    public static readonly TimeSpan Critical = TimeSpan.FromSeconds(8);
}
