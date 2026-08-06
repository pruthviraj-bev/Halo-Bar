using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DynamicIsland.Helpers;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Persists named focus sessions to disk under %LOCALAPPDATA%\DynamicIsland.
/// On the first-ever run (no file exists) it seeds exactly one default session
/// (Name = "Focus", DurationSeconds = 1500) so the dashboard always has something
/// to read. Never throws.
/// </summary>
public static class FocusSessionStore
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland"
    );

    private static readonly string SessionsFile = Path.Combine(BaseDir, "focus_sessions.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Reads focus_sessions.json and returns the stored sessions. When the file is
    /// missing (first run) or unreadable, seeds and persists exactly one default
    /// session (Name = "Focus", DurationSeconds = 1500) and returns that.
    /// </summary>
    public static List<FocusSession> LoadAll()
    {
        try
        {
            if (!File.Exists(SessionsFile))
            {
                return SeedAndSave();
            }

            var json = File.ReadAllText(SessionsFile);
            var sessions = JsonSerializer.Deserialize<List<FocusSession>>(json, SerializerOptions);
            if (sessions == null || sessions.Count == 0)
            {
                return SeedAndSave();
            }
            return sessions;
        }
        catch (Exception ex)
        {
            Logger.Error("FocusSessionStore: failed to load sessions", ex);
            return SeedAndSave();
        }
    }

    /// <summary>
    /// Writes the full session list to focus_sessions.json. Never throws.
    /// </summary>
    public static void SaveAll(List<FocusSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var json = JsonSerializer.Serialize(sessions, SerializerOptions);
            File.WriteAllText(SessionsFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("FocusSessionStore: failed to save sessions", ex);
        }
    }

    private static List<FocusSession> SeedAndSave()
    {
        var seed = new List<FocusSession> { new() { Name = "Focus", DurationSeconds = 1500 } };
        SaveAll(seed);
        return seed;
    }
}
