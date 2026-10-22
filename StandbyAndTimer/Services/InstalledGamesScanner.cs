using System.IO;
using System.Text.Json;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Services;

/// <summary>An installed game discovered from a Steam library or Epic Games manifest.</summary>
public sealed record InstalledGameCandidate(string Name, string ExecutablePath, string Source);

/// <summary>
/// Scans local Steam libraries (<c>appmanifest_*.acf</c>) and Epic Games Launcher
/// manifests (<c>*.item</c>) to find installed games and their primary executables.
/// </summary>
internal static class InstalledGamesScanner
{
    private static readonly string[] IgnoredAppNames =
    [
        "Steamworks Common Redistributables",
        "Steam Linux Runtime",
        "Proton ",
        "SteamVR",
    ];

    private static readonly string[] IgnoredExePrefixes =
    [
        "unins",
        "unitycrashhandler",
        "crashreport",
        "crashhandler",
        "crashsender",
        "vc_redist",
        "vcredist",
        "dxsetup",
        "dotnet",
        "easyanticheat",
        "eac_launcher",
        "beservice",
        "battleye",
        "ue4prereqsetup",
        "ueprereqsetup",
        "redistr",
        "setup",
    ];

    public static Task<IReadOnlyList<InstalledGameCandidate>> ScanAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            string? steam = CleanupTargets.ReadSteamPath();
            var steamAppsDirs = CleanupTargets.SteamLibraryRoots(steam, CleanupTargets.ReadLibraryFolders(steam))
                .Select(l => Path.Combine(l, "steamapps"))
                .ToList();

            string epicManifests = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");

            return ScanDirectories(steamAppsDirs, [epicManifests], ct);
        }, ct);

    internal static IReadOnlyList<InstalledGameCandidate> ScanDirectories(
        IEnumerable<string> steamAppsDirs,
        IEnumerable<string> epicManifestDirs,
        CancellationToken ct = default)
    {
        var results = new List<InstalledGameCandidate>();
        var seenExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string steamApps in steamAppsDirs)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(steamApps)) continue;

            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string acf in manifests)
            {
                ct.ThrowIfCancellationRequested();
                if (TryReadSteamManifest(acf, steamApps) is { } candidate && seenExes.Add(candidate.ExecutablePath))
                    results.Add(candidate);
            }
        }

        foreach (string manifestDir in epicManifestDirs)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(manifestDir)) continue;

            IEnumerable<string> items;
            try { items = Directory.EnumerateFiles(manifestDir, "*.item"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string itemFile in items)
            {
                ct.ThrowIfCancellationRequested();
                if (TryReadEpicManifest(itemFile) is { } candidate && seenExes.Add(candidate.ExecutablePath))
                    results.Add(candidate);
            }
        }

        return results
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static InstalledGameCandidate? TryReadSteamManifest(string acfPath, string steamAppsDir)
    {
        string text;
        try { text = File.ReadAllText(acfPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        string? name = null;
        string? installDir = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string[] parts = rawLine.Trim().Split('"');
            // "key"<tabs>"value" -> "", key, <tabs>, value, ""
            if (parts.Length != 5 || parts[0].Length != 0 || parts[2].Trim().Length != 0 || parts[4].Length != 0)
                continue;
            if (parts[1].Equals("name", StringComparison.OrdinalIgnoreCase))
                name = parts[3].Trim();
            else if (parts[1].Equals("installdir", StringComparison.OrdinalIgnoreCase))
                installDir = parts[3].Replace(@"\\", @"\").Trim();
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
            return null;

        if (IgnoredAppNames.Any(ig => name.StartsWith(ig, StringComparison.OrdinalIgnoreCase)))
            return null;

        string gameRoot = Path.Combine(steamAppsDir, "common", installDir);
        if (!Directory.Exists(gameRoot)) return null;

        string? exe = FindBestExecutable(gameRoot, name, installDir);
        return exe is null ? null : new InstalledGameCandidate(name, exe, "Steam");
    }

    internal static InstalledGameCandidate? TryReadEpicManifest(string itemPath)
    {
        try
        {
            using var stream = File.OpenRead(itemPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            string? name = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
            string? installLoc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
            string? launchExe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installLoc))
                return null;
            if (!Directory.Exists(installLoc))
                return null;

            // Prefer the best game binary (e.g. Shipping exe if LaunchExecutable is an anti-cheat bootstrapper),
            // falling back to LaunchExecutable if it exists and is not an anti-cheat launcher.
            string? directExe = !string.IsNullOrWhiteSpace(launchExe)
                ? Path.GetFullPath(Path.Combine(installLoc, launchExe.Replace('/', '\\')))
                : null;

            if (directExe is not null && File.Exists(directExe) && !IsIgnoredExecutable(Path.GetFileNameWithoutExtension(directExe)))
                return new InstalledGameCandidate(name, directExe, "Epic");

            string? best = FindBestExecutable(installLoc, name, Path.GetFileName(installLoc));
            if (best is not null)
                return new InstalledGameCandidate(name, best, "Epic");

            if (directExe is not null && File.Exists(directExe))
                return new InstalledGameCandidate(name, directExe, "Epic");

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    internal static string? FindBestExecutable(string rootDir, string gameName, string folderName)
    {
        var candidates = new List<FileInfo>();
        CollectExecutables(new DirectoryInfo(rootDir), depth: 0, maxDepth: 3, candidates);
        if (candidates.Count == 0) return null;

        var filtered = candidates
            .Where(f => !IsIgnoredExecutable(Path.GetFileNameWithoutExtension(f.Name)))
            .ToList();
        if (filtered.Count == 0) filtered = candidates;

        string normGame = NormalizeAlphaNum(gameName);
        string normFolder = NormalizeAlphaNum(folderName);

        // Score: Unreal *-Shipping.exe or exact name match gets a bonus, plus file size in MB.
        return filtered
            .OrderByDescending(f => ScoreExecutable(f, normGame, normFolder))
            .ThenByDescending(f => f.Length)
            .First()
            .FullName;
    }

    private static double ScoreExecutable(FileInfo f, string normGame, string normFolder)
    {
        string stem = Path.GetFileNameWithoutExtension(f.Name);
        string normStem = NormalizeAlphaNum(stem);
        double score = f.Length / (1024.0 * 1024.0); // size in MB

        if (stem.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase))
            score += 500;
        if (normStem.Length > 0 && (normStem.Equals(normGame, StringComparison.OrdinalIgnoreCase)
                                 || normStem.Equals(normFolder, StringComparison.OrdinalIgnoreCase)))
            score += 300;
        if (stem.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            score -= 200;

        return score;
    }

    private static string NormalizeAlphaNum(string s) =>
        new(s.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsIgnoredExecutable(string stem) =>
        IgnoredExePrefixes.Any(prefix => stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || stem.Contains("crashhandler", StringComparison.OrdinalIgnoreCase)
        || stem.Contains("crashreport", StringComparison.OrdinalIgnoreCase)
        || stem.Contains("unins", StringComparison.OrdinalIgnoreCase);

    private static void CollectExecutables(DirectoryInfo dir, int depth, int maxDepth, List<FileInfo> sink)
    {
        try
        {
            foreach (var f in dir.EnumerateFiles("*.exe"))
                sink.Add(f);
            if (depth >= maxDepth) return;
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                CollectExecutables(sub, depth + 1, maxDepth, sink);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
