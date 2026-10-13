using System.Diagnostics;

namespace StandbyAndTimer.Services;

// Which of the listed games are running right now. Matches by exe name
// (file name without extension), the same way ProcessOptimizationService
// finds them: reading MainModule.FileName for every process needs access
// rights that anti-cheat protected games deny.
internal static class RunningGames
{
    /// <summary>
    /// Names of all running processes. One enumeration per call; the
    /// per-game GetProcessesByName this replaces enumerated everything once
    /// per game.
    /// </summary>
    public static HashSet<string> CurrentProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { names.Add(p.ProcessName); }
                catch { /* exited between enumeration and access */ }
            }
        }
        return names;
    }

    /// <summary>The paths from <paramref name="gamePaths"/> whose exe name is in <paramref name="runningNames"/>.</summary>
    public static List<string> Match(IEnumerable<string> gamePaths, IReadOnlySet<string> runningNames) =>
        gamePaths.Where(p => runningNames.Contains(Path.GetFileNameWithoutExtension(p))).ToList();
}
