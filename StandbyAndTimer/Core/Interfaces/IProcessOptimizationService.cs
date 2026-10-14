namespace StandbyAndTimer.Core.Interfaces;

public interface IProcessOptimizationService
{
    /// <summary>
    /// Finds running processes matching the given executable paths,
    /// sets High priority + full CPU affinity, then disposes each Process handle.
    /// Already-optimized PIDs are skipped; exited PIDs are pruned.
    /// <paramref name="pidsOf"/> (exe name → PIDs) and <paramref name="isRunning"/>
    /// come from the caller's process snapshot; without them the service
    /// takes its own (Process.GetProcessesByName per game).
    /// </summary>
    Task CheckAndOptimizeAsync(IReadOnlyList<string> executablePaths,
                               Func<string, IReadOnlyList<int>>? pidsOf = null,
                               Func<int, bool>? isRunning = null);

    /// <summary>Clears the set of known-optimized PIDs (e.g. on settings reset).</summary>
    void Clear();
}
