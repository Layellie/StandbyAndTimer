namespace StandbyAndTimer.Core.Interfaces;

/// <summary>
/// Reads the tail of the application log file for the in-app Logs viewer.
/// Implementations are responsible for both the file IO (async, share-friendly)
/// and any presentation-level reformatting (timestamp compaction, etc.) so
/// callers receive a display-ready string and don't need to know the on-disk
/// log format.
/// </summary>
public interface ILogTailReader
{
    /// <summary>
    /// Returns up to <paramref name="maxLines"/> most recent log lines as a
    /// single string, oldest→newest, with trailing newline per line. Returns
    /// <see cref="string.Empty"/> if the log file does not exist yet.
    /// On read failure, returns a single user-facing error sentinel rather
    /// than throwing — the Logs viewer never aborts the panel.
    /// </summary>
    Task<string> ReadTailAsync(int maxLines, CancellationToken ct = default);
}
