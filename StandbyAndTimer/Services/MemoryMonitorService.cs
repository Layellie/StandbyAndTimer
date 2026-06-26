using System.Diagnostics;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class MemoryMonitorService : IMemoryMonitorService
{
    private readonly IStandbyPurgeService       _purgeService;
    private readonly IProcessOptimizationService _processService;

    // The total Windows standby list = Normal Priority + Reserve + Core.
    // Reading only one bucket (as the original did) understated the value by
    // an order of magnitude on most systems. Counters are created lazily on
    // the loop thread because their constructor is expensive (~1 s on a cold
    // process). Building them in the service ctor blocked the UI thread for
    // ~4.8 s during app startup, since this service is resolved by DI before
    // the window is shown.
    private PerformanceCounter? _standbyNormal;
    private PerformanceCounter? _standbyReserve;
    private PerformanceCounter? _standbyCore;
    private bool _countersInitialized;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool  _disposed;

    public int  StandbyLimitMb   { get; set; }
    public int  FreeLimitMb      { get; set; }
    public bool AutoPurgeEnabled { get; set; }
    public bool GameModeEnabled  { get; set; }
    public IReadOnlyList<string> GamePaths { get; set; } = [];

    public event EventHandler<MemorySnapshot>? SnapshotUpdated;

    public MemoryMonitorService(
        IStandbyPurgeService purgeService,
        IProcessOptimizationService processService)
    {
        _purgeService   = purgeService;
        _processService = processService;
        // Counters are created in EnsureCountersInitialized on the loop thread.
    }

    private void EnsureCountersInitialized()
    {
        if (_countersInitialized) return;
        _standbyNormal  = TryCreateCounter("Standby Cache Normal Priority Bytes");
        _standbyReserve = TryCreateCounter("Standby Cache Reserve Bytes");
        _standbyCore    = TryCreateCounter("Standby Cache Core Bytes");
        _countersInitialized = true;
    }

    private static PerformanceCounter? TryCreateCounter(string counterName)
    {
        try
        {
            var c = new PerformanceCounter("Memory", counterName, null);
            // PerformanceCounter.NextValue() returns 0 on the very first call;
            // warm it up so the first user-visible snapshot has a real value.
            try { _ = c.NextValue(); } catch { /* counter unavailable */ }
            return c;
        }
        catch (Exception ex)
        {
            Logger.Warn($"MemoryMonitorService: counter '{counterName}' unavailable: {ex.Message}");
            return null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Run the loop on a thread-pool thread so that
        // EnsureCountersInitialized — which can take ~1 s for the first
        // PerformanceCounter construction on a cold process — never touches
        // the UI thread or the synchronous StartAsync caller.
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loopTask is not null)
            await _loopTask.ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // AVRT MMCSS registration is intentionally NOT used here:
        // it's per-thread and is lost across every `await` (the continuation
        // resumes on a different ThreadPool worker), so it would only leak
        // an avrt handle without providing any scheduling benefit for a
        // 1-second poll. Timer-level MMCSS lives in TimerResolutionService.

        // First call after construction; runs off the UI thread because
        // StartAsync queued us via Task.Run. Cancellation between Start and
        // the first tick is honoured.
        EnsureCountersInitialized();
        if (ct.IsCancellationRequested) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var snapshot = ReadSnapshot();
                SnapshotUpdated?.Invoke(this, snapshot);

                // Master switch: AutoPurgeEnabled must be ON, and both thresholds
                // must be > 0. Defaults ship as OFF + 0/0 so a fresh install never
                // touches the standby list until the user explicitly opts in.
                bool purgeNeeded = AutoPurgeEnabled
                    && StandbyLimitMb > 0
                    && FreeLimitMb    > 0
                    && snapshot.StandbyMb >= StandbyLimitMb
                    && snapshot.FreeMb    <= FreeLimitMb;

                if (purgeNeeded)
                    await _purgeService.PurgeAsync().ConfigureAwait(false);

                if (GameModeEnabled && GamePaths.Count > 0)
                    await _processService.CheckAndOptimizeAsync(GamePaths).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private MemorySnapshot ReadSnapshot()
    {
        var mem = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref mem);

        long totalMb     = (long)(mem.ullTotalPhys / (1024 * 1024));
        long availableMb = (long)(mem.ullAvailPhys / (1024 * 1024));
        long standbyMb   = ReadStandbyMb();

        // GlobalMemoryStatusEx.ullAvailPhys is "Available" = standby + free + zero.
        // Surfacing that as "Free" makes the UI double-count cache memory and
        // — when standby is the dominant bucket — makes Free track Standby
        // almost 1:1 (the "saçma değer" the user reported). Subtract standby
        // to expose the truly free + zero portion. Clamp at 0 to guard against
        // the race where standby was read just before a purge dropped it
        // below the value still cached in mem.ullAvailPhys.
        long freeMb = Math.Max(0, availableMb - standbyMb);

        return new MemorySnapshot(totalMb, freeMb, standbyMb);
    }

    private long ReadStandbyMb()
    {
        double bytes = 0;
        bytes += SafeNext(_standbyNormal);
        bytes += SafeNext(_standbyReserve);
        bytes += SafeNext(_standbyCore);
        return (long)(bytes / (1024 * 1024));
    }

    private static float SafeNext(PerformanceCounter? counter)
    {
        if (counter is null) return 0;
        try   { return counter.NextValue(); }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        _standbyNormal?.Dispose();
        _standbyReserve?.Dispose();
        _standbyCore?.Dispose();
        _standbyNormal = _standbyReserve = _standbyCore = null;
    }
}
