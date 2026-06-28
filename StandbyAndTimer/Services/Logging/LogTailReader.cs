using System.IO;
using System.Text;
using StandbyAndTimer.Core.Interfaces;

namespace StandbyAndTimer.Services.Logging;

/// <summary>
/// Default <see cref="ILogTailReader"/> implementation backed by the same
/// log file <see cref="Logger"/> writes to.
///
/// Design notes:
/// <list type="bullet">
///   <item><b>Async file IO.</b> The Settings panel calls us from the UI
///         dispatcher; doing the read async keeps the panel responsive on
///         spinning disks / OneDrive-backed AppData.</item>
///   <item><b>Bounded memory.</b> A fixed-size ring buffer of length
///         <c>maxLines</c> holds at most that many references regardless of
///         how large the on-disk file grows.</item>
///   <item><b>Shared read.</b> <see cref="FileShare.ReadWrite"/> ensures
///         a concurrent <see cref="Logger"/> write (different thread, same
///         process) never trips <see cref="UnauthorizedAccessException"/>.</item>
///   <item><b>Display reformatting.</b> The on-disk line format
///         <c>"yyyy-MM-dd HH:mm:ss.fff [LEVEL] msg"</c> is compacted to
///         <c>"HH:mm:ss [LEVEL] msg"</c> for display, freeing 15 columns
///         that the narrow Settings panel was clipping off the message body.
///         The file on disk keeps the full timestamp for archival use.</item>
/// </list>
/// </summary>
internal sealed class LogTailReader : ILogTailReader
{
    public async Task<string> ReadTailAsync(int maxLines, CancellationToken ct = default)
    {
        if (maxLines <= 0) return string.Empty;

        try
        {
            string path = Logger.LogFilePath;
            if (!File.Exists(path)) return string.Empty;

            // useAsync:true gives genuine overlapped IO on Windows — important
            // when AppData lives on a slow OneDrive-synced volume.
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var sr = new StreamReader(fs, Encoding.UTF8);

            var ring = new string[maxLines];
            int idx = 0, count = 0;
            string? raw;
            while ((raw = await sr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                ring[idx] = CompactTimestamp(raw);
                idx = (idx + 1) % maxLines;
                if (count < maxLines) count++;
            }

            // Average line length after compaction ~70 chars; pre-size the
            // builder to avoid mid-build reallocations on the common path.
            var sb = new StringBuilder(count * 72);
            int start = count < maxLines ? 0 : idx;
            for (int i = 0; i < count; i++)
                sb.AppendLine(ring[(start + i) % maxLines]);
            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"(failed to read log: {ex.Message})";
        }
    }

    // Expected on-disk format: "yyyy-MM-dd HH:mm:ss.fff [LEVEL] message"
    //                           0         1         2
    //                           0123456789012345678901234
    // We strip "yyyy-MM-dd " (chars 0..10 + space) and ".fff" (chars 19..22)
    // so the visible prefix becomes "HH:mm:ss [LEVEL] message". Lines that
    // don't match the expected shape — stack-trace continuation lines, our
    // own "(failed to read log…)" sentinel, anything user-provided — pass
    // through unmodified rather than risk corrupting them with a bad slice.
    private static string CompactTimestamp(string line)
    {
        if (line.Length < 24) return line;
        if (line[4]  != '-' || line[7]  != '-' || line[10] != ' ') return line;
        if (line[13] != ':' || line[16] != ':' || line[19] != '.') return line;
        // chars 11..18 = "HH:mm:ss" ; chars 23.. = " [LEVEL] message"
        return string.Concat(line.AsSpan(11, 8), line.AsSpan(23));
    }
}
