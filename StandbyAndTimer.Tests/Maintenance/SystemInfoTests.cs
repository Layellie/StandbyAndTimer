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

    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    [Theory]
    [InlineData("ded574b5-45a0-4f42-8737-46345c09c238", PowerMode.BestPerformance)]
    [InlineData("00000000-0000-0000-0000-000000000000", PowerMode.Balanced)]
    [InlineData("961cc777-2547-4f9d-8174-7d86181b8a7a", PowerMode.BestPowerEfficiency)]
    [InlineData("11111111-2222-3333-4444-555555555555", PowerMode.Unknown)]
    public void Power_mode_from_the_overlay_on_the_balanced_plan(string overlay, PowerMode expected) =>
        Assert.Equal(expected, SystemInfoService.PowerModeFrom(new Guid(overlay), Balanced));

    [Fact]
    public void Power_modes_exist_only_on_the_balanced_plan()
    {
        var highPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        Assert.Equal(PowerMode.NotOnThisPlan, SystemInfoService.PowerModeFrom(new Guid("ded574b5-45a0-4f42-8737-46345c09c238"), highPerformance));
        Assert.Equal(PowerMode.Unknown, SystemInfoService.PowerModeFrom(null, Balanced));
        Assert.Equal(PowerMode.Unknown, SystemInfoService.PowerModeFrom(Guid.Empty, null));
    }

    [Fact]
    public void Reads_this_machines_power_mode() =>
        Assert.True(Enum.IsDefined(new SystemInfoService().GetPowerMode()));

    [Fact]
    public void Power_settings_open_through_explorer() =>
        Assert.Equal("ms-settings:powersleep", SystemToolLauncher.StartInfoFor(SystemTool.PowerSettings).Arguments);
}
