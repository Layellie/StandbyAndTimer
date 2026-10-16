using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// The previous Windows installation (Windows.old). Its files are protected,
/// so the app never deletes in it: Windows' own Disk Cleanup removes it, run
/// silently for its "Previous Installations" item only (a sagerun preset the
/// app sets just for the run and removes afterwards). Freed = size before
/// minus after. Afterwards Windows can no longer go back to that version.
/// </summary>
internal sealed class WindowsOldTarget : ICleanupTarget
{
    private const string Preset          = "9917";
    private const string HandlerKey      = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Previous Installations";
    private const string FlagName        = "StateFlags" + Preset;
    private const int    CommandTimeoutMs = 30 * 60 * 1000;   // removing a whole Windows can take long

    private readonly string _folder;
    private readonly Func<bool> _runDiskCleanup;

    // DI uses this one (it only sees public constructors).
    public WindowsOldTarget()
        : this(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Windows.old"), RunDiskCleanup) { }

    // Tests: a temp folder and a stand-in for Disk Cleanup (true = it succeeded).
    internal WindowsOldTarget(string folder, Func<bool> runDiskCleanup)
    {
        _folder         = folder;
        _runDiskCleanup = runDiskCleanup;
    }

    public string Id => CleanupTargetIds.WindowsOld;
    public bool BlockedWhileGameRuns => false;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Scan([_folder], null, DateTime.UtcNow, ct), ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct) => Task.Run(() =>
    {
        var before = SafeFolderCleaner.Scan([_folder], null, DateTime.UtcNow, ct);
        if (!before.Found) return CleanupResult.Nothing;
        if (!_runDiskCleanup())
            throw new InvalidOperationException("Disk Cleanup could not remove the previous Windows installation");
        var after = SafeFolderCleaner.Scan([_folder], null, DateTime.UtcNow, ct);
        return new CleanupResult(Math.Max(0, before.Bytes - after.Bytes), Math.Max(0, before.Files - after.Files), after.Files);
    }, ct);

    internal static ProcessStartInfo DiskCleanupCommand() =>
        new(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), "/sagerun:" + Preset) { UseShellExecute = false };

    /// <summary>Sets the preset, runs, and always removes the preset again.</summary>
    internal static bool RunWithPreset(Action setPreset, Action clearPreset, Func<bool> run)
    {
        setPreset();
        try { return run(); }
        finally { clearPreset(); }
    }

    private static bool RunDiskCleanup()
    {
        try
        {
            return RunWithPreset(
                () =>
                {
                    using var key = Registry.LocalMachine.OpenSubKey(HandlerKey, writable: true)
                                    ?? throw new InvalidOperationException("Disk Cleanup has no Previous Installations item");
                    key.SetValue(FlagName, 2, RegistryValueKind.DWord);
                },
                () =>
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(HandlerKey, writable: true);
                        key?.DeleteValue(FlagName, throwOnMissingValue: false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        Logger.Warn($"Disk Cleanup preset not removed: {ex.Message}");
                    }
                },
                () =>
                {
                    using var p = Process.Start(DiskCleanupCommand());
                    if (p is null) return false;
                    if (!p.WaitForExit(CommandTimeoutMs))
                    {
                        Logger.Warn("Disk Cleanup did not finish in 30 min");
                        return false;
                    }
                    return true;   // cleanmgr reports no useful exit code; the size after tells what happened
                });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Disk Cleanup for Windows.old failed: {ex.Message}");
            return false;
        }
    }
}
