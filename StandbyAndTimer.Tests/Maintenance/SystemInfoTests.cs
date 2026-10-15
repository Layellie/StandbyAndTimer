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
}
