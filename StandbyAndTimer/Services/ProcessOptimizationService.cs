using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class ProcessOptimizationService : IProcessOptimizationService
{
    // ConcurrentDictionary so overlapping CheckAndOptimizeAsync calls (e.g. the
    // monitor tick still running when Clear() fires from the UI) cannot corrupt
    // the set. Only PIDs are stored — no Process handles kept alive between calls.
    private readonly ConcurrentDictionary<int, byte> _optimizedPids = new();

    public Task CheckAndOptimizeAsync(IReadOnlyList<string> executablePaths,
                                      Func<string, IReadOnlyList<int>>? pidsOf = null,
                                      Func<int, bool>? isRunning = null) =>
        Task.Run(() => CheckAndOptimize(executablePaths, pidsOf, isRunning));

    private void CheckAndOptimize(IReadOnlyList<string> executablePaths,
                                  Func<string, IReadOnlyList<int>>? pidsOf, Func<int, bool>? isRunning)
    {
        int  cpuCount     = Environment.ProcessorCount;
        long affinityMask = (1L << cpuCount) - 1;

        foreach (string path in executablePaths)
        {
            string procName = Path.GetFileNameWithoutExtension(path);
            foreach (Process p in Find(procName, pidsOf))
            {
                using (p) // dispose handle immediately after use — no leaks
                {
                    try
                    {
                        p.PriorityClass = ProcessPriorityClass.High;
                        // affinityMask is bounded by ProcessorCount (≤ 64). On a
                        // 64-CPU host the top bit is set, which makes the signed
                        // IntPtr cast nominally overflow — but we *want* the bit
                        // pattern preserved, so suppress CA2020 here rather than
                        // wrapping in checked() (which would throw) or unchecked()
                        // (which the analyzer still flags).
#pragma warning disable CA2020
                        p.ProcessorAffinity = (IntPtr)affinityMask;
#pragma warning restore CA2020
                        ApplyTimerResolutionOptOut(p);
                        _optimizedPids.TryAdd(p.Id, 0);
                    }
                    catch { /* process may exit between enumeration and access */ }
                }
            }
        }

        // Prune PIDs whose processes have since exited (the caller's snapshot
        // answers that without opening a handle).
        foreach (int pid in _optimizedPids.Keys)
        {
            bool exited;
            if (isRunning is not null) exited = !isRunning(pid);
            else
            {
                try   { using var p = Process.GetProcessById(pid); exited = p.HasExited; }
                catch { exited = true; }
            }
            if (exited) _optimizedPids.TryRemove(pid, out _);
        }
    }

    // Processes to optimize for one exe name, skipping PIDs already done.
    // With the caller's snapshot only new PIDs get a Process object; the
    // fallback snapshot (GetProcessesByName) returns all of them.
    private IEnumerable<Process> Find(string procName, Func<string, IReadOnlyList<int>>? pidsOf)
    {
        if (pidsOf is null)
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                if (_optimizedPids.ContainsKey(p.Id)) { p.Dispose(); continue; }
                yield return p;
            }
            yield break;
        }
        foreach (int pid in pidsOf(procName))
        {
            if (_optimizedPids.ContainsKey(pid)) continue;
            Process? p = null;
            try { p = Process.GetProcessById(pid); }
            catch { /* exited since the snapshot */ }
            if (p is not null) yield return p;
        }
    }

    // Tells Windows 11 that this process opts out of EcoQoS / timer-resolution
    // throttling. Without this, modern Windows clamps the game's timer back to
    // ~15.6 ms once it loses foreground focus, defeating our 0.5 ms global timer.
    private static void ApplyTimerResolutionOptOut(Process p)
    {
        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
        {
            Version     = 1,
            ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                        | NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask   = 0  // 0 = disable both throttles
        };
        try
        {
            NativeMethods.SetProcessInformation(
                p.Handle,
                NativeMethods.ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
        }
        catch (Exception ex)
        {
            Logger.Warn($"SetProcessInformation on PID {p.Id} failed: {ex.Message}");
        }
    }

    public void Clear() => _optimizedPids.Clear();
}
