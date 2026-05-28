using System.Diagnostics;
using StandbyAndTimer.Core.Interfaces;

namespace StandbyAndTimer.Services;

internal sealed class AutoStartService : IAutoStartService
{
    private const string TaskName = "StandbyAndTimer_AutoStart";

    public Task EnableAsync(string executablePath) =>
        RunSchtasksAsync(
            $"/create /tn \"{TaskName}\" /tr \"\\\"{executablePath}\\\" -hidden\" /sc onlogon /rl highest /f");

    public Task DisableAsync() =>
        RunSchtasksAsync($"/delete /tn \"{TaskName}\" /f");

    private static Task RunSchtasksAsync(string args) => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                WindowStyle    = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5_000); // don't block indefinitely
        }
        catch { /* schtasks may not be available in sandboxed environments */ }
    });
}
