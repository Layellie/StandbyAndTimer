using System.Diagnostics;
using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// The Delivery Optimization cache (update files Windows shares with other PCs).
/// The folder belongs to a Windows service, so the app never deletes in it:
/// Windows' own Delete-DeliveryOptimizationCache empties it, and the freed
/// size is the folder size before minus after. The folder is readable only
/// elevated; unreadable counts as not found.
/// </summary>
internal sealed class DeliveryOptimizationTarget : ICleanupTarget
{
    private const int CommandTimeoutMs = 60_000;

    private readonly string _folder;
    private readonly Func<bool> _deleteCache;

    // DI uses this one (it only sees public constructors).
    public DeliveryOptimizationTarget()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ServiceProfiles", "NetworkService",
                            "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), RunDeleteCommand) { }

    // Tests: a temp folder and a stand-in for the Windows command (true = it succeeded).
    internal DeliveryOptimizationTarget(string folder, Func<bool> deleteCache)
    {
        _folder      = folder;
        _deleteCache = deleteCache;
    }

    public string Id => CleanupTargetIds.DeliveryOptimization;
    public bool BlockedWhileGameRuns => false;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Scan([_folder], null, DateTime.UtcNow, ct), ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct) => Task.Run(() =>
    {
        var before = SafeFolderCleaner.Scan([_folder], null, DateTime.UtcNow, ct);
        if (!before.Found) return CleanupResult.Nothing;
        if (!_deleteCache())
            throw new InvalidOperationException("Delete-DeliveryOptimizationCache failed");
        var after = SafeFolderCleaner.Scan([_folder], null, DateTime.UtcNow, ct);
        return new CleanupResult(Math.Max(0, before.Bytes - after.Bytes), Math.Max(0, before.Files - after.Files), after.Files);
    }, ct);

    internal static ProcessStartInfo DeleteCommand() =>
        new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -Command Delete-DeliveryOptimizationCache -Force")
        { UseShellExecute = false, CreateNoWindow = true };

    private static bool RunDeleteCommand()
    {
        try
        {
            using var p = Process.Start(DeleteCommand());
            if (p is null) return false;
            if (!p.WaitForExit(CommandTimeoutMs))
            {
                Logger.Warn("Delete-DeliveryOptimizationCache did not finish in 60 s");
                try { p.Kill(); } catch (InvalidOperationException) { }
                return false;
            }
            if (p.ExitCode != 0) Logger.Warn($"Delete-DeliveryOptimizationCache exit code {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warn($"Delete-DeliveryOptimizationCache could not start: {ex.Message}");
            return false;
        }
    }
}
