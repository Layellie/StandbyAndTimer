using System.IO;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// Deletes the contents of known folders, safely:
/// 1. never enters a reparse point (junction / symlink / mount point); a
///    link inside a root is removed as a link, its target is never touched;
/// 2. files in use or denied are skipped and counted, never thrown;
/// 3. optionally keeps files written within <c>keepNewerThan</c> (temp folders:
///    a running installer may still need them);
/// 4. never deletes a root itself; empty sub-folders go, deepest first;
/// 5. FreedBytes is the size of the files actually deleted.
/// Scan walks with the same rules, so it shows what a clean would remove.
/// This is the only code in the app that deletes files.
/// </summary>
internal static class SafeFolderCleaner
{
    public static CleanupScan Scan(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)
    {
        bool found = false;
        long bytes = 0;
        int files = 0;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            found = true;
            Walk(new DirectoryInfo(root), isRoot: true, keepNewerThan, nowUtc, delete: false, ct,
                 onFile: f => { bytes += f.Length; files++; return true; });
        }
        return found ? new CleanupScan(true, bytes, files) : CleanupScan.NotFound;
    }

    public static CleanupResult Clean(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            Walk(new DirectoryInfo(root), isRoot: true, keepNewerThan, nowUtc, delete: true, ct, onFile: f =>
            {
                long length = f.Length;
                try
                {
                    if (f.Attributes.HasFlag(FileAttributes.ReadOnly))
                        f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                    freed += length;
                    deleted++;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    return false;
                }
            });
        }
        return new CleanupResult(freed, deleted, skipped);
    }

    // Post-order walk. onFile returns whether the file is gone (clean) / counted (scan).
    private static void Walk(DirectoryInfo dir, bool isRoot, TimeSpan? keepNewerThan, DateTime nowUtc,
                             bool delete, CancellationToken ct, Func<FileInfo, bool> onFile)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var entry in entries)
        {
            bool isLink;
            try { isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (isLink)
            {
                // Never follow it. Clean removes only the link itself:
                // Directory.Delete(non-recursive) / File.Delete on a link
                // deletes the link, not what it points to.
                if (delete)
                {
                    try
                    {
                        if (entry is DirectoryInfo) Directory.Delete(entry.FullName, recursive: false);
                        else                        File.Delete(entry.FullName);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                Walk(sub, isRoot: false, keepNewerThan, nowUtc, delete, ct, onFile);
                continue;
            }

            if (entry is FileInfo file)
            {
                if (keepNewerThan is { } keep && nowUtc - SafeWriteTime(file) < keep)
                    continue;
                onFile(file);
            }
        }

        if (delete && !isRoot)
        {
            try { dir.Delete(recursive: false); }   // only succeeds when empty
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static DateTime SafeWriteTime(FileInfo f)
    {
        try { return f.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MaxValue; }
    }
}
