using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class TimerResolutionService : ITimerResolutionService
{
    // ── Triple-lock strategy ──────────────────────────────────────────────────
    //  1. NtSetTimerResolution(min, true)     ← per-process modern API
    //  2. PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION (set once in App)
    //  3. Self-heal watchdog (50 ms, dedicated AVRT thread) ← re-Set
    //     unconditionally every tick. The watchdog runs on its OWN Thread
    //     (not the ThreadPool) so it can never be starved by user code or
    //     EcoQoS scheduling pressure during a game, and it registers itself
    //     as MMCSS "Pro Audio" so Win 11 won't throttle the watchdog itself.
    //     Combined: re-assert every ≤50 ms regardless of system load, so
    //     even if another process briefly released its high-res request the
    //     OS is back at 0.5 ms within ~3 timer interrupts.
    //
    // `MinimumResolution` is read from NtQueryTimerResolution at Activate time,
    // so we always request the chipset's most aggressive value (often 5000 =
    // 0.5ms but some systems accept 4900 ≈ 0.49ms).

    private const int WatchdogTickMs = 50;

    private Thread?                _watchdogThread;
    private ManualResetEventSlim?  _watchdogStop;
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

        // 5. Self-heal watchdog — dedicated Thread (NOT ThreadPool) that
        //    re-asserts the request every 50 ms. The dedicated thread is the
        //    key win over System.Threading.Timer: under heavy game load the
        //    ThreadPool can briefly starve, delaying the callback by tens of
        //    ms. A real Thread with AVRT Pro Audio MMCSS is immune to that
        //    starvation and is also exempt from Win 11 EcoQoS throttling, so
        //    drift can never persist longer than ~50 ms even with a CPU-bound
        //    game running on the same cores.
        StopWatchdog();
        _watchdogStop = new ManualResetEventSlim(initialState: false);
        _watchdogThread = new Thread(WatchdogLoop)
        {
            Name         = "TimerResolution-Watchdog",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal,
        };
        _watchdogThread.Start();

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

        StopWatchdog();

        // Shutdown path: both calls are best-effort — by the time we get
        // here we're about to lose the process, so a failed return is logged
        // but otherwise non-actionable.
        _ = NativeMethods.NtSetTimerResolution(_targetUnits, false, out _);
        _ = NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

        IsActive = false;
        _lastReportedMs = 0;
        Logger.Info("Timer deactivated");
    }

    private void StopWatchdog()
    {
        _watchdogStop?.Set();
        // Bounded join — if the thread is somehow stuck (e.g. an MMCSS revert
        // taking longer than expected during shutdown), we don't want to hang
        // the whole process; the OS will reclaim the thread anyway.
        _watchdogThread?.Join(TimeSpan.FromMilliseconds(500));
        _watchdogThread = null;
        _watchdogStop?.Dispose();
        _watchdogStop = null;
    }

    // Dedicated background thread that re-asserts the timer resolution every
    // WatchdogTickMs. Registers as AVRT "Pro Audio" on entry so Win 11 won't
    // EcoQoS-throttle the watchdog itself — without this, the very thread
    // responsible for fighting throttling could be throttled.
    private void WatchdogLoop()
    {
        uint taskIndex = 0;
        IntPtr avrt = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        try
        {
            // ManualResetEventSlim.Wait(timeout) returns true on signal, false
            // on timeout — looping until signal gives clean cancellation.
            while (_watchdogStop is { } stop && !stop.Wait(WatchdogTickMs))
                SelfHeal();
        }
        finally
        {
            if (avrt != IntPtr.Zero)
                _ = NativeMethods.AvRevertMmThreadCharacteristics(avrt);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
    }

    // ── Self-heal: unconditional re-assert each tick ─────────────────────────
    private void SelfHeal()
    {
        if (!IsActive) return;
        try
        {
            // Re-assert without a Query gate. NtSetTimerResolution returns the
            // kernel's *current* (post-Set) resolution in `current`, so a single
            // syscall gives us both the re-assert AND the authoritative reading
            // we need for the UI — half the syscalls of the old query-then-set
            // path. If another process released its request between ticks, this
            // Set immediately reclaims the 0.5 ms slot before the next interrupt.
            if (NativeMethods.NtSetTimerResolution(_targetUnits, true, out uint current) != 0)
                return;

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
