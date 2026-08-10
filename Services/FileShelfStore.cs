using System;
using System.Collections.Generic;
using System.IO;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Session-only store for files and folders staged in the File Shelf.
/// Maximum 10 items. Never persisted to disk.
/// All public members are safe to call from the UI thread only.
/// </summary>
public sealed class FileShelfStore
{
    public const int MaxItems = 10;

    private readonly List<StashedFile> _items = new();

    /// <summary>Current staged items, in add order.</summary>
    public IReadOnlyList<StashedFile> Items => _items;

    /// <summary>True when the shelf has reached MaxItems.</summary>
    public bool IsFull => _items.Count >= MaxItems;

    /// <summary>True when the shelf has no items.</summary>
    public bool IsEmpty => _items.Count == 0;

    /// <summary>
    /// Raised on the UI thread whenever Items changes
    /// (item added, removed, or cleared).
    /// </summary>
    public event EventHandler? ItemsChanged;

    /// <summary>
    /// Attempts to add a file or folder to the shelf.
    /// Returns false if the shelf is full or the path is already staged.
    /// Returns true if the item was added.
    /// </summary>
    public bool TryAdd(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsFull) return false;

        // Normalize path for duplicate detection.
        string normalized = Path.GetFullPath(path).TrimEnd('\\', '/');

        // Reject duplicates (case-insensitive, Windows paths).
        foreach (var existing in _items)
        {
            if (string.Equals(
                    Path.GetFullPath(existing.Path).TrimEnd('\\', '/'),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }

        bool isFolder = Directory.Exists(path);
        _items.Add(new StashedFile(path, isFolder));
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Removes a staged item by path. No-op if not found.
    /// </summary>
    public void Remove(string path)
    {
        string normalized = Path.GetFullPath(path).TrimEnd('\\', '/');
        int index = _items.FindIndex(f =>
            string.Equals(
                Path.GetFullPath(f.Path).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (index < 0) return;
        _items.RemoveAt(index);
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes all staged items.
    /// </summary>
    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }
}