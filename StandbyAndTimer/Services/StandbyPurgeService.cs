using System.Diagnostics;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class StandbyPurgeService : IStandbyPurgeService
{
    private const int SystemMemoryListInformation       = 80;
    // SYSTEM_MEMORY_LIST_COMMAND values.
    // 3: writes the modified page list out, so its pages become standby.
    private const int MemoryFlushModifiedList           = 3;
    private const int MemoryPurgeStandbyList            = 4;
    // 5: frees only the priority-0 part of the standby list (pages Windows
    // itself rates least likely to be reused).
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    /// <summary>One step of a purge, run in order.</summary>
    internal enum Step
    {
        FlushModifiedList,      // modified pages -> written -> standby
        TrimSystemFileCache,    // the system cache's working set -> standby
        PurgeStandby,           // the whole standby list -> free
        PurgeLowPriorityStandby // priority-0 standby -> free
    }

    public event EventHandler? PurgeSucceeded;

    public bool LightMode { get; set; }
    public bool DeepMode  { get; set; }

    internal static int CommandFor(bool lightMode) =>
        lightMode ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList;

    /// <summary>
    /// Deep: first move the modified list and the system cache's working set
    /// onto the standby list, then purge all of it, so one purge frees what
    /// a plain one leaves behind. Deep wins over light (the view model keeps
    /// them exclusive anyway).
    /// </summary>
    internal static Step[] PlanFor(bool lightMode, bool deepMode) =>
        deepMode  ? [Step.FlushModifiedList, Step.TrimSystemFileCache, Step.PurgeStandby]
      : lightMode ? [Step.PurgeLowPriorityStandby]
      :             [Step.PurgeStandby];

    public Task<bool> PurgeAsync()
    {
        var plan = PlanFor(LightMode, DeepMode);   // read once, on the caller's thread
        return Task.Run(() =>
        {
            bool success = Purge(plan);
            if (success)
                PurgeSucceeded?.Invoke(this, EventArgs.Empty);
            return success;
        });
    }

    // Success = the standby step succeeded; the deep-only steps before it are
    // best effort (logged) and never stop the purge itself.
    private static bool Purge(Step[] plan)
    {
        try
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");   // memory list commands
            if (plan.Contains(Step.TrimSystemFileCache))
                EnablePrivilege("SeIncreaseQuotaPrivilege");      // SetSystemFileCacheSize

            bool success = false;
            foreach (var step in plan)
            {
                switch (step)
                {
                    case Step.FlushModifiedList:
                        int flush = MemoryListCommand(MemoryFlushModifiedList);
                        if (flush != 0) Logger.Warn(FormattableString.Invariant($"Deep purge: flushing the modified list failed (0x{flush:X8})"));
                        break;
                    case Step.TrimSystemFileCache:
                        // (SIZE_T)-1 for both limits = empty the system cache's working set.
                        if (!NativeMethods.SetSystemFileCacheSize(-1, -1, 0))
                            Logger.Warn($"Deep purge: trimming the system file cache failed ({Marshal.GetLastWin32Error()})");
                        break;
                    case Step.PurgeStandby:
                        success = MemoryListCommand(MemoryPurgeStandbyList) == 0;
                        break;
                    case Step.PurgeLowPriorityStandby:
                        success = MemoryListCommand(MemoryPurgeLowPriorityStandbyList) == 0;
                        break;
                }
            }
            return success;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Purge failed: {ex.Message}");
            return false;
        }
    }

    private static int MemoryListCommand(int command)
    {
        IntPtr pCommand = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(pCommand, command);
            return NativeMethods.NtSetSystemInformation(SystemMemoryListInformation, pCommand, sizeof(int));
        }
        finally
        {
            Marshal.FreeHGlobal(pCommand);
        }
    }

    private static void EnablePrivilege(string name)
    {
        IntPtr hToken = IntPtr.Zero;
        if (!NativeMethods.OpenProcessToken(
                Process.GetCurrentProcess().Handle,
                0x0028,          // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
                ref hToken))
            return;

        try
        {
            long luid = 0;
            NativeMethods.LookupPrivilegeValue(null, name, ref luid);

            var tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid           = luid,
                Attributes     = 2  // SE_PRIVILEGE_ENABLED
            };
            NativeMethods.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.CloseHandle(hToken);
        }
    }
}
