using System.Runtime.InteropServices;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

/// <summary>The physical memory lists at one moment, in MB.</summary>
/// <param name="StandbyByPriorityMb">Standby pages per priority 0..7 (0 = what a light purge frees).</param>
internal sealed record MemoryListInfo(long ZeroMb, long FreeMb, long ModifiedMb, long[] StandbyByPriorityMb)
{
    public long StandbyMb      => StandbyByPriorityMb.Sum();
    public long StandbyLowMb   => StandbyByPriorityMb[0];
    public long FreeAndZeroMb  => ZeroMb + FreeMb;
}

/// <summary>
/// NtQuerySystemInformation(SystemMemoryListInformation): the same numbers
/// RAMMap shows, in one ~0.2 µs call, no admin needed. The standby total
/// matches the "Memory\Standby Cache *" counters to the MB.
/// </summary>
internal static class MemoryLists
{
    // SYSTEM_MEMORY_LIST_INFORMATION (x64): ZeroPageCount, FreePageCount,
    // ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount,
    // PageCountByPriority[8] (standby), RepurposedPagesByPriority[8],
    // ModifiedPageCountPageFile: 22 ULONG_PTRs.
    internal const int Longs = 22;
    private const int Zero = 0, Free = 1, Modified = 2, ModifiedNoWrite = 3, StandbyStart = 5;

    public static bool TryRead([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MemoryListInfo? info)
    {
        info = null;
        if (IntPtr.Size != 8) return false;
        var buffer = new long[Longs];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (NativeMethods.NtQuerySystemInformation(NativeMethods.SystemMemoryListInformation,
                    handle.AddrOfPinnedObject(), Longs * sizeof(long), out _) != 0)
                return false;
        }
        finally { handle.Free(); }

        long Mb(long pages) => pages * Environment.SystemPageSize / (1024 * 1024);
        var standby = new long[8];
        for (int i = 0; i < 8; i++) standby[i] = Mb(buffer[StandbyStart + i]);
        info = new MemoryListInfo(Mb(buffer[Zero]), Mb(buffer[Free]),
                                  Mb(buffer[Modified] + buffer[ModifiedNoWrite]), standby);
        return true;
    }
}
