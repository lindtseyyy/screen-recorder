using System.IO;

namespace ScreenRecorder.Infrastructure;

/// <summary>
/// Minimal text logger for diagnosing failures. Writes to
/// %LOCALAPPDATA%\ScreenRecorder\logs\ and keeps the last 5 files.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _path = string.Empty;

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenRecorder", "logs");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _path = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            foreach (var old in new DirectoryInfo(LogDirectory).GetFiles("app-*.log")
                         .OrderByDescending(f => f.Name).Skip(4)) // + this session's = 5
            {
                try { old.Delete(); } catch { /* best effort */ }
            }
            Info("Session started.");
        }
        catch
        {
            _path = string.Empty; // logging unavailable; never crash the app
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        if (string.IsNullOrEmpty(_path))
            return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // never crash the app
            }
        }
    }
}
