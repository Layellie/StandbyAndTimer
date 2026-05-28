using System.IO;
using System.Text;

namespace StandbyAndTimer.Services;

internal static class Logger
{
    private static readonly object _gate = new();
    private const long MaxBytes = 1_048_576;

    private static readonly string _logDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "StandbyAndTimer");

    private static readonly string _logFile = Path.Combine(_logDir, "log.txt");

    public static string LogFilePath => _logFile;

    public static void Info(string message)    => Write("INFO ", message);
    public static void Warn(string message)    => Write("WARN ", message);
    public static void Error(string message)   => Write("ERROR", message);

    public static void Error(string context, Exception ex) =>
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_logDir);
                RollIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_logFile, line, Encoding.UTF8);
            }
        }
        catch { /* logger must never throw */ }
    }

    private static void RollIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logFile);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                var archive = Path.Combine(_logDir, "log.old.txt");
                if (File.Exists(archive)) File.Delete(archive);
                File.Move(_logFile, archive);
            }
        }
        catch { }
    }
}
