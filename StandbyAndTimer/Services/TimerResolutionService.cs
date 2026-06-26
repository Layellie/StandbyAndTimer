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
    private uint   _targetUnits = NativeMethods.TARGET_TIMER_RESOLUTION;
    private double _lastReportedMs;     // dedupe: don't fire event when sample didn't move
    private bool   _disposed;

    public bool IsActive { get; private set; }

    public event EventHandler<double>? ResolutionMeasured;

    public double Activate()
    {
        uint actualUnits = ApplyTimerRequest();
        double reportedMs = actualUnits / 10_000.0;
        _lastReportedMs   = reportedMs;
        Logger.Info($"Timer activated (sync): target={_targetUnits / 10_000.0:F3} ms, actual={reportedMs:F3} ms");
        return reportedMs;
    }

    public async Task<double> ActivateAsync(CancellationToken ct = default)
    {
        uint actualUnits = ApplyTimerRequest();

        // Short warm-up. The kernel sometimes publishes a transitional value
        // on the very first Set after a cold process start; re-querying after
        // a brief delay returns the steady-state value the watchdog will then
        // keep refreshing.
        await Task.Delay(150, ct).ConfigureAwait(false);

        if (NativeMethods.NtQueryTimerResolution(out _, out _, out uint current) == 0 && current > 0)
            actualUnits = current;

        double reportedMs = actualUnits / 10_000.0;
        _lastReportedMs   = reportedMs;
        Logger.Info($"Timer activated (async): target={_targetUnits / 10_000.0:F3} ms, actual={reportedMs:F3} ms");
        return reportedMs;
    }

    // Centralises the kernel-level setup so Activate / ActivateAsync stay in sync.
    // Returns the "current" resolution the kernel reports right after Set, in
    // 100-ns units (≈ 5000 for 0.5 ms when honored).
    private uint ApplyTimerRequest()
    {
        // 1. Discover the most aggressive resolution this chipset supports.
        //    NtQueryTimerResolution returns 100-ns units; the *maximum*
        //    parameter is the smallest period (most aggressive), so we use it.
        if (NativeMethods.NtQueryTimerResolution(out _, out uint maxRes, out _) == 0
            && maxRes > 0)
        {
            _targetUnits = Math.Min(maxRes, NativeMethods.TARGET_TIMER_RESOLUTION);
        }

        // 2. Nt high-resolution request (typically 0.5 ms). On Windows 10/11
        //    timer requests are per-process and coalesced to the most
        //    aggressive value. We intentionally do NOT call timeBeginPeriod(1)
        //    here: on Win 11 the legacy 1 ms request can clamp the process's
        //    effective minimum to 1 ms even when Nt asked for 0.5 ms, which
        //    is what caused the auto-start "1 ms instead of 0.5 ms" bug.
        //    IGNORE_TIMER_RESOLUTION opt-out is set once at App.OnStartup so
        //    this request survives minimize/hide.
        int status = NativeMethods.NtSetTimerResolution(_targetUnits, true, out uint actualUnits);
        Logger.Info(FormattableString.Invariant(
            $"NtSetTimerResolution: requested={_targetUnits} actual={actualUnits} status=0x{status:X8}"));

        // 3. Prevent system sleep while timer is active. Return value is the
        // previous state — irrelevant here; we only care that the flags got
        // applied. SetThreadExecutionState returns 0 only on bad-arg, which
        // we cover by passing well-known constants.
        _ = NativeMethods.SetThreadExecutionState(
            NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

        // 4. AVRT multimedia scheduling for this thread.
        uint taskIndex = 0;
        NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

        // 5. Self-heal watchdog — every 1 second, query the current resolution;
        //    if it drifted away from our target (e.g. another process released
        //    its request and the OS rolled back), force-Set again. Re-creating
        //    the watchdog on each Activate is harmless because Deactivate
        //    disposes it.
        _watchdog?.Dispose();
        _watchdog = new System.Threading.Timer(_ => SelfHeal(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        IsActive = true;
        _lastReportedMs = 0;
        return actualUnits;
    }

    // Samples the system time and records the deltas between consecutive
    // distinct values. Because GetSystemTimeAsFileTime is only updated on
    // timer interrupt, the median delta is the true active timer period.
    //
    // Total wall time = samples * actualTick ≈ 60 * 0.5 ms = ~30 ms. Cheap.
    private static double MeasureActualResolutionMs(int samples = 60)
    {
        try
        {
            var deltas = new long[samples];
            NativeMethods.GetSystemTimeAsFileTime(out long prev);

            int collected = 0;
            while (collected < samples)
            {
                long curr;
                do { NativeMethods.GetSystemTimeAsFileTime(out curr); }
                while (curr == prev);
                deltas[collected++] = curr - prev;
                prev = curr;
            }

            Array.Sort(deltas);
            long medianUnits = deltas[samples / 2];   // 100-ns units
            return medianUnits / 10_000.0;
        }
        catch (Exception ex)
        {
            // Fallback: report the requested resolution if sampling fails for
            // any reason — better a slightly optimistic number than crashing
            // the timer-activate path.
            Logger.Warn($"MeasureActualResolutionMs failed: {ex.Message}");
            return NativeMethods.TARGET_TIMER_RESOLUTION / 10_000.0;
        }
    }

    public void Deactivate()
    {
        if (!IsActive) return;

        _watchdog?.Dispose();
        _watchdog = null;

        // Shutdown path: both calls are best-effort — by the time we get
        // here we're about to lose the process, so a failed return is logged
        // but otherwise non-actionable.
        _ = NativeMethods.NtSetTimerResolution(_targetUnits, false, out _);
        _ = NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

        IsActive = false;
        _lastReportedMs = 0;
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
                _ = NativeMethods.NtSetTimerResolution(_targetUnits, true, out current);
            }

            // Surface the kernel's authoritative period to the UI. We deliberately
            // prefer this over sample-based measurement: GetSystemTimeAsFileTime
            // has its own granularity (16 ms cadence on Win 11 for non-multimedia
            // threads) and frequently reports a value larger than what the kernel
            // is actually firing the timer at, which made the UI show 1 ms even
            // when the timer was honoured at 0.5 ms.
            if (current > 0)
            {
                double measured = current / 10_000.0;
                if (Math.Abs(measured - _lastReportedMs) > 0.005)
                {
                    _lastReportedMs = measured;
                    ResolutionMeasured?.Invoke(this, measured);
                }
            }
        }
        catch { /* watchdog must never throw */ }
    }
}
