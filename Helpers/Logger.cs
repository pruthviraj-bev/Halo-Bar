using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DynamicIsland.Helpers;

/// <summary>
/// Static thread-safe file logger.
/// Writes logs to %USERPROFILE%\AppData\Local\DynamicIsland\logs\app.log.
///
/// A single long-lived writer drains a bounded channel on a background thread,
/// so hot paths (hover transitions, animation frames) never open/close the file
/// per line. Lines are flushed whenever the queue runs dry, keeping log
/// visibility near-realtime while batching bursts.
/// </summary>
public static class Logger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland",
        "logs"
    );

    private static readonly string LogFilePath = Path.Combine(LogDirectory, "app.log");

    private static readonly Channel<string> Queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    static Logger()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
            _ = DrainAsync();
        }
        catch
        {
            // Fail silently on startup directory creation
        }
    }

    /// <summary>
    /// Logs an info message.
    /// </summary>
    public static void Info(string message) => Log("INFO", message);

    /// <summary>
    /// Logs an error message and optional exception.
    /// </summary>
    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex != null)
        {
            sb.AppendLine().Append(ex.ToString());
        }
        Log("ERROR", sb.ToString());
    }

    private static void Log(string level, string message)
    {
        // Best-effort enqueue: a full queue drops the oldest line rather than
        // blocking or crashing the app (logging must never be a hot path).
        if (Queue.Writer.TryWrite(
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}"))
        {
            // On a bursting writer the queue stays non-empty and the drainer
            // keeps going; when it dries out the drainer flushes to disk so
            // lines appear as soon as the burst ends.
        }
    }

    /// <summary>
    /// Single background consumer: writes a batch, then flushes whenever the
    /// queue runs dry so logs stay near-realtime. One long-lived StreamWriter
    /// (open once) instead of a per-line open/append/close.
    /// </summary>
    private static async Task DrainAsync()
    {
        try
        {
            using var writer = new StreamWriter(LogFilePath, append: true, Encoding.UTF8);
            writer.AutoFlush = false;

            while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                int batch = 0;
                while (Queue.Reader.TryRead(out var line))
                {
                    writer.Write(line);
                    if (++batch >= 256) // never starve the UI thread with one giant flush
                    {
                        writer.Flush();
                        batch = 0;
                    }
                }
                writer.Flush();
            }
        }
        catch
        {
            // The logger itself must never crash the app.
        }
    }
}