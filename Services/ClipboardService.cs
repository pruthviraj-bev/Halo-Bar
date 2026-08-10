using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Service responsible for monitoring and interacting with the system clipboard.
/// Exposes a cached CurrentItem and fires ClipboardChanged when new content is detected.
/// Duplicate-suppression prevents re-firing for the same copied data.
/// Also maintains a persistent multi-item History list (most-recent-first) with
/// pin, delete, retention cleanup, and image copy-back support.
/// </summary>
public class ClipboardService
{
    public ClipboardItem? CurrentItem { get; private set; }

    /// <summary>
    /// Timestamp of the most recent ReCopy SetContent call. QueryAsync uses it to
    /// recognize the OS ContentChanged that OUR OWN re-copy triggers, so that
    /// dedup does not fire ClipboardChanged and spawn a fresh transient pill.
    /// Self-heals after a short window, so it can never suppress a real capture.
    /// </summary>
    private DateTime _selfCopyUtc = DateTime.MinValue;

    /// <summary>
    /// Multi-item clipboard history, most-recent-first. Persisted to disk via ClipboardHistoryStore.
    /// </summary>
    public ObservableCollection<ClipboardItem> History { get; } = new();

    /// <summary>
    /// Non-pinned items older than this many days are removed by CleanupExpiredItems().
    /// Persisted to settings.json via ClipboardHistoryStore and selectable from the
    /// clipboard card's retention dropdown. 0 keeps everything forever.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    public event EventHandler<ClipboardItem?>? ClipboardChanged;

    public void Initialize()
    {
        // Clamp defensively (hand-edited settings.json could hold any value) — the
        // same guard SetRetentionDays applies; <= 0 means "keep forever".
        RetentionDays = Math.Max(0, ClipboardHistoryStore.LoadRetentionDays());
        LoadHistory();
        CleanupExpiredItems();
        _ = HydrateImageStreamsAsync();
        StartCleanupTimer();

        try
        {
            Clipboard.ContentChanged += OnContentChanged;
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to subscribe to ContentChanged", ex);
        }
    }

    private void LoadHistory()
    {
        foreach (var item in ClipboardHistoryStore.Load())
        {
            History.Add(item);
        }
        Helpers.Logger.Info($"ClipboardService: loaded {History.Count} history item(s)");
    }

    /// <summary>
    /// Rebuilds a live stream reference for each persisted image item so ReCopy can
    /// put it back on the clipboard without re-reading the file synchronously.
    /// </summary>
    private async Task HydrateImageStreamsAsync()
    {
        foreach (var item in History)
        {
            if (item.Type != ClipboardItemType.Image || string.IsNullOrEmpty(item.ImageFilePath)) continue;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.ImageFilePath);
                item.ImageStreamRef = RandomAccessStreamReference.CreateFromFile(file);
            }
            catch (Exception ex)
            {
                Helpers.Logger.Error("ClipboardService: failed to hydrate image stream", ex);
            }
        }
    }

    private void OnContentChanged(object? sender, object e)
    {
        _ = QueryAsync();
    }

    private readonly SemaphoreSlim _queryLock = new(1, 1);

    private async Task QueryAsync()
    {
        // Serialize clipboard queries: screenshot tools can fire ContentChanged twice within
        // milliseconds, and without a lock two overlapping calls can both pass the dedup
        // search before either has inserted — inserting duplicate entries for one capture.
        await _queryLock.WaitAsync();
        try
        {
            int retryCount = 0;
            const int maxRetries = 1;

            while (true)
            {
                try
                {
                    var data = Clipboard.GetContent();
                    if (data == null) return;

                    ClipboardItem? item = null;

                    if (data.Contains(StandardDataFormats.Text))
                    {
                        string text = await data.GetTextAsync();
                        if (string.IsNullOrEmpty(text)) return;

                        item = new ClipboardItem
                        {
                            Type = ClipboardItemType.Text,
                            RawText = text,
                            Title = text.Length > 40 ? text[..40].TrimEnd() + "…" : text,
                            Detail = $"{text.Length} characters"
                        };
                    }
                    else if (data.Contains(StandardDataFormats.StorageItems))
                    {
                        var files = await data.GetStorageItemsAsync();
                        if (files == null || files.Count == 0) return;

                        string names = string.Join(", ", files.Select(f => f.Name));
                        string title = files.Count == 1 ? files[0].Name : $"{files.Count} files";
                        item = new ClipboardItem
                        {
                            Type = ClipboardItemType.Files,
                            Title = title,
                            Detail = names,
                            FilePaths = files.Select(f => f.Path).ToList()
                        };
                    }
                    else if (data.Contains(StandardDataFormats.Bitmap))
                    {
                        var bmpRef = await data.GetBitmapAsync();
                        if (bmpRef == null) return;

                        // Read the image stream exactly ONCE, then persist and hash those
                        // bytes. Screenshot tools (e.g. Snip & Sketch) serve their
                        // delay-rendered bitmap stream only a single time — a second
                        // OpenReadAsync() fails with RPC_S_SERVER_UNAVAILABLE, leaving no
                        // file on disk and a cached stream reference that later re-copies
                        // as nothing when the user clicks the history card.
                        byte[]? imageBytes = await ReadAllBytesAsync(bmpRef);
                        if (imageBytes is not { Length: > 0 }) return;

                        item = new ClipboardItem
                        {
                            Type = ClipboardItemType.Image,
                            Title = "Image",
                            Detail = "Bitmap image",
                            ImageStreamRef = await CreateStreamRefAsync(imageBytes),
                            ImageFilePath = await ClipboardHistoryStore.SaveImageBytesAsync(imageBytes),
                            ImageHash = await ComputeImageHashAsync(imageBytes)
                        };
                    }

                    if (item != null)
                    {
                        // MRU dedup: search the ENTIRE history for a matching entry (same pairwise
                        // content comparison ItemsIdentical already does per type), not just History[0].
                        // This also swallows our own ReCopy writes, whose ContentChanged event is
                        // delivered asynchronously after SetContent has already returned.
                        ClipboardItem? match = History.FirstOrDefault(existing => ItemsIdentical(existing, item));

                        if (match != null)
                        {
                            // Re-copied an existing entry: move it to the top instead of inserting a
                            // duplicate. No SaveImageAsync — nothing new to persist, so no orphaned
                            // duplicate image file on disk for something we're about to discard.
                            int oldIndex = History.IndexOf(match);
                            match.Timestamp = DateTimeOffset.Now;
                            History.Move(oldIndex, 0);
                            ClipboardHistoryStore.Save(History);

                            CurrentItem = match;

                            // A re-copy initiated from the pill must not spawn a fresh transient
                            // pill — the widget shows its own "Copied" confirmation instead.
                            bool selfCopy = (DateTime.UtcNow - _selfCopyUtc).TotalMilliseconds < 1500;
                            if (!selfCopy)
                                ClipboardChanged?.Invoke(this, match);

                            Helpers.Logger.Info("ClipboardService: moved existing item to top (re-copied)");
                            return;
                        }

                        // Skip doomed image captures entirely (RPC_S_SERVER_UNAVAILABLE when the
                        // clipboard source stream is gone): no pixels to hash AND no file saved —
                        // inserting a card with a blank thumbnail would just need manual deletion.
                        if (item.Type == ClipboardItemType.Image
                            && string.IsNullOrEmpty(item.ImageHash)
                            && string.IsNullOrEmpty(item.ImageFilePath))
                        {
                            Helpers.Logger.Info("ClipboardService: skipped unreadable image capture (no pixels, no saved file)");
                            return;
                        }

                        item.Timestamp = DateTimeOffset.Now;
                        History.Insert(0, item);
                        ClipboardHistoryStore.Save(History);

                        CurrentItem = item;
                        ClipboardChanged?.Invoke(this, item);
                        Helpers.Logger.Info($"ClipboardService: successfully queried item. Type={item.Type}, Title='{item.Title}', Detail='{item.Detail}'");
                    }

                    break; // Success, exit retry loop
                }
                catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x800401D0)
                {
                    if (retryCount < maxRetries)
                    {
                        retryCount++;
                        Helpers.Logger.Info($"ClipboardService: Clipboard busy (0x800401D0). Retrying query in 100ms... (Attempt {retryCount}/{maxRetries})");
                        await Task.Delay(100);
                        continue;
                    }

                    Helpers.Logger.Error("ClipboardService: error reading clipboard after retry", ex);
                    break;
                }
                catch (Exception ex)
                {
                    Helpers.Logger.Error("ClipboardService: error reading clipboard", ex);
                    break;
                }
            }
        }
        finally
        {
            _queryLock.Release();
        }
    }

    private static bool ItemsIdentical(ClipboardItem a, ClipboardItem b)
    {
        if (a.Type != b.Type) return false;
        return a.Type switch
        {
            ClipboardItemType.Text => string.Equals(a.RawText, b.RawText, StringComparison.Ordinal),
            ClipboardItemType.Files => string.Equals(a.Detail, b.Detail, StringComparison.Ordinal),
            ClipboardItemType.Image => !string.IsNullOrEmpty(a.ImageHash)
                && string.Equals(a.ImageHash, b.ImageHash, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Computes a SHA-256 over the decoded pixel buffer (Bgra8, premultiplied alpha) so the
    /// hash reflects visual content rather than the encoded stream bytes, which clipboard
    /// format negotiation can re-encode differently between captures. Returns null when the
    /// image cannot be decoded. Operates on already-materialized bytes so the clipboard's
    /// single-shot image stream is opened exactly once.
    /// </summary>
    private static async Task<string?> ComputeImageHashAsync(byte[] imageBytes)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var frame = await decoder.GetFrameAsync(0);
            var pixelData = await frame.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(pixelData.DetachPixelData()));
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardService: failed to hash image", ex);
            return null;
        }
    }

    /// <summary>
    /// Reads the entire image stream once into memory. Screenshot tools serve their
    /// delay-rendered clipboard bitmap only a single time, so this must be the ONLY place
    /// the source stream is opened; everything downstream works from the materialized bytes.
    /// </summary>
    private static async Task<byte[]?> ReadAllBytesAsync(RandomAccessStreamReference streamRef)
    {
        try
        {
            using var src = await streamRef.OpenReadAsync();
            using var ms = new MemoryStream();
            await src.AsStreamForRead().CopyToAsync(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardService: failed to read image stream", ex);
            return null;
        }
    }

    /// <summary>
    /// Builds a live, in-memory stream reference from materialized bytes so the transient
    /// ClipboardWidget can preview the capture without touching the (now-consumed) clipboard source.
    /// </summary>
    private static async Task<RandomAccessStreamReference?> CreateStreamRefAsync(byte[] imageBytes)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);
            return RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardService: failed to build image stream reference", ex);
            return null;
        }
    }

    public void TogglePin(ClipboardItem item)
    {
        if (item == null) return;
        item.IsPinned = !item.IsPinned;
        ClipboardHistoryStore.Save(History);
    }

    public void DeleteItem(ClipboardItem item)
    {
        if (item == null) return;
        if (History.Remove(item))
        {
            if (!string.IsNullOrEmpty(item.ImageFilePath))
            {
                ClipboardHistoryStore.DeleteImageFile(item.ImageFilePath);
            }
            ClipboardHistoryStore.Save(History);
        }
    }

    /// <summary>
    /// Removes non-pinned items older than RetentionDays, deleting their image files.
    /// Pinned items are exempt until explicitly unpinned or deleted.
    /// </summary>
    public void CleanupExpiredItems()
    {
        if (History.Count == 0 || RetentionDays <= 0) return;

        var cutoff = DateTimeOffset.Now - TimeSpan.FromDays(RetentionDays);
        bool changed = false;

        for (int i = History.Count - 1; i >= 0; i--)
        {
            var item = History[i];
            if (item.IsPinned) continue;
            if (item.Timestamp < cutoff)
            {
                if (!string.IsNullOrEmpty(item.ImageFilePath))
                {
                    ClipboardHistoryStore.DeleteImageFile(item.ImageFilePath);
                }
                History.RemoveAt(i);
                changed = true;
            }
        }

        if (changed)
        {
            ClipboardHistoryStore.Save(History);
        }
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _cleanupTimer;

    /// <summary>
    /// Starts a low-frequency timer so expired items are pruned even while the app
    /// stays open for days on end (cleanup previously ran only at startup).
    /// </summary>
    private void StartCleanupTimer()
    {
        try
        {
            _cleanupTimer = new Microsoft.UI.Xaml.DispatcherTimer
            {
                Interval = TimeSpan.FromHours(6)
            };
            _cleanupTimer.Tick += (_, _) => CleanupExpiredItems();
            _cleanupTimer.Start();
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to start retention cleanup timer", ex);
        }
    }

    /// <summary>
    /// Updates the retention period, persists the choice, and immediately prunes
    /// items that exceed the new cutoff. Passing 0 keeps everything forever.
    /// </summary>
    public void SetRetentionDays(int days)
    {
        RetentionDays = Math.Max(0, days);
        ClipboardHistoryStore.SaveRetentionDays(RetentionDays);
        CleanupExpiredItems();
    }

    public void Clear()
    {
        try
        {
            Clipboard.Clear();
            CurrentItem = null;
            ClipboardChanged?.Invoke(this, null);
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to clear clipboard", ex);
        }
    }

    /// <summary>
    /// Removes an item from the persisted clipboard history and deletes its
    /// associated image file. Leaves the OS clipboard untouched.
    /// </summary>
    public void RemoveFromHistory(ClipboardItem item)
    {
        if (item == null || !History.Remove(item)) return;

        ClipboardHistoryStore.DeleteImageFile(item.ImageFilePath);
        ClipboardHistoryStore.Save(History);

        if (ReferenceEquals(CurrentItem, item))
            CurrentItem = null;

        Helpers.Logger.Info("ClipboardService: removed item from history");
    }

    public void ReCopy(ClipboardItem item)
    {
        if (item == null) return;
        try
        {
            var pkg = new DataPackage();
            switch (item.Type)
            {
                case ClipboardItemType.Text:
                    if (string.IsNullOrEmpty(item.RawText)) return;
                    pkg.SetText(item.RawText);
                    break;

                case ClipboardItemType.Image:
                    // Prefer a fresh, file-backed stream read from disk: a cached
                    // ImageStreamRef can be stale/dead shortly after capture (delay-rendered
                    // clipboard), silently producing unpasteable content. Only fall back to
                    // the cached live reference when no file was ever persisted.
                    var streamRef = !string.IsNullOrEmpty(item.ImageFilePath)
                        ? LoadImageStreamRefFromPath(item.ImageFilePath)
                        : item.ImageStreamRef;
                    if (streamRef == null) return;
                    pkg.SetBitmap(streamRef);
                    break;

                case ClipboardItemType.Files:
                    if (item.FilePaths == null || item.FilePaths.Count == 0) return;

                    var storageItems = new List<IStorageItem>(item.FilePaths.Count);
                    foreach (var path in item.FilePaths)
                    {
                        try
                        {
                            storageItems.Add(StorageFile.GetFileFromPathAsync(path).GetAwaiter().GetResult());
                        }
                        catch
                        {
                            try
                            {
                                storageItems.Add(StorageFolder.GetFolderFromPathAsync(path).GetAwaiter().GetResult());
                            }
                            catch
                            {
                                // Skip paths that can no longer be resolved.
                            }
                        }
                    }

                    if (storageItems.Count == 0) return;
                    pkg.SetStorageItems(storageItems);
                    break;
            }
            Clipboard.SetContent(pkg);
            _selfCopyUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to re-copy item", ex);
        }
    }

    /// <summary>
    /// Synchronous fallback for re-copying a persisted image before its stream ref has
    /// been hydrated. Reads the bytes into an in-memory stream and wraps it.
    /// </summary>
    private static RandomAccessStreamReference? LoadImageStreamRefFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var fileStream = File.OpenRead(path))
            {
                var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(ReadAllBytes(fileStream));
                writer.StoreAsync().GetAwaiter().GetResult();
                writer.DetachStream(); // stream stays alive, owned by the reference
            }
            stream.Seek(0);
            return RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("ClipboardService: failed to load image for re-copy", ex);
            return null;
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _cleanupTimer?.Stop();
        Clipboard.ContentChanged -= OnContentChanged;
    }
}

public enum ClipboardItemType { Text, Image, Files }

/// <summary>
/// Immutable-style model representing a captured clipboard snapshot.
/// ImageStreamRef is a live stream kept in memory only; ImageFilePath is what
/// gets persisted to disk and used to reconstruct the stream after a restart.
/// </summary>
public class ClipboardItem
{
    public ClipboardItemType Type { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? RawText { get; set; }
    public string? ImageFilePath { get; set; }
    public string? ImageHash { get; set; }
    public List<string>? FilePaths { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    [JsonIgnore]
    public RandomAccessStreamReference? ImageStreamRef { get; set; }
}
