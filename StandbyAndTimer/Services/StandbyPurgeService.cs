using System.Diagnostics;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class StandbyPurgeService : IStandbyPurgeService
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList      = 4;

    public event EventHandler? PurgeSucceeded;

    public Task<bool> PurgeAsync() => Task.Run(() =>
    {
        bool success = Purge();
        if (success)
            PurgeSucceeded?.Invoke(this, EventArgs.Empty);
        return success;
    });

    private static bool Purge()
    {
        try
        {
            ElevatePrivilege();

            IntPtr pCommand = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(pCommand, MemoryPurgeStandbyList);
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
