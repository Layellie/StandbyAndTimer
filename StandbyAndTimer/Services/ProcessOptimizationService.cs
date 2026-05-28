using System.Diagnostics;
using StandbyAndTimer.Core.Interfaces;

namespace StandbyAndTimer.Services;

internal sealed class ProcessOptimizationService : IProcessOptimizationService
{
    // Only PIDs are stored — no Process handles kept alive between calls.
    private readonly HashSet<int> _optimizedPids = [];

    public Task CheckAndOptimizeAsync(IReadOnlyList<string> executablePaths) =>
        Task.Run(() => CheckAndOptimize(executablePaths));

    private void CheckAndOptimize(IReadOnlyList<string> executablePaths)
    {
        int  cpuCount     = Environment.ProcessorCount;
        long affinityMask = (1L << cpuCount) - 1;

        foreach (string path in executablePaths)
        {
            string procName = Path.GetFileNameWithoutExtension(path);
            Process[] found = Process.GetProcessesByName(procName);

            foreach (Process p in found)
            {
                using (p) // dispose handle immediately after use — no leaks
                {
                    if (_optimizedPids.Contains(p.Id)) continue;
                    try
                    {
                        p.PriorityClass     = ProcessPriorityClass.High;
                        p.ProcessorAffinity = (IntPtr)affinityMask;
                        _optimizedPids.Add(p.Id);
                    }
                    catch { /* process may exit between enumeration and access */ }
                }
            }
        }

        // Prune PIDs whose processes have since exited.
        _optimizedPids.RemoveWhere(pid =>
        {
            try   { using var p = Process.GetProcessById(pid); return p.HasExited; }
            catch { return true; }
        });
    }

    public void Clear() => _optimizedPids.Clear();
}
