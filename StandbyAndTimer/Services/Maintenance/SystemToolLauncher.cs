using System.Diagnostics;
using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// Opens Windows' own tools from System32 with fixed arguments (nothing from
/// user input). The app is elevated, so they start elevated too, which is
/// what dfrgui, cleanmgr and DISM/SFC need.
/// </summary>
internal sealed class SystemToolLauncher : ISystemToolLauncher
{
    public bool Launch(SystemTool tool)
    {
        try
        {
            using var p = Process.Start(StartInfoFor(tool));
            Logger.Info($"System tool opened: {tool}");
            return p is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"System tool {tool} failed to start: {ex.Message}");
            return false;
        }
    }

    internal static ProcessStartInfo StartInfoFor(SystemTool tool)
    {
        // Windows' settings pages: a fixed URI handed to explorer.exe.
        string? uri = tool switch
        {
            SystemTool.GraphicsSettings => "ms-settings:display-advancedgraphics",
            SystemTool.CaptureSettings  => "ms-settings:gaming-captures",
            SystemTool.CoreIsolation    => "windowsdefender://coreisolation",
            SystemTool.GameModeSettings => "ms-settings:gaming-gamemode",
            SystemTool.PowerSettings    => "ms-settings:powersleep",
            _ => null,
        };
        if (uri is not null)
            return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), uri)
                   { UseShellExecute = false };

        string sys = Environment.SystemDirectory;
        (string exe, string args) = tool switch
        {
            SystemTool.OptimizeDrives    => ("dfrgui.exe", ""),
            SystemTool.DiskCleanup       => ("cleanmgr.exe", ""),
            SystemTool.PowerOptions      => ("control.exe", "/name Microsoft.PowerOptions"),
            SystemTool.VirtualMemory     => ("SystemPropertiesPerformance.exe", ""),
            // A visible console; "&" runs SFC even when DISM fails (no internet).
            SystemTool.RepairSystemFiles => ("cmd.exe", "/k \"DISM /Online /Cleanup-Image /RestoreHealth & sfc /scannow\""),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
        };
        return new ProcessStartInfo(Path.Combine(sys, exe), args) { UseShellExecute = false };
    }
}
