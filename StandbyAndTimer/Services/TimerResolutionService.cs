using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class TimerResolutionService : ITimerResolutionService
{
    // ── Triple-lock strategy ──────────────────────────────────────────────────
    //  1. NtSetTimerResolution(min, true)     ← per-process modern API
    //  2. timeBeginPeriod(1) (winmm)          ← legacy MMTimer double-binding
    //  3. PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION (set once in App)
    //  4. Self-heal watchdog (1 s tick)       ← if OS drift, NtQuery returns
    //                                            a value != target → re-Set.
    //
    // `MinimumResolution` is read from NtQueryTimerResolution at Activate time,
    // so we always request the chipset's most aggressive value (often 5000 =
    // 0.5ms but some systems accept 4900 ≈ 0.49ms).

    private System.Threading.Timer? _watchdog;
    private uint  _targetUnits = NativeMethods.TARGET_TIMER_RESOLUTION;
    private bool  _disposed;

    public bool IsActive { get; private set; }

    public double Activate()
    {
        // 1. Discover the most aggressive resolution this chipset supports.
        //    NtQueryTimerResolution returns 100-ns units; the *maximum*
        //    parameter is the smallest period (most aggressive), so we use it.
        if (NativeMethods.NtQueryTimerResolution(out _, out uint maxRes, out _) == 0
            && maxRes > 0)
        {
            _targetUnits = Math.Min(maxRes, NativeMethods.TARGET_TIMER_RESOLUTION);
        }

        // 2. winmm fallback — guarantees at least 1 ms even without Nt success.
        NativeMethods.TimeBeginPeriod(1);

        // 3. Nt high-resolution request (typically 0.5 ms).
        //    IGNORE_TIMER_RESOLUTION opt-out is set once at App.OnStartup so this
        //    request survives minimize / hide / background.
        NativeMethods.NtSetTimerResolution(_targetUnits, true, out _);

        // 4. Prevent system sleep while timer is active.
        NativeMethods.SetThreadExecutionState(
            NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

        // 5. AVRT multimedia scheduling for this thread.
        uint taskIndex = 0;
        NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

        // 6. Self-heal watchdog — every 1 second, query the current resolution;
        //    if it drifted away from our target (e.g. another process released
        //    its request and the OS rolled back), force-Set again.
        _watchdog = new System.Threading.Timer(_ => SelfHeal(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        IsActive = true;

        double ms = _targetUnits / 10_000.0;
        Logger.Info($"Timer activated: target={_targetUnits} (100-ns) = {ms:F3} ms");
        return ms;
    }

    public void Deactivate()
    {
        if (!IsActive) return;

        _watchdog?.Dispose();
        _watchdog = null;

        NativeMethods.NtSetTimerResolution(_targetUnits, false, out _);
        NativeMethods.TimeEndPeriod(1);
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

        IsActive = false;
        Logger.Info("Timer deactivated");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
    }

    // ── Self-heal: re-assert only when actual ≠ target (cheap NtQuery first) ─
    private void SelfHeal()
    {
        if (!IsActive) return;
        try
        {
            if (NativeMethods.NtQueryTimerResolution(out _, out _, out uint current) != 0)
                return;

            // Allow ±1 unit of tolerance — OS occasionally reports the rounded value.
            if (current == 0 || Math.Abs((long)current - _targetUnits) > 1)
            {
                NativeMethods.NtSetTimerResolution(_targetUnits, true, out _);
            }
        }
        catch { /* watchdog must never throw */ }
    }
}
