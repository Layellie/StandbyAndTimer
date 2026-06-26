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
                    if (_optimizedPids.ContainsKey(p.Id)) continue;
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

        // Prune PIDs whose processes have since exited.
        foreach (int pid in _optimizedPids.Keys)
        {
            bool exited;
            try   { using var p = Process.GetProcessById(pid); exited = p.HasExited; }
            catch { exited = true; }
            if (exited) _optimizedPids.TryRemove(pid, out _);
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
                34,  // ProcessPowerThrottling
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
