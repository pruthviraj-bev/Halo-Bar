using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DynamicIsland.Services;

namespace DynamicIsland.Helpers;

/// <summary>
/// Persists clipboard history to disk under %LOCALAPPDATA%\DynamicIsland\clipboard.
/// Image items have their bytes written to the images\ subfolder and the JSON entry
/// stores the absolute file path instead of the bytes themselves.
/// </summary>
public static class ClipboardHistoryStore
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland",
        "clipboard"
    );

    private static readonly string HistoryFile = Path.Combine(BaseDir, "history.json");
    private static readonly string ImagesDir = Path.Combine(BaseDir, "images");
    private static readonly string SettingsFile = Path.Combine(BaseDir, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact JSON: the 470+ item history was serialized indented at ~5.7 KB
        // per item (2.68 MB today). Compact encoding roughly halves the file size,
        // the per-save serialization string, and startup parse time. The file is
        // internal — deserialization is format-agnostic.
        WriteIndented = false
    };

    /// <summary>
    /// Reads history.json and returns the stored items. Returns an empty list when
    /// the file is missing or unreadable — never throws.
    /// </summary>
    public static List<ClipboardItem> Load()
    {
        try
        {
            if (!File.Exists(HistoryFile))
            {
                return new List<ClipboardItem>();
            }

            var json = File.ReadAllText(HistoryFile);
            return JsonSerializer.Deserialize<List<ClipboardItem>>(json, SerializerOptions)
                ?? new List<ClipboardItem>();
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardHistoryStore: failed to load history", ex);
            return new List<ClipboardItem>();
        }
    }

    // ── Background persistence queue ────────────────────────────────────────
    // History writes used to be a synchronous File.WriteAllText on the UI thread
    // (every capture, pin, delete — the capture path especially). Writes are now
    // queued: the caller snapshots the list (a cheap shallow copy), and a single
    // background drainer writes the LATEST state, coalescing bursts (e.g. rapid
    // captures) into one disk write of the final state. Order is preserved
    // because each new snapshot supersedes the previous one — the last queued
    // state is always what lands on disk.
    private static readonly object SaveLock = new();
    private static List<ClipboardItem>? _latestSnapshot;
    private static bool _draining;

    /// <summary>
    /// Queues a full-history write to disk. The list is shallow-copied
    /// immediately on the caller's thread (O(n) reference copies); serialization
    /// and file I/O run on a background thread. Coalesced, ordered, never
    /// throws. No caller should rely on the file being updated synchronously.
    /// </summary>
    public static void QueueSave(IEnumerable<ClipboardItem> history)
    {
        Logger.Info($"[PROFILE] QueueSave start ms={Environment.TickCount64}");
        var snapshot = history.ToList();

        bool startDrain;
        lock (SaveLock)
        {
            _latestSnapshot = snapshot;
            startDrain = !_draining;
            if (startDrain) _draining = true;
        }

        if (startDrain)
            _ = DrainAsync();
    }

    private static async Task DrainAsync()
    {
        while (true)
        {
            List<ClipboardItem>? toWrite;
            lock (SaveLock)
            {
                toWrite = _latestSnapshot;
                _latestSnapshot = null;
                if (toWrite == null)
                {
                    // No snapshot queued — this drainer is done. Releasing the
                    // lock with _draining = false lets the next QueueSave start
                    // its own drain; a snapshot can never slip in between the
                    // null check and the flag clear because QueueSave needs this
                    // same lock.
                    _draining = false;
                    return;
                }
            }

            try
            {
                // ConfigureAwait(false): the drain loop must never hop back to
                // the caller's (UI) SynchronizationContext.
                Logger.Info($"[PROFILE] DrainAsync write start ms={Environment.TickCount64}");
                await Task.Run(() => SaveCore(toWrite)).ConfigureAwait(false);
                Logger.Info($"[PROFILE] DrainAsync write end ms={Environment.TickCount64}");
            }
            catch (Exception ex)
            {
                Logger.Error("ClipboardHistoryStore: background save failed", ex);
            }
        }
    }

    /// <summary>
    /// Writes the full history list to history.json. Never throws.
    /// </summary>
    private static void SaveCore(IEnumerable<ClipboardItem> history)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var json = JsonSerializer.Serialize(history, SerializerOptions);
            File.WriteAllText(HistoryFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardHistoryStore: failed to save history", ex);
        }
    }

    /// <summary>
    /// Writes a captured image's bytes to the images\ subfolder and returns the absolute
    /// file path, or null if the image could not be persisted. Takes the materialized bytes
    /// (not a stream reference) because screenshot tools serve their delay-rendered clipboard
    /// bitmap only once — the bytes are read a single time upstream in ClipboardService.
    /// </summary>
    public static async Task<string?> SaveImageBytesAsync(byte[] imageBytes)
    {
        try
        {
            Directory.CreateDirectory(ImagesDir);
            var path = Path.Combine(ImagesDir, Guid.NewGuid().ToString("N") + ".png");
            await File.WriteAllBytesAsync(path, imageBytes);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardHistoryStore: failed to save image", ex);
            return null;
        }
    }

    /// <summary>
    /// Deletes an image file if it exists. Safe to call with null or missing paths.
    /// </summary>
    public static void DeleteImageFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardHistoryStore: failed to delete image file", ex);
        }
    }

    // ── Clipboard settings (retention) ─────────────────────────────────────

    private sealed class ClipboardSettingsFile
    {
        public int RetentionDays { get; set; }
    }

    /// <summary>
    /// Reads the persisted retention period (days) from settings.json. Returns the
    /// 30-day default when the file is missing or unreadable — never throws.
    /// </summary>
    public static int LoadRetentionDays()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return 30;
            }

            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<ClipboardSettingsFile>(json, SerializerOptions)?.RetentionDays ?? 30;
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardHistoryStore: failed to load clipboard settings", ex);
            return 30;
        }
    }

    /// <summary>
    /// Persists the retention period (days). Never throws.
    /// </summary>
    public static void SaveRetentionDays(int days)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var json = JsonSerializer.Serialize(new ClipboardSettingsFile { RetentionDays = days }, SerializerOptions);
            File.WriteAllText(SettingsFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("ClipboardHistoryStore: failed to save clipboard settings", ex);
        }
    }
}
