namespace StandbyAndTimer.Core.Interfaces;

public interface IProcessOptimizationService
{
    /// <summary>
    /// Finds running processes matching the given executable paths,
    /// sets High priority + full CPU affinity, then disposes each Process handle.
    /// Already-optimized PIDs are skipped; exited PIDs are pruned.
    /// </summary>
    Task CheckAndOptimizeAsync(IReadOnlyList<string> executablePaths);

    /// <summary>Clears the set of known-optimized PIDs (e.g. on settings reset).</summary>
    void Clear();
}
