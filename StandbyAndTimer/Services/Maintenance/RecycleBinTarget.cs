using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>The Recycle Bin of every drive, through the shell (no dialog, no sound).</summary>
internal sealed class RecycleBinTarget : ICleanupTarget
{
    public string Id => CleanupTargetIds.RecycleBin;
    public bool BlockedWhileGameRuns => false;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) => StaTask.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var info = Query();
        return new CleanupScan(true, info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue));
    });

    public Task<CleanupResult> CleanAsync(CancellationToken ct) => StaTask.Run(() =>
    {
        var before = Query();
        if (before.i64NumItems == 0) return CleanupResult.Nothing;
        int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
        if (hr != 0) throw new COMException("SHEmptyRecycleBin failed", hr);
        var after = Query();
        return new CleanupResult(Math.Max(0, before.i64Size - after.i64Size),
                                 (int)Math.Max(0, before.i64NumItems - after.i64NumItems),
                                 (int)Math.Min(after.i64NumItems, int.MaxValue));
    });

    private static NativeMethods.SHQUERYRBINFO Query()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
        if (hr != 0) throw new COMException("SHQueryRecycleBin failed", hr);
        return info;
    }
}
