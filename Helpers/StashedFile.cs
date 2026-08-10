using System;
using Microsoft.UI.Xaml.Media;

namespace DynamicIsland.Helpers;

/// <summary>
/// Snapshot of a single file or folder staged in the File Shelf.
/// Session-only — never persisted to disk. Path/Name/IsFolder/AddedAt are
/// immutable; <see cref="Thumbnail"/> is a runtime-only presentation cache
/// field populated asynchronously by the shelf host for grid display.
/// </summary>
public sealed class StashedFile
{
    /// <summary>Full absolute path to the file or folder.</summary>
    public string Path { get; }

    /// <summary>Display name (file/folder name only, no directory).</summary>
    public string Name { get; }

    /// <summary>True if this entry is a directory; false if a file.</summary>
    public bool IsFolder { get; }

    /// <summary>Timestamp when this item was added to the shelf.</summary>
    public DateTimeOffset AddedAt { get; }

    /// <summary>
    /// Lazily-loaded shell thumbnail for grid display. Null until loaded, or
    /// when the shell provides no thumbnail (the UI falls back to the
    /// Folder/Document glyph). Not part of the immutable snapshot.
    /// </summary>
    public ImageSource? Thumbnail { get; set; }

    public StashedFile(string path, bool isFolder)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        IsFolder = isFolder;
        AddedAt = DateTimeOffset.Now;
    }
}