using System.IO;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;

namespace StandbyAndTimer.Services;

internal sealed class GlobalTimerSetting : IGlobalTimerSetting
{
    internal const string KeyPath   = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    internal const string ValueName = "GlobalTimerResolutionRequests";

    // Windows 11 is build 22000+; Environment.OSVersion reports the real
    // build because app.manifest declares Windows 10/11 compatibility.
    public bool IsSupported => Environment.OSVersion.Version.Build >= 22000;

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is int v && v == 1;
            }
            catch (Exception ex)
            {
                Logger.Warn($"GlobalTimerSetting read: {ex.Message}");
                return false;
            }
        }
    }

    public bool TrySetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null) { Logger.Warn($"GlobalTimerSetting: HKLM\\{KeyPath} missing"); return false; }
            // Off = no value, which is Windows' own default (not a 0 left behind).
            if (enabled) key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            else         key.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Info($"GlobalTimerResolutionRequests {(enabled ? "set to 1" : "removed")} (takes effect after restart)");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Logger.Warn($"GlobalTimerSetting write: {ex.Message}");
            return false;
        }
    }

    public DateTime LastBootUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
