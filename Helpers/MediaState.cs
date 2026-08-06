using System;
using Windows.Media;
using Windows.Storage.Streams;

namespace DynamicIsland.Helpers;

/// <summary>
/// Immutable snapshot representing the active system media state.
/// </summary>
public class MediaState
{
    public string Title { get; }
    public string Artist { get; }
    public IRandomAccessStreamReference? Thumbnail { get; }
    public bool IsPlaying { get; }
    public TimeSpan Position { get; }
    public TimeSpan Duration { get; }
    public DateTimeOffset LastUpdatedTime { get; }
    public MediaPlaybackAutoRepeatMode? AutoRepeatMode { get; }
    public string SourceAppUserModelId { get; }
    public string SourceName { get; }

    public MediaState(
        string title,
        string artist,
        IRandomAccessStreamReference? thumbnail,
        bool isPlaying,
        TimeSpan position = default,
        TimeSpan duration = default,
        DateTimeOffset? lastUpdatedTime = null,
        MediaPlaybackAutoRepeatMode? autoRepeatMode = null,
        string sourceAppUserModelId = "",
        string sourceName = "Unknown Source")
    {
        Title = title;
        Artist = artist;
        Thumbnail = thumbnail;
        IsPlaying = isPlaying;
        Position = position;
        Duration = duration;
        LastUpdatedTime = lastUpdatedTime ?? DateTimeOffset.Now;
        AutoRepeatMode = autoRepeatMode;
        SourceAppUserModelId = sourceAppUserModelId;
        SourceName = sourceName;
    }
}
