using System.IO;
using Microsoft.Win32.SafeHandles;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

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
///    folder root; a root that is a link (file or folder) is neither
///    followed nor deleted;
/// 7. a root inside the profile, ProgramData or the Windows folder
///    (<see cref="TrustedBases"/>) is not touched when a folder between it
///    and that base is a link (%LOCALAPPDATA%\NVIDIA made a junction: its
///    DXCache then lies somewhere else); roots elsewhere (Steam libraries,
///    C:\NVIDIA) are only checked themselves, as before;
/// 8. every folder is held open while it is walked, so it cannot be renamed
///    or swapped for a link between the look at it and the listing of what
///    is inside (this app is elevated; the folders are writable by any
///    program of the user).
/// Scan walks with the same rules, so it shows what a clean would remove.
/// This is the only code in the app that deletes files.
/// </summary>
internal static class SafeFolderCleaner
{
    /// <summary>
    /// Folders a non-elevated program cannot replace: the links above them
    /// (a profile moved to another drive) were made by an administrator.
    /// </summary>
    private static readonly string[] TrustedBases =
    [
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
    ];

    public static CleanupScan Scan(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)
    {
        bool found = false;
        long bytes = 0;
        int files = 0;
        foreach (string root in roots)
            found |= VisitRoot(root, TrustedBases, keepNewerThan, nowUtc, delete: false,
                               onFile: f => { bytes += f.Length; files++; return true; }, onEnter: null, ct);
        return found ? new CleanupScan(true, bytes, files) : CleanupScan.NotFound;
    }

    /// <summary>
    /// The size of the files in one folder, for a listing (the Downloads
    /// dialog). The same walk as <see cref="Scan"/> (never into a link, the
    /// folder itself must not be one), but the folder may lie below a link:
    /// a Downloads folder moved to another drive with a junction. Nothing is
    /// deleted through this.
    /// </summary>
    public static long SizeOf(string folder, CancellationToken ct)
    {
        long bytes = 0;
        VisitRoot(folder, trustedBases: [], keepNewerThan: null, DateTime.UtcNow, delete: false,
                  onFile: f => { bytes += f.Length; return true; }, onEnter: null, ct);
        return bytes;
    }

    public static CleanupResult Clean(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct) =>
        Clean(roots, keepNewerThan, nowUtc, onEnter: null, ct);

    /// <param name="onEnter">Tests: called with each folder once it is held open, before its contents are listed.</param>
    internal static CleanupResult Clean(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc,
                                        Action<string>? onEnter, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (string root in roots)
        {
            VisitRoot(root, TrustedBases, keepNewerThan, nowUtc, delete: true, onFile: f =>
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
            }, onEnter, ct);
        }
        return new CleanupResult(freed, deleted, skipped);
    }

    // A folder root is walked; a file root goes through the same file rules.
    // Returns whether the root exists.
    private static bool VisitRoot(string root, IReadOnlyList<string> trustedBases, TimeSpan? keepNewerThan, DateTime nowUtc,
                                  bool delete, Func<FileInfo, bool> onFile, Action<string>? onEnter, CancellationToken ct)
    {
        if (Directory.Exists(root))
        {
            var dir = new DirectoryInfo(root);
            // Never through a link: any user can create C:\NVIDIA as a junction.
            // Held open from here on, which also keeps every folder above it
            // from being renamed, so the look at those stays true.
            using var pin = Pin(dir.FullName);
            if (pin is null || HasLinkAbove(dir.FullName, trustedBases)) return true;
            WalkInside(dir, keepNewerThan, nowUtc, delete, onFile, onEnter, ct);
            return true;
        }
        var file = new FileInfo(root);
        if (!file.Exists) return false;
        ct.ThrowIfCancellationRequested();
        if (IsLinkOrUnreadable(file) || HasLinkAbove(file.FullName, trustedBases)) return true;   // never through a link
        if (keepNewerThan is { } keep && nowUtc - SafeWriteTime(file) < keep) return true;
        onFile(file);
        return true;
    }

    // A sub-folder: held open while it is walked, then removed if it is empty.
    private static void Walk(DirectoryInfo dir, TimeSpan? keepNewerThan, DateTime nowUtc,
                             bool delete, Func<FileInfo, bool> onFile, Action<string>? onEnter, CancellationToken ct)
    {
        using (var pin = Pin(dir.FullName))
        {
            if (pin is null) return;   // a link by now, gone, or being deleted by its owner
            WalkInside(dir, keepNewerThan, nowUtc, delete, onFile, onEnter, ct);
        }

        if (delete)
        {
            try { dir.Delete(recursive: false); }   // only succeeds when empty
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Post-order walk of a folder the caller holds open. onFile returns
    // whether the file is gone (clean) / counted (scan).
    private static void WalkInside(DirectoryInfo dir, TimeSpan? keepNewerThan, DateTime nowUtc,
                                   bool delete, Func<FileInfo, bool> onFile, Action<string>? onEnter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        onEnter?.Invoke(dir.FullName);

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
                Walk(sub, keepNewerThan, nowUtc, delete, onFile, onEnter, ct);
                continue;
            }

            if (entry is FileInfo file)
            {
                if (keepNewerThan is { } keep && nowUtc - SafeWriteTime(file) < keep)
                    continue;
                onFile(file);
            }
        }
    }

    /// <summary>
    /// Opens a real folder (never a link) and holds it: without delete
    /// sharing, nobody can rename, delete or replace it, or rename a folder
    /// above it, until the handle is closed. Null for a link, or when it
    /// cannot be opened (gone, denied, or its owner is deleting it right now).
    /// </summary>
    internal static SafeFileHandle? Pin(string directory)
    {
        var handle = NativeMethods.CreateFile(
            ExtendedPath(directory),
            // Listing access, not attributes alone: the sharing rules do not apply to attribute-only handles.
            NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        try
        {
            var attributes = File.GetAttributes(handle);   // of what was opened, not of the path
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
                return handle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        handle.Dispose();
        return null;
    }

    // CreateFile takes paths over 260 characters only in this form (temp
    // folders do get that deep); .NET's own file APIs add it themselves.
    private static string ExtendedPath(string fullPath) =>
        fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
      : fullPath.StartsWith(@"\\", StringComparison.Ordinal)   ? @"\\?\UNC\" + fullPath[2..]
      :                                                          @"\\?\" + fullPath;

    /// <summary>
    /// Whether a folder between <paramref name="path"/> and the trusted base
    /// it lies under is a link (or cannot be read); the base itself is not
    /// checked. A path under none of <paramref name="trustedBases"/> has
    /// nothing to check: a Steam library reached through a junction (a
    /// library moved to another drive) is cleaned as it always was. The
    /// folders that matter here, the ones any program of the user can swap,
    /// are those inside the profile and ProgramData.
    /// </summary>
    internal static bool HasLinkAbove(string path, IReadOnlyList<string> trustedBases)
    {
        var above = new List<DirectoryInfo>();
        for (var dir = Directory.GetParent(path); dir is not null; dir = dir.Parent)
        {
            if (trustedBases.Contains(dir.FullName, StringComparer.OrdinalIgnoreCase))
                return above.Any(IsLinkOrUnreadable);
            above.Add(dir);
        }
        return false;
    }

    private static bool IsLinkOrUnreadable(FileSystemInfo root)
    {
        try { return root.Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static DateTime SafeWriteTime(FileInfo f)
    {
        try { return f.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MaxValue; }
    }
}
