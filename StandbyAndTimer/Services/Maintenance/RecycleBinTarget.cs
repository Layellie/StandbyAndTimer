using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>The Recycle Bin of every drive, through the shell (no dialog, no sound).</summary>
internal sealed class RecycleBinTarget : ICleanupTarget
{
    private readonly Func<NativeMethods.SHQUERYRBINFO> _query;
    private readonly Func<int> _empty;

    // DI uses this one (it only sees public constructors).
    public RecycleBinTarget() : this(QueryShell, EmptyShell) { }

    // Tests replace the shell calls, so the clean logic runs without touching the real bin.
    internal RecycleBinTarget(Func<NativeMethods.SHQUERYRBINFO> query, Func<int> empty)
    {
        _query = query;
        _empty = empty;
    }

    public string Id => CleanupTargetIds.RecycleBin;
    public bool BlockedWhileGameRuns => false;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) => StaTask.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var info = _query();
        return new CleanupScan(true, info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue));
    });

    public Task<CleanupResult> CleanAsync(CancellationToken ct) => StaTask.Run(() =>
    {
        var before = _query();
        if (before.i64NumItems == 0) return CleanupResult.Nothing;
        int hr = _empty();
        Marshal.ThrowExceptionForHR(hr);
        var after = _query();
        return new CleanupResult(Math.Max(0, before.i64Size - after.i64Size),
                                 (int)Math.Max(0, before.i64NumItems - after.i64NumItems),
                                 (int)Math.Min(after.i64NumItems, int.MaxValue));
    });

    private static int EmptyShell() => NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
        NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);

    private static NativeMethods.SHQUERYRBINFO QueryShell()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
        Marshal.ThrowExceptionForHR(hr);
        return info;
    }
}
