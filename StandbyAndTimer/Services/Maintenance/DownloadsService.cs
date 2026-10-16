using System.IO;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// The user's Downloads folder: top-level entries with sizes, and moving
/// chosen ones to the Recycle Bin (never deleting them). Links are not listed:
/// recycling a junction could reach what it points to.
/// </summary>
internal sealed class DownloadsService : IDownloadsService
{
    private readonly Lazy<string?> _folderLookup;
    private readonly Func<string, bool> _recycle;

    // DI uses this one (it only sees public constructors). DI builds it at
    // start-up, so the shell lookup waits for the first use of the tab.
    public DownloadsService() : this(FindDownloads) { }

    // Tests: a temp folder, and a fake recycler (true = moved).
    internal DownloadsService(string? folder, Func<string, bool>? recycle = null) : this(() => folder, recycle) { }

    internal DownloadsService(Func<string?> findFolder, Func<string, bool>? recycle = null)
    {
        _folderLookup = new Lazy<string?>(() => findFolder() is { } f && Directory.Exists(f) ? f : null);
        _recycle      = recycle ?? SendToRecycleBin;
    }

    public string? FolderPath => _folderLookup.Value;

    public async Task<CleanupScan> ScanAsync(CancellationToken ct)
    {
        if (FolderPath is null) return CleanupScan.NotFound;
        var list = await ListAsync(ct).ConfigureAwait(false);
        return new CleanupScan(true, list.Sum(e => e.Bytes), list.Count);
    }

    public Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<DownloadEntry>>(() =>
    {
        if (FolderPath is not { } folder) return [];
        var list = new List<DownloadEntry>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            FileAttributes a;
            try { a = info.Attributes; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (a.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (a.HasFlag(FileAttributes.Hidden) && a.HasFlag(FileAttributes.System)) continue;   // desktop.ini

            long bytes = info is FileInfo f ? f.Length
                       : SafeFolderCleaner.Scan([info.FullName], null, DateTime.UtcNow, ct).Bytes;
            list.Add(new DownloadEntry(info.FullName, info.Name, info is DirectoryInfo, bytes, info.LastWriteTime));
        }
        return list;
    }, ct);

    public async Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries)
    {
        var failed = new List<DownloadEntry>();
        foreach (var e in entries)
        {
            bool ok;
            try { ok = await StaTask.Run(() => _recycle(e.Path)).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn($"Recycle {e.Path}: {ex.Message}"); ok = false; }
            if (!ok) failed.Add(e);
        }
        Logger.Info($"Downloads: {entries.Count - failed.Count} moved to the Recycle Bin, {failed.Count} failed");
        return failed;
    }

    private static bool SendToRecycleBin(string path)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc  = NativeMethods.FO_DELETE,
            pFrom  = path + "\0",   // + the marshaller's null = double-null terminated
            fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION |
                              NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_WANTNUKEWARNING),
        };
        int rc = NativeMethods.SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    private static string? FindDownloads()
    {
        if (NativeMethods.SHGetKnownFolderPath(NativeMethods.FOLDERID_Downloads, 0, IntPtr.Zero, out IntPtr p) == 0)
        {
            try { return Marshal.PtrToStringUni(p); }
            finally { NativeMethods.CoTaskMemFree(p); }
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }
}
