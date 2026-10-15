using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>A cleanup row backed by one or more folders, through SafeFolderCleaner.</summary>
internal sealed class FolderCleanupTarget(string id, IReadOnlyList<string> roots,
                                          TimeSpan? keepNewerThan = null, bool blockedWhileGameRuns = false) : ICleanupTarget
{
    public string Id => id;
    public bool BlockedWhileGameRuns => blockedWhileGameRuns;
    public IReadOnlyList<string> Roots => roots;

    // Tests set a fixed clock for the keep window.
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Scan(roots, keepNewerThan, UtcNow(), ct), ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Clean(roots, keepNewerThan, UtcNow(), ct), ct);
}

/// <summary>The default folder targets (spec §4.2). Row order = registration order in AppBootstrapper.</summary>
internal static class CleanupTargets
{
    /// <summary>Temp folders keep files written in the last 24 hours (a running installer may need them).</summary>
    internal static readonly TimeSpan TempKeepWindow = TimeSpan.FromHours(24);

    private static string Local    => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string LocalLow => Path.Combine(Path.GetDirectoryName(Local)!, "LocalLow");
    private static string Common   => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string Windows  => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static FolderCleanupTarget NvidiaShader() => new(CleanupTargetIds.NvidiaShader,
    [
        Path.Combine(Local, "NVIDIA", "DXCache"),
        Path.Combine(Local, "NVIDIA", "GLCache"),
        Path.Combine(LocalLow, "NVIDIA", "PerDriverVersion", "DXCache"),
        Path.Combine(Common, "NVIDIA Corporation", "NV_Cache"),
    ], blockedWhileGameRuns: true);

    public static FolderCleanupTarget AmdShader() => new(CleanupTargetIds.AmdShader,
    [
        Path.Combine(Local, "AMD", "DxCache"),
        Path.Combine(Local, "AMD", "DxcCache"),
        Path.Combine(Local, "AMD", "VkCache"),
        Path.Combine(Local, "AMD", "GLCache"),
    ], blockedWhileGameRuns: true);

    public static FolderCleanupTarget UserTemp() =>
        new(CleanupTargetIds.UserTemp, [Path.TrimEndingDirectorySeparator(Path.GetTempPath())], TempKeepWindow);

    public static FolderCleanupTarget WindowsTemp() =>
        new(CleanupTargetIds.WindowsTemp, [Path.Combine(Windows, "Temp")], TempKeepWindow);

    public static FolderCleanupTarget WindowsUpdate() =>
        new(CleanupTargetIds.WindowsUpdate, [Path.Combine(Windows, "SoftwareDistribution", "Download")]);
}
