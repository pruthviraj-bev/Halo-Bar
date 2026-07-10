using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DynamicIsland.Helpers;

/// <summary>
/// Simple static thread-safe file logger.
/// Writes logs to %USERPROFILE%\AppData\Local\DynamicIsland\logs\app.log.
/// </summary>
public static class Logger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland",
        "logs"
    );

    private static readonly string LogFilePath = Path.Combine(LogDirectory, "app.log");
    private static readonly object LockObj = new();

    static Logger()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
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
        var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
        Task.Run(() =>
        {
            lock (LockObj)
            {
                try
                {
                    File.AppendAllText(LogFilePath, logLine);
                }
                catch
                {
                    // Fail silently to prevent logging failures from crashing the app
                }
            }
        });
    }
}
