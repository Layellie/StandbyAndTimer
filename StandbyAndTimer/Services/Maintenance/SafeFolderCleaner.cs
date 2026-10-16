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
/// 4. never deletes a root folder itself; empty sub-folders go, deepest first;
/// 5. FreedBytes is the size of the files actually deleted;
/// 6. a root that is a file (MEMORY.DMP) is handled like a file inside a
///    folder root; a root that is a link is neither followed nor deleted.
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
            found |= VisitRoot(root, keepNewerThan, nowUtc, delete: false,
                               onFile: f => { bytes += f.Length; files++; return true; }, ct);
        return found ? new CleanupScan(true, bytes, files) : CleanupScan.NotFound;
    }

    public static CleanupResult Clean(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (string root in roots)
        {
            VisitRoot(root, keepNewerThan, nowUtc, delete: true, onFile: f =>
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
            }, ct);
        }
        return new CleanupResult(freed, deleted, skipped);
    }

    // A folder root is walked; a file root goes through the same file rules.
    // Returns whether the root exists.
    private static bool VisitRoot(string root, TimeSpan? keepNewerThan, DateTime nowUtc,
                                  bool delete, Func<FileInfo, bool> onFile, CancellationToken ct)
    {
        if (Directory.Exists(root))
        {
            Walk(new DirectoryInfo(root), isRoot: true, keepNewerThan, nowUtc, delete, onFile, ct);
            return true;
        }
        var file = new FileInfo(root);
        if (!file.Exists) return false;
        ct.ThrowIfCancellationRequested();
        try
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;   // never through a link
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        if (keepNewerThan is { } keep && nowUtc - SafeWriteTime(file) < keep) return true;
        onFile(file);
        return true;
    }

    // Post-order walk. onFile returns whether the file is gone (clean) / counted (scan).
    private static void Walk(DirectoryInfo dir, bool isRoot, TimeSpan? keepNewerThan, DateTime nowUtc,
                             bool delete, Func<FileInfo, bool> onFile, CancellationToken ct)
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
                Walk(sub, isRoot: false, keepNewerThan, nowUtc, delete, onFile, ct);
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
