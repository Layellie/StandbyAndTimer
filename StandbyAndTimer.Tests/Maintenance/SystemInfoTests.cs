using System.IO;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public class SystemInfoTests
{
    [Theory]
    [InlineData(@"\??\C:\pagefile.sys", "C:")]
    [InlineData(@"\??\d:\pagefile.sys", "D:")]
    [InlineData(@"C:\pagefile.sys", "C:")]
    public void Drive_of_an_nt_page_file_name(string name, string drive) =>
        Assert.Equal(drive, SystemInfoService.DriveOf(name));

    [Theory]
    [InlineData(new[] { @"?:\pagefile.sys" }, "C:", true)]          // "automatically manage for all drives"
    [InlineData(new[] { @"C:\pagefile.sys 0 0" }, "C:", true)]       // system managed size
    [InlineData(new[] { @"C:\pagefile.sys" }, "C:", true)]
    [InlineData(new[] { @"C:\pagefile.sys 4096 8192" }, "C:", false)] // custom size
    [InlineData(new[] { @"D:\pagefile.sys 0 0" }, "C:", false)]
    public void System_managed_from_the_paging_files_setting(string[] entries, string drive, bool expected) =>
        Assert.Equal(expected, SystemInfoService.IsSystemManaged(entries, drive));

    // Builds one SYSTEM_PAGEFILE_INFORMATION (x64 layout) in unmanaged memory,
    // name stored after the 32-byte header like Windows does.
    [Fact]
    public void Parses_a_page_file_entry()
    {
        string name = @"\??\C:\pagefile.sys";
        int nameBytes = name.Length * 2;
        IntPtr buf = Marshal.AllocHGlobal(32 + nameBytes);
        try
        {
            Marshal.WriteInt32(buf, 0, 0);          // NextEntryOffset: last
            Marshal.WriteInt32(buf, 4, 1048576);    // TotalSize (pages)
            Marshal.WriteInt32(buf, 8, 300000);     // TotalInUse
            Marshal.WriteInt32(buf, 12, 400000);    // PeakUsage
            Marshal.WriteInt16(buf, 16, (short)nameBytes);
            Marshal.WriteInt16(buf, 18, (short)nameBytes);
            Marshal.WriteIntPtr(buf, 24, buf + 32);
            Marshal.Copy(name.ToCharArray(), 0, buf + 32, name.Length);

            var list = SystemInfoService.ParsePageFiles(buf, 32 + nameBytes);

            Assert.Equal([new SystemInfoService.RawPageFile("C:", 1048576, 300000)], list);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [Fact]
    public void Reads_this_machines_power_plan_and_page_files()
    {
        var info = new SystemInfoService();

        var plan = info.GetPowerPlan();
        var files = info.GetPageFiles();

        Assert.NotNull(plan);
        Assert.False(string.IsNullOrWhiteSpace(plan!.Name));
        Assert.All(files, f => Assert.Matches("^[A-Z]:$", f.Drive));
    }

    [Theory]
    [InlineData(SystemTool.OptimizeDrives, "dfrgui.exe", "")]
    [InlineData(SystemTool.DiskCleanup, "cleanmgr.exe", "")]
    [InlineData(SystemTool.PowerOptions, "control.exe", "/name Microsoft.PowerOptions")]
    [InlineData(SystemTool.VirtualMemory, "SystemPropertiesPerformance.exe", "")]
    [InlineData(SystemTool.RepairSystemFiles, "cmd.exe", "/k \"DISM /Online /Cleanup-Image /RestoreHealth & sfc /scannow\"")]
    public void Tools_start_from_system32_with_fixed_arguments(SystemTool tool, string exe, string args)
    {
        var psi = SystemToolLauncher.StartInfoFor(tool);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, exe), psi.FileName);
        Assert.Equal(args, psi.Arguments);
        Assert.False(psi.UseShellExecute);
    }

    // ── Gaming rows (spec §9.3) ─────────────────────────────────────────────

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData("2", null)]
    public void Hags_from_HwSchMode(object? raw, bool? expected) =>
        Assert.Equal(expected, SystemInfoService.Hags(raw));

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(null, 1, true)]     // captures are on unless turned off
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(null, null, false)]
    public void Background_recording_from_GameDVR(object? appCapture, object? historical, bool expected) =>
        Assert.Equal(expected, SystemInfoService.BackgroundRecording(appCapture, historical));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void Memory_integrity_from_Enabled(object? raw, bool expected) =>
        Assert.Equal(expected, SystemInfoService.MemoryIntegrity(raw));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(null, true)]        // Windows' default is on
    public void Game_mode_from_AutoGameModeEnabled(object? raw, bool expected) =>
        Assert.Equal(expected, SystemInfoService.GameMode(raw));

    [Fact]
    public void Reads_this_machines_gaming_settings() =>
        Assert.NotNull(new SystemInfoService().GetGamingInfo());

    [Theory]
    [InlineData(SystemTool.GraphicsSettings, "ms-settings:display-advancedgraphics")]
    [InlineData(SystemTool.CaptureSettings, "ms-settings:gaming-captures")]
    [InlineData(SystemTool.CoreIsolation, "windowsdefender://coreisolation")]
    [InlineData(SystemTool.GameModeSettings, "ms-settings:gaming-gamemode")]
    public void Windows_pages_open_through_explorer_with_a_fixed_uri(SystemTool tool, string uri)
    {
        var psi = SystemToolLauncher.StartInfoFor(tool);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), psi.FileName);
        Assert.Equal(uri, psi.Arguments);
        Assert.False(psi.UseShellExecute);
    }
}
