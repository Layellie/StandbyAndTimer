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

    private static string[] ReadPagingFilesSetting() =>
        ReadValue(Registry.LocalMachine, MemoryManagementKey, "PagingFiles") as string[] ?? [];

    // ── Power mode (spec §9.3) ──────────────────────────────────────────────

    private static readonly Guid BalancedPlan        = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid OverlayBestPerf     = new("ded574b5-45a0-4f42-8737-46345c09c238");
    private static readonly Guid OverlayBestPowerEff = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    public PowerMode GetPowerMode()
    {
        Guid? overlay = null;
        try
        {
            if (NativeMethods.PowerGetEffectiveOverlayScheme(out Guid g) == 0) overlay = g;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Warn($"Power mode unreadable: {ex.Message}");
        }
        bool isWindows10 = Environment.OSVersion.Version.Build < 22000;
        bool hasBattery = NativeMethods.GetSystemPowerStatus(out var status) && (status.BatteryFlag & 128) == 0;   // 128 = no battery
        return PowerModeFrom(overlay, GetPowerPlan()?.Id, isWindows10, hasBattery);
    }

    /// <summary>
    /// Only the Balanced plan has power modes; Guid.Empty is Balanced's own
    /// mode. Windows 10 shows its slider only on battery-powered PCs, with
    /// other names for the same three steps.
    /// </summary>
    internal static PowerMode PowerModeFrom(Guid? overlay, Guid? activePlan, bool isWindows10 = false, bool hasBattery = true)
    {
        if (isWindows10 && !hasBattery) return PowerMode.NotAvailable;
        if (activePlan is { } plan && plan != BalancedPlan) return PowerMode.NotOnThisPlan;
        if (overlay is not { } o || activePlan is null) return PowerMode.Unknown;
        if (o == OverlayBestPerf)     return PowerMode.BestPerformance;
        if (o == OverlayBestPowerEff) return isWindows10 ? PowerMode.BetterBattery : PowerMode.BestPowerEfficiency;
        if (o == Guid.Empty)          return isWindows10 ? PowerMode.BetterPerformance : PowerMode.Balanced;
        return PowerMode.Unknown;
    }

    // ── Gaming rows (spec §9.3): the registry values Windows' own pages write ──

    public GamingInfo GetGamingInfo() => new(
        Hags(ReadValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")),
        BackgroundRecording(ReadValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
                            ReadValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled")),
        MemoryIntegrity(ReadValue(Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled")),
        GameMode(ReadValue(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled")));

    /// <summary>HwSchMode: 2 on, 1 off; anything else means Windows decides (null).</summary>
    internal static bool? Hags(object? hwSchMode) => hwSchMode switch
    {
        2 => true,    // a DWORD comes back as a boxed int; constant patterns check the type too
        1 => false,
        _ => null,
    };

    /// <summary>"Record what happened": captures not turned off and historical capture on.</summary>
    internal static bool BackgroundRecording(object? appCapture, object? historical) =>
        appCapture is not 0 && historical is 1;

    internal static bool MemoryIntegrity(object? enabled) => enabled is 1;

    /// <summary>Game Mode is on unless turned off (AutoGameModeEnabled = 0).</summary>
    internal static bool GameMode(object? autoGameMode) => autoGameMode is not 0;

    private static object? ReadValue(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }
}
