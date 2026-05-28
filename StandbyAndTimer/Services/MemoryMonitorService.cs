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

    private PerformanceCounter? _standbyCounter;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool  _disposed;

    // ── IMemoryMonitorService configuration ──────────────────────────────────
    public int StandbyLimitMb  { get; set; } = 1024;
    public int FreeLimitMb     { get; set; } = 1024;
    public bool GameModeEnabled { get; set; }
    public IReadOnlyList<string> GamePaths { get; set; } = [];

    public event EventHandler<MemorySnapshot>? SnapshotUpdated;

    public MemoryMonitorService(
        IStandbyPurgeService purgeService,
        IProcessOptimizationService processService)
    {
        _purgeService   = purgeService;
        _processService = processService;

        // Initialise once — PerformanceCounter is expensive to create repeatedly.
        _standbyCounter = new PerformanceCounter("Memory", "Standby Cache Reserve Bytes", null);

        // PerformanceCounter.NextValue() returns 0 on the very first call; warm
        // it up here so the first user-visible snapshot already has a real value.
        try { _ = _standbyCounter.NextValue(); } catch { /* counter unavailable */ }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
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
        // Register this worker thread as an AVRT "Games" task so Windows never
        // throttles it, even when the main window is hidden to the system tray.
        uint taskIndex = 0;
        IntPtr avrtHandle = NativeMethods.AvSetMmThreadCharacteristics("Games", ref taskIndex);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var snapshot = ReadSnapshot();
                SnapshotUpdated?.Invoke(this, snapshot);

                bool purgeNeeded = StandbyLimitMb > 0
                    && snapshot.StandbyMb >= StandbyLimitMb
                    && snapshot.FreeMb    <= FreeLimitMb;

                if (purgeNeeded)
                    await _purgeService.PurgeAsync().ConfigureAwait(false);

                if (GameModeEnabled && GamePaths.Count > 0)
                    await _processService.CheckAndOptimizeAsync(GamePaths).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (avrtHandle != IntPtr.Zero)
                NativeMethods.AvRevertMmThreadCharacteristics(avrtHandle);
        }
    }

    private MemorySnapshot ReadSnapshot()
    {
        var mem = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref mem);

        long totalMb   = (long)(mem.ullTotalPhys / (1024 * 1024));
        long freeMb    = (long)(mem.ullAvailPhys  / (1024 * 1024));
        long standbyMb = 0;

        try   { standbyMb = (long)(_standbyCounter!.NextValue() / (1024 * 1024)); }
        catch { /* PerformanceCounter may fail transiently; carry last known value */ }

        return new MemorySnapshot(totalMb, freeMb, standbyMb);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _standbyCounter?.Dispose();
        _standbyCounter = null;
    }
}
