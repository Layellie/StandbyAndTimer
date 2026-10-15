using System.Runtime.InteropServices;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>Power plan (powrprof) and page files (NtQuerySystemInformation 18 + the PagingFiles setting).</summary>
internal sealed class SystemInfoService : ISystemInfoService
{
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const uint ErrorMoreData = 234;

    public PowerPlanInfo? GetPowerPlan()
    {
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr guidPtr) != 0 || guidPtr == IntPtr.Zero)
                return null;
            try
            {
                Guid id = Marshal.PtrToStructure<Guid>(guidPtr);
                uint size = 0;
                uint rc = NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                if ((rc != 0 && rc != ErrorMoreData) || size == 0) return new PowerPlanInfo(id, null);
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                        return new PowerPlanInfo(id, null);
                    return new PowerPlanInfo(id, Marshal.PtrToStringUni(buffer));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { NativeMethods.LocalFree(guidPtr); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Warn($"Power plan unreadable: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<PageFileInfo> GetPageFiles()
    {
        int size = 4096;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemPageFileInformation, buffer, size, out int needed);
                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH) { size = Math.Max(size * 2, needed); continue; }
                if (status != 0 || needed == 0) return [];
                string[] setting = ReadPagingFilesSetting();
                long pageSize = Environment.SystemPageSize;
                return ParsePageFiles(buffer, needed)
                    .Select(p => new PageFileInfo(p.Drive, p.TotalPages * pageSize / (1024 * 1024),
                                                  p.InUsePages * pageSize / (1024 * 1024), IsSystemManaged(setting, p.Drive)))
                    .ToList();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    internal readonly record struct RawPageFile(string Drive, long TotalPages, long InUsePages);

    // SYSTEM_PAGEFILE_INFORMATION (x64): ULONG NextEntryOffset @0, TotalSize @4,
    // TotalInUse @8, PeakUsage @12, UNICODE_STRING PageFileName @16
    // (USHORT Length @16, MaximumLength @18, PWSTR Buffer @24).
    internal static List<RawPageFile> ParsePageFiles(IntPtr buffer, int length)
    {
        var list = new List<RawPageFile>();
        int offset = 0;
        while (offset + 32 <= length)
        {
            uint next      = (uint)Marshal.ReadInt32(buffer, offset);
            long total     = (uint)Marshal.ReadInt32(buffer, offset + 4);
            long inUse     = (uint)Marshal.ReadInt32(buffer, offset + 8);
            int nameBytes  = (ushort)Marshal.ReadInt16(buffer, offset + 16);
            IntPtr namePtr = Marshal.ReadIntPtr(buffer, offset + 24);
            string name    = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr, nameBytes / 2);
            list.Add(new RawPageFile(DriveOf(name), total, inUse));
            if (next == 0) break;
            offset += (int)next;
        }
        return list;
    }

    internal static string DriveOf(string ntName)
    {
        string n = ntName.StartsWith(@"\??\", StringComparison.Ordinal) ? ntName[4..] : ntName;
        return n.Length >= 2 && n[1] == ':' ? n[..2].ToUpperInvariant() : n;
    }

    // PagingFiles entries: "?:\pagefile.sys" (automatic for all drives),
    // "C:\pagefile.sys 0 0" or "C:\pagefile.sys" (system managed size),
    // "C:\pagefile.sys 4096 8192" (custom size, MB).
    internal static bool IsSystemManaged(IEnumerable<string> pagingFiles, string drive)
    {
        foreach (string raw in pagingFiles)
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            if (parts[0].StartsWith(@"?:\", StringComparison.Ordinal)) return true;
            if (!parts[0].StartsWith(drive, StringComparison.OrdinalIgnoreCase)) continue;
            return parts.Length < 3 || (parts[1] == "0" && parts[2] == "0");
        }
        return false;
    }

    private static string[] ReadPagingFilesSetting()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKey);
            return key?.GetValue("PagingFiles") as string[] ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return [];
        }
    }
}
