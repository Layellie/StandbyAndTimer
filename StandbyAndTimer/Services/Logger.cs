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

    // Reads the tail of the active log file. Used by the in-app Logs viewer
    // so users don't have to open Explorer + Notepad to triage a hiccup. We
    // open with FileShare.ReadWrite so a concurrent Write from the gate above
    // (different process? no — same process, different thread) can't AccessDenied
    // the reader. Returns oldest→newest as a single string.
    public static string ReadTail(int maxLines)
    {
        try
        {
            if (!File.Exists(_logFile)) return string.Empty;
            using var fs = new FileStream(_logFile, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);

            // Ring buffer of the last `maxLines` lines — keeps memory bounded
            // even if the log is close to MaxBytes (~1 MB).
            var ring = new string[maxLines];
            int idx = 0, count = 0;
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                ring[idx] = line;
                idx = (idx + 1) % maxLines;
                if (count < maxLines) count++;
            }

            var sb = new StringBuilder(count * 80);
            int start = count < maxLines ? 0 : idx;
            for (int i = 0; i < count; i++)
                sb.AppendLine(ring[(start + i) % maxLines]);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(failed to read log: {ex.Message})";
        }
    }
}
