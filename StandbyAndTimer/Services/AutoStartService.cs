using System.Diagnostics;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Infrastructure;

namespace StandbyAndTimer.Services;

internal sealed class AutoStartService : IAutoStartService
{
    public Task<bool> EnableAsync(string executablePath) =>
        RunSchtasksAsync(
            $"/create /tn \"{AppConstants.AutoStartTaskName}\" /tr \"\\\"{executablePath}\\\" -hidden\" /sc onlogon /rl highest /f");

    public Task<bool> DisableAsync() =>
        RunSchtasksAsync($"/delete /tn \"{AppConstants.AutoStartTaskName}\" /f");

    // Returns true if schtasks ran to completion with exit code 0. Errors
    // (process couldn't start, timeout, non-zero exit, sandboxed environment)
    // all surface as `false` so the caller can show the user a failure status
    // — previously the result was swallowed and a failed AutoStart toggle
    // would silently leave the UI checkbox ON with no scheduled task behind it.
    private static Task<bool> RunSchtasksAsync(string args) => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                WindowStyle    = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5_000)) return false;
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"schtasks ({args}): {ex.Message}");
            return false;
        }
    });
}
