using System.IO;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>A cleanup row backed by one or more folders, through SafeFolderCleaner.</summary>
internal sealed class FolderCleanupTarget : ICleanupTarget
{
    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly TimeSpan? _keepNewerThan;

    public FolderCleanupTarget(string id, IReadOnlyList<string> roots,
                               TimeSpan? keepNewerThan = null, bool blockedWhileGameRuns = false)
        : this(id, () => roots, keepNewerThan, blockedWhileGameRuns) { }

    // Roots read at each scan and clean (Steam libraries can be added any time).
    public FolderCleanupTarget(string id, Func<IReadOnlyList<string>> roots,
                               TimeSpan? keepNewerThan = null, bool blockedWhileGameRuns = false)
    {
        Id                   = id;
        _roots               = roots;
        _keepNewerThan       = keepNewerThan;
        BlockedWhileGameRuns = blockedWhileGameRuns;
    }

    public string Id { get; }
    public bool BlockedWhileGameRuns { get; }
    public IReadOnlyList<string> Roots => _roots();

    // Tests set a fixed clock for the keep window.
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Scan(_roots(), _keepNewerThan, UtcNow(), ct), ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Clean(_roots(), _keepNewerThan, UtcNow(), ct), ct);
}

/// <summary>The default folder targets (spec §4.2, §9.1). Row order = registration order in AppBootstrapper.</summary>
internal static class CleanupTargets
{
    /// <summary>Temp folders keep files written in the last 24 hours (a running installer may need them).</summary>
    internal static readonly TimeSpan TempKeepWindow = TimeSpan.FromHours(24);

    private static string Local       => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string LocalLow    => Path.Combine(Path.GetDirectoryName(Local)!, "LocalLow");
    private static string Common      => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string Windows     => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory)!;

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

    /// <summary>Windows' own DirectX shader cache.</summary>
    public static FolderCleanupTarget DirectXShader() =>
        new(CleanupTargetIds.DirectXShader, [Path.Combine(Local, "D3DSCache")], blockedWhileGameRuns: true);

    /// <summary>steamapps\shadercache of every Steam library, looked up at each scan.</summary>
    public static FolderCleanupTarget SteamShader() =>
        new(CleanupTargetIds.SteamShader, () =>
        {
            string? steam = ReadSteamPath();
            return SteamShaderRoots(steam, ReadLibraryFolders(steam));
        }, blockedWhileGameRuns: true);

    /// <summary>Driver installers that NVIDIA and AMD extract to the system drive and leave there.</summary>
    public static FolderCleanupTarget DriverLeftovers() =>
        new(CleanupTargetIds.DriverLeftovers, [Path.Combine(SystemDrive, "NVIDIA"), Path.Combine(SystemDrive, "AMD")]);

    public static FolderCleanupTarget UserTemp() =>
        new(CleanupTargetIds.UserTemp, [Path.TrimEndingDirectorySeparator(Path.GetTempPath())], TempKeepWindow);

    public static FolderCleanupTarget WindowsTemp() =>
        new(CleanupTargetIds.WindowsTemp, [Path.Combine(Windows, "Temp")], TempKeepWindow);

    public static FolderCleanupTarget WindowsUpdate() =>
        new(CleanupTargetIds.WindowsUpdate, [Path.Combine(Windows, "SoftwareDistribution", "Download")]);

    /// <summary>Crash dumps and Windows Error Reporting reports (per user and machine wide).</summary>
    public static FolderCleanupTarget CrashDumps() => new(CleanupTargetIds.CrashDumps,
    [
        Path.Combine(Local, "CrashDumps"),
        Path.Combine(Local, "Microsoft", "Windows", "WER", "ReportArchive"),
        Path.Combine(Local, "Microsoft", "Windows", "WER", "ReportQueue"),
        Path.Combine(Common, "Microsoft", "Windows", "WER", "ReportArchive"),
        Path.Combine(Common, "Microsoft", "Windows", "WER", "ReportQueue"),
    ]);

    /// <summary>
    /// The shader cache folder of the Steam install and of every library in
    /// libraryfolders.vdf (lines <c>"path"  "D:\\SteamLibrary"</c>). The file is
    /// user-editable: anything that is not such a line with a full path is skipped.
    /// </summary>
    internal static IReadOnlyList<string> SteamShaderRoots(string? steamPath, string? libraryFoldersVdf)
    {
        var libraries = new List<string>();
        if (FullPath(steamPath) is { } steam) libraries.Add(steam);
        foreach (string line in (libraryFoldersVdf ?? "").Split('\n'))
        {
            // "path"<tabs>"D:\\SteamLibrary" splits into: "", path, <tabs>, D:\\SteamLibrary, ""
            string[] parts = line.Trim().Split('"');
            if (parts.Length != 5 || parts[0].Length != 0 || parts[1] != "path" ||
                parts[2].Trim().Length != 0 || parts[4].Length != 0) continue;
            if (FullPath(parts[3].Replace(@"\\", @"\")) is { } library) libraries.Add(library);
        }
        return libraries.Select(l => Path.Combine(l, "steamapps", "shadercache"))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Replace('/', '\\');
        if (!Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? ReadSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string? ReadLibraryFolders(string? steamPath)
    {
        if (FullPath(steamPath) is not { } steam) return null;
        try { return File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
