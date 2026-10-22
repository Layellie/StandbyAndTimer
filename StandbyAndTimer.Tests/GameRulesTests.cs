using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// Records what the view model pushes to the monitor; runs no loop.
public sealed class FakeMemory : IMemoryMonitorService
{
    public event EventHandler<MemorySnapshot>? SnapshotUpdated;
    public int  StandbyLimitMb    { get; set; }
    public int  FreeLimitMb       { get; set; }
    public bool AutoPurgeEnabled  { get; set; }
    public bool GameModeEnabled   { get; set; }
    public bool AutoPurgeIdleOnly { get; set; }
    public bool AutoPurgeOnlyInGames { get; set; }
    public int  IdleThresholdMs   { get; set; }
    public int  AutoPurgeCooldownSeconds { get; set; }
    public IReadOnlyList<string> GamePaths            { get; set; } = [];
    public IReadOnlyList<string> WatchedGamePaths     { get; set; } = [];
    public IReadOnlyList<string> NoAutoPurgeGamePaths { get; set; } = [];
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() { }
    public void Raise(MemorySnapshot s) => SnapshotUpdated?.Invoke(this, s);
}

public class AutoPurgeDecisionTests
{
    private static readonly MemorySnapshot LowFree = new(16_000, FreeMb: 500, StandbyMb: 6_000);

    private static bool Decide(MemorySnapshot s, bool blocked = false, bool onlyInGames = false, bool anyGame = false) =>
        MemoryMonitorService.ShouldAutoPurge(enabled: true, standbyLimitMb: 4096, freeLimitMb: 1024, s,
            idleGatePassed: true, blockingGameRunning: blocked, onlyInGames: onlyInGames, anyGameRunning: anyGame);

    [Fact]
    public void Purges_when_both_thresholds_are_crossed() => Assert.True(Decide(LowFree));

    [Fact]
    public void Does_not_purge_while_a_game_that_blocks_auto_purge_runs() =>
        Assert.False(Decide(LowFree, blocked: true));

    [Fact]
    public void Disabled_switch_or_zero_limits_never_purge()
    {
        Assert.False(MemoryMonitorService.ShouldAutoPurge(false, 4096, 1024, LowFree, true, false, false, false));
        Assert.False(MemoryMonitorService.ShouldAutoPurge(true, 0, 1024, LowFree, true, false, false, false));
    }
}

public class RunningGamesTests
{
    [Fact]
    public void Matches_by_exe_name_case_insensitively()
    {
        var running = new HashSet<string>(["hades", "Discord"], StringComparer.OrdinalIgnoreCase);

        var hits = RunningGames.Match([@"C:\Games\Hades\Hades.exe", @"C:\Games\Other.exe"], running);

        Assert.Equal([@"C:\Games\Hades\Hades.exe"], hits);
    }
}

public class PerGameSettingsTests
{
    private const string Exe = @"C:\Windows\System32\notepad.exe";
    private readonly FakeMemory _mem = new();
    private readonly Harness _h;

    public PerGameSettingsTests()
    {
        _h = new Harness(memory: _mem);
        _h.Main.AddGameFromPath(Exe);
        _h.Main.SelectedGame = _h.Main.Games[0];
    }

    [Fact]
    public void A_new_game_gets_priority_and_allows_auto_purge_by_default()
    {
        Assert.True(_h.Main.SelectedGameBoost);
        Assert.True(_h.Main.SelectedGameAutoPurge);
        Assert.Equal([Exe], _mem.GamePaths);
        Assert.Equal([Exe], _mem.WatchedGamePaths);
        Assert.Empty(_mem.NoAutoPurgeGamePaths);
    }

    [Fact]
    public void Turning_priority_off_leaves_the_process_alone_but_keeps_watching_it()
    {
        _h.Main.SelectedGameBoost = false;

        Assert.Empty(_mem.GamePaths);
        Assert.Equal([Exe], _mem.WatchedGamePaths);
        Assert.False(_h.LastSaved.Games[0].BoostPriority);
    }

    [Fact]
    public void Turning_auto_purge_off_for_a_game_blocks_auto_purge_while_it_runs()
    {
        _h.Main.SelectedGameAutoPurge = false;

        Assert.Equal([Exe], _mem.NoAutoPurgeGamePaths);
        Assert.False(_h.LastSaved.Games[0].AllowAutoPurge);
    }

    [Fact]
    public void Saved_per_game_settings_come_back_on_load()
    {
        var mem = new FakeMemory();
        var h = new Harness(new AppSettings
        {
            FirstRunCompleted = true,
            Games = [new GameEntry { DisplayName = "notepad", ExecutablePath = Exe, BoostPriority = false, AllowAutoPurge = false }],
        }, memory: mem);

        h.Main.InitializeAsync().GetAwaiter().GetResult();

        Assert.Empty(mem.GamePaths);
        Assert.Equal([Exe], mem.NoAutoPurgeGamePaths);
    }
}
