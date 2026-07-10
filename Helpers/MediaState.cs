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

    public MediaState(string title, string artist, IRandomAccessStreamReference? thumbnail, bool isPlaying)
    {
        Title = title;
        Artist = artist;
        Thumbnail = thumbnail;
        IsPlaying = isPlaying;
    }
}
