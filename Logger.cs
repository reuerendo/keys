using System;
using System.IO;
using System.Threading;

namespace VirtualKeyboard;

/// <summary>
/// Simple thread-safe file logger for debugging keyboard input issues
/// </summary>
public static class Logger
{
    private static readonly string LogFilePath;
    private static readonly object LockObject = new object();

    static Logger()
    {
        // Create log file in the same directory as the executable
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        LogFilePath = Path.Combine(appDir, "VirtualKeyboard.log");
        
        // Clear previous log on startup
        try
        {
            File.WriteAllText(LogFilePath, $"=== Virtual Keyboard Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch
        {
            // Ignore errors during initialization
        }
    }

    /// <summary>
    /// Log an informational message
    /// </summary>
    public static void Info(string message)
    {
        Log("INFO", message);
    }

    /// <summary>
    /// Log a warning message
    /// </summary>
    public static void Warning(string message)
    {
        Log("WARN", message);
    }

    /// <summary>
    /// Log an error message
    /// </summary>
    public static void Error(string message, Exception ex = null)
    {
        string fullMessage = ex != null ? $"{message}: {ex.Message}" : message;
        Log("ERROR", fullMessage);
    }

    /// <summary>
    /// Log a debug message with detailed information
    /// </summary>
    public static void Debug(string message)
    {
        Log("DEBUG", message);
    }

    /// <summary>
    /// Core logging method
    /// </summary>
    private static void Log(string level, string message)
    {
        lock (LockObject)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, logEntry);
            }
            catch
            {
                // Silently fail if we can't write to log
            }
        }
    }

    /// <summary>
    /// Get the current log file path
    /// </summary>
    public static string GetLogFilePath() => LogFilePath;
}