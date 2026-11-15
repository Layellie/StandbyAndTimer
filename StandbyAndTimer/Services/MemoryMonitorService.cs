using System.Diagnostics;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class MemoryMonitorService : IMemoryMonitorService
{
    private readonly IStandbyPurgeService        _purgeService;
    private readonly IProcessOptimizationService _processService;
    private readonly IIdleMonitor                _idleMonitor;

    // The standby list (all 8 priorities), its priority-0 part and the
    // modified list come from MemoryLists (SystemMemoryListInformation): the
    // same standby total as the Memory "Standby Cache *" counters, in ~0.2 µs
    // instead of ~150 µs, without the ~9 MB / 0.5 s the counters cost. The
    // counters remain as the fallback if the query ever fails; they are then
    // created lazily on the loop thread (their constructor blocked the UI
    // thread for seconds when built in the service ctor).
    private bool _nativeListsFailed;

    private PerformanceCounter? _standbyNormal;
    private PerformanceCounter? _standbyReserve;
    private PerformanceCounter? _standbyCore;
    private bool _countersInitialized;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool  _disposed;

    public int  StandbyLimitMb           { get; set; }
    public int  FreeLimitMb              { get; set; }
    public bool AutoPurgeEnabled         { get; set; }
    public bool GameModeEnabled          { get; set; }
    public bool AutoPurgeIdleOnly        { get; set; }
    public int  IdleThresholdMs          { get; set; }
    public int  AutoPurgeCooldownSeconds { get; set; }
    public bool AutoPurgeOnlyInGames     { get; set; }
    public IReadOnlyList<string> GamePaths            { get; set; } = [];
    public IReadOnlyList<string> AboveNormalGamePaths { get; set; } = [];
    public IReadOnlyList<string> NoAutoPurgeGamePaths { get; set; } = [];

    public IReadOnlyList<string> WatchedGamePaths
    {
        get => _watched.Paths;
        set => _watched = Watched.From(value);
    }

    /// <summary>
    /// The watched list as each tick needs it, worked out once when the list
    /// changes (not every second): each path with its exe name, and the names
    /// as the set the process snapshot looks for. Swapped as one object, so a
    /// tick never sees half of a change.
    /// </summary>
    private sealed record Watched(IReadOnlyList<string> Paths, (string Path, string Name)[] Games, HashSet<string> Names)
    {
        public static readonly Watched None = From([]);

        public static Watched From(IReadOnlyList<string> paths)
        {
            var games = paths.Select(p => (p, Path.GetFileNameWithoutExtension(p))).ToArray();
            return new Watched(paths, games,
                               new HashSet<string>(games.Select(g => g.Item2), StringComparer.OrdinalIgnoreCase));
        }
    }

    private volatile Watched _watched = Watched.None;

    // Tests can inject a deterministic clock to verify Smart Cooldown gating.
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    private DateTime? _lastAutoPurgeUtc;

    public event EventHandler<MemorySnapshot>? SnapshotUpdated;

    public MemoryMonitorService(
        IStandbyPurgeService purgeService,
        IProcessOptimizationService processService,
        IIdleMonitor idleMonitor)
    {
        _purgeService   = purgeService;
        _processService = processService;
        _idleMonitor    = idleMonitor;
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

        if (ct.IsCancellationRequested) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // One bad tick must not end the loop: auto purge, game
                // boost and game detection would stop until a restart.
                try { await TickAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warn($"MemoryMonitor tick: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync()
    {
        // Which listed games run right now: one process snapshot per tick,
        // and only when there is a list to check. Game boost reuses it.
        // The snapshot holds the listed names alone (boosted games are among them).
        var watched = _watched;
        ProcessSnapshot? processes = watched.Games.Length > 0 ? RunningGames.Take(watched.Names) : null;
        List<string> running = [];
        if (processes is { Names.Count: > 0 })
            foreach (var (path, name) in watched.Games)
                if (processes.Names.Contains(name)) running.Add(path);

        // Game boost first, so the snapshot can say which running game refused
        // it and the "game started" notification is right the first time. A
        // failure here must not cost the tick its snapshot (the window's
        // numbers and the timer rules hang on it).
        IReadOnlyCollection<string> refused = [];
        if (GameModeEnabled && GamePaths.Count > 0)
        {
            try
            {
                await _processService.CheckAndOptimizeAsync(BoostTargets(GamePaths, running),
                                                            processes is null ? null : processes.PidsOf,
                                                            processes is null ? null : processes.Contains,
                                                            AboveNormalGamePaths)
                                     .ConfigureAwait(false);
                refused = _processService.RefusedPaths ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn($"Game boost: {ex.GetType().Name}: {ex.Message}");
            }
        }

        TimeSpan idleFor = _idleMonitor.TimeSinceLastInput;   // idle gate below + timer idle-off (view model)
        var snapshot = ReadSnapshot() with { RunningGamePaths = running, IdleFor = idleFor, BoostRefusedPaths = refused };
        SnapshotUpdated?.Invoke(this, snapshot);

        // Idle gate: when AutoPurgeIdleOnly is on, suppress the purge
        // until the user has been quiet for IdleThresholdMs.
        bool idleGate = !AutoPurgeIdleOnly
                     || idleFor.TotalMilliseconds >= IdleThresholdMs;

        var noPurge = NoAutoPurgeGamePaths;
        bool blocked = noPurge.Count > 0
            && running.Any(p => noPurge.Contains(p, StringComparer.OrdinalIgnoreCase));

        DateTime nowUtc = UtcNow();
        bool cooldownPassed = IsCooldownComplete(_lastAutoPurgeUtc, nowUtc, AutoPurgeCooldownSeconds);

        bool purgeNeeded = cooldownPassed && ShouldAutoPurge(AutoPurgeEnabled, StandbyLimitMb, FreeLimitMb, snapshot,
            idleGate, blocked, AutoPurgeOnlyInGames, running.Count > 0);

        if (purgeNeeded)
        {
            if (await _purgeService.PurgeAsync(PurgeTrigger.Auto).ConfigureAwait(false))
                _lastAutoPurgeUtc = nowUtc;
        }
    }

    /// <summary>
    /// The boost-enabled games that are running. GamePaths is a subset of
    /// WatchedGamePaths, so <paramref name="running"/> already tells which run;
    /// passing only those saves a full process snapshot (~2.5 ms measured)
    /// per listed game per second. Called even when empty, so exited PIDs
    /// are still pruned.
    /// </summary>
    internal static List<string> BoostTargets(IReadOnlyList<string> boostPaths, IReadOnlyList<string> running) =>
        boostPaths.Where(p => running.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The automatic purge decision, Win32-free. Master switch on, both
    /// thresholds set (> 0) and crossed, the idle gate passed, no running
    /// game that pauses auto purge, and, with "only in games", a listed game
    /// running. Defaults ship as OFF + 0/0 so a fresh install never touches
    /// the standby list until the user opts in.
    /// </summary>
    internal static bool ShouldAutoPurge(bool enabled, int standbyLimitMb, int freeLimitMb,
        MemorySnapshot snapshot, bool idleGatePassed, bool blockingGameRunning,
        bool onlyInGames, bool anyGameRunning) =>
        enabled
        && standbyLimitMb > 0
        && freeLimitMb    > 0
        && snapshot.StandbyMb >= standbyLimitMb
        && snapshot.FreeMb    <= freeLimitMb
        && idleGatePassed
        && !blockingGameRunning
        && (!onlyInGames || anyGameRunning);

    /// <summary>
    /// Smart Cooldown gate: true when cooldown is disabled (&lt;= 0), no automatic
    /// purge has fired yet, or at least <paramref name="cooldownSeconds"/> have elapsed.
    /// </summary>
    internal static bool IsCooldownComplete(DateTime? lastAutoPurgeUtc, DateTime nowUtc, int cooldownSeconds) =>
        cooldownSeconds <= 0
        || lastAutoPurgeUtc is not { } last
        || (nowUtc - last).TotalSeconds >= cooldownSeconds;

    private MemorySnapshot ReadSnapshot()
    {
        var mem = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref mem);

        long totalMb     = (long)(mem.ullTotalPhys / (1024 * 1024));
        long availableMb = (long)(mem.ullAvailPhys / (1024 * 1024));
        var (standbyMb, standbyLowMb, modifiedMb) = ReadLists();

        // GlobalMemoryStatusEx.ullAvailPhys is "Available" = standby + free + zero.
        // Surfacing that as "Free" makes the UI double-count cache memory and
        // — when standby is the dominant bucket — makes Free track Standby
        // almost 1:1 (the "saçma değer" the user reported). Subtract standby
        // to expose the truly free + zero portion. Clamp at 0 to guard against
        // the race where standby was read just before a purge dropped it
        // below the value still cached in mem.ullAvailPhys.
        long freeMb = Math.Max(0, availableMb - standbyMb);

        return new MemorySnapshot(totalMb, freeMb, standbyMb)
        {
            StandbyLowMb = standbyLowMb,
            ModifiedMb   = modifiedMb,
        };
    }

    // Standby total, its priority-0 part and the modified list, in MB. The
    // counter fallback only knows the total.
    private (long standby, long low, long modified) ReadLists()
    {
        if (!_nativeListsFailed)
        {
            if (MemoryLists.TryReadTotals(out long standby, out long low, out long modified)) return (standby, low, modified);
            _nativeListsFailed = true;
            Logger.Warn("MemoryMonitor: SystemMemoryListInformation query failed, using performance counters");
        }

        EnsureCountersInitialized();
        double bytes = 0;
        bytes += SafeNext(_standbyNormal);
        bytes += SafeNext(_standbyReserve);
        bytes += SafeNext(_standbyCore);
        return ((long)(bytes / (1024 * 1024)), 0, 0);
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
