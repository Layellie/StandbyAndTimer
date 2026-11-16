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
    private const int Zero = 0, Free = 1, Modified = 2, ModifiedNoWrite = 3, StandbyStart = 5, Priorities = 8;

    private static long Mb(long pages) => pages * Environment.SystemPageSize / (1024 * 1024);

    // Into the caller's stack buffer: nothing is allocated.
    private static bool Query(Span<long> pages) =>
        IntPtr.Size == 8
        && NativeMethods.NtQuerySystemInformation(NativeMethods.SystemMemoryListInformation,
               ref MemoryMarshal.GetReference(pages), Longs * sizeof(long), out _) == 0;

    public static bool TryRead([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MemoryListInfo? info)
    {
        info = null;
        Span<long> pages = stackalloc long[Longs];
        if (!Query(pages)) return false;

        var standby = new long[Priorities];
        for (int i = 0; i < Priorities; i++) standby[i] = Mb(pages[StandbyStart + i]);
        info = new MemoryListInfo(Mb(pages[Zero]), Mb(pages[Free]),
                                  Mb(pages[Modified] + pages[ModifiedNoWrite]), standby);
        return true;
    }

    /// <summary>
    /// The three numbers the monitor needs every second (the standby list,
    /// its priority-0 part, the modified list; the same values as
    /// <see cref="TryRead"/>), without allocating anything.
    /// </summary>
    public static bool TryReadTotals(out long standbyMb, out long standbyLowMb, out long modifiedMb)
    {
        standbyMb = standbyLowMb = modifiedMb = 0;
        Span<long> pages = stackalloc long[Longs];
        if (!Query(pages)) return false;

        for (int i = 0; i < Priorities; i++) standbyMb += Mb(pages[StandbyStart + i]);
        standbyLowMb = Mb(pages[StandbyStart]);
        modifiedMb   = Mb(pages[Modified] + pages[ModifiedNoWrite]);
        return true;
    }
}
