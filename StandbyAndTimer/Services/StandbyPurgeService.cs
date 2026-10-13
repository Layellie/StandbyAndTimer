using System.Diagnostics;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class StandbyPurgeService : IStandbyPurgeService
{
    private const int SystemMemoryListInformation       = 80;
    private const int MemoryPurgeStandbyList            = 4;
    // SYSTEM_MEMORY_LIST_COMMAND value 5: frees only the priority-0 part of
    // the standby list (pages Windows itself rates least likely to be reused).
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    public event EventHandler? PurgeSucceeded;

    public bool LightMode { get; set; }

    internal static int CommandFor(bool lightMode) =>
        lightMode ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList;

    public Task<bool> PurgeAsync()
    {
        int command = CommandFor(LightMode);   // read once, on the caller's thread
        return Task.Run(() =>
        {
            bool success = Purge(command);
            if (success)
                PurgeSucceeded?.Invoke(this, EventArgs.Empty);
            return success;
        });
    }

    private static bool Purge(int command)
    {
        try
        {
            ElevatePrivilege();

            IntPtr pCommand = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(pCommand, command);
                int result = NativeMethods.NtSetSystemInformation(
                    SystemMemoryListInformation,
                    pCommand,
                    sizeof(int));
                return result == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pCommand);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void ElevatePrivilege()
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
            NativeMethods.LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", ref luid);

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
