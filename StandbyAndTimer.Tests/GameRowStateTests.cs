using System.ComponentModel;
using System.Diagnostics;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

// Two things the app finds out about a listed game while it runs, and says:
// the game refused the priority change (anti-cheat), or its exe is not there.
public class BoostRefusedTests
{
    private const string Exe = @"C:\Windows\System32\notepad.exe";
    private static readonly string Self = Process.GetCurrentProcess().ProcessName;

    private static MemorySnapshot Snapshot(params string[] refused) =>
        new(16_000, 800, 2_900) { BoostRefusedPaths = refused };

    [Fact]
    public async Task The_service_names_the_listed_path_that_refused_until_the_process_is_gone()
    {
        var svc = new ProcessOptimizationService { Apply = (_, _, _) => throw new Win32Exception(5) };
        IReadOnlyList<int> PidsOf(string name) => [Environment.ProcessId];

        Assert.Empty(svc.RefusedPaths);
        await svc.CheckAndOptimizeAsync([Exe], PidsOf, _ => true);
        Assert.Equal([Exe], svc.RefusedPaths);

        await svc.CheckAndOptimizeAsync([], PidsOf, _ => false);   // exited
        Assert.Empty(svc.RefusedPaths);
    }

    private sealed class RefusingOptimizer(string path) : IProcessOptimizationService
    {
        public int Calls;
        public Task CheckAndOptimizeAsync(IReadOnlyList<string> executablePaths,
            Func<string, IReadOnlyList<int>>? pidsOf = null, Func<int, bool>? isRunning = null,
            IReadOnlyCollection<string>? aboveNormalPaths = null)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<string> RefusedPaths => Calls > 0 ? [path] : [];
        public void Clear() { }
    }

    private sealed class NoIdle : IIdleMonitor
    {
        public TimeSpan TimeSinceLastInput => TimeSpan.Zero;
    }

    [Fact]
    public async Task The_monitor_boosts_first_so_the_same_ticks_snapshot_already_says_who_refused()
    {
        string running = $@"C:\Games\Here\{Self}.exe";
        var optimizer = new RefusingOptimizer(running);
        using var monitor = new MemoryMonitorService(NullProxy.Of<IStandbyPurgeService>(), optimizer, new NoIdle())
        {
            GameModeEnabled  = true,
            GamePaths        = [running],
            WatchedGamePaths = [running],
        };
        var first = new TaskCompletionSource<MemorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotUpdated += (_, s) => first.TrySetResult(s);

        await monitor.StartAsync(CancellationToken.None);
        MemorySnapshot snapshot;
        try { snapshot = await first.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await monitor.StopAsync(); }

        Assert.Equal([running], snapshot.BoostRefusedPaths);
    }

    [Fact]
    public void A_refusal_marks_the_game_and_says_so_once_in_the_status_line()
    {
        var h = new Harness();
        h.Main.AddGameFromPath(Exe);
        h.Main.GameModeEnabled = true;

        h.Main.ShowSnapshot(Snapshot(Exe));
        Assert.True(h.Main.Games[0].BoostRefused);
        Assert.Equal("Str_Status_BoostRefused", h.Main.StatusMessage);

        h.Main.StatusMessage = "something newer";
        h.Main.ShowSnapshot(Snapshot(Exe));                 // still refusing: not said again
        Assert.Equal("something newer", h.Main.StatusMessage);

        h.Main.ShowSnapshot(Snapshot());                    // the game closed
        Assert.False(h.Main.Games[0].BoostRefused);
    }

    [Fact]
    public async Task The_game_started_notification_says_the_priority_was_not_changed()
    {
        var h = new Harness();
        h.Main.AddGameFromPath(Exe);
        h.Main.GameModeEnabled = true;
        var shown = new List<GameStartedArgs>();
        h.Main.GameStartedNotification += (_, a) => shown.Add(a);

        h.Main.ShowSnapshot(Snapshot(Exe));
        await h.Main.HandleRunningGamesAsync([Exe]);

        Assert.Equal("Str_GameNotify_BoostRefused", Assert.Single(shown).Body.Split(" · ")[0]);
    }

    [Fact]
    public async Task A_game_that_took_the_priority_is_announced_as_before()
    {
        var h = new Harness();
        h.Main.AddGameFromPath(Exe);
        h.Main.GameModeEnabled = true;
        var shown = new List<GameStartedArgs>();
        h.Main.GameStartedNotification += (_, a) => shown.Add(a);

        h.Main.ShowSnapshot(Snapshot());
        await h.Main.HandleRunningGamesAsync([Exe]);

        Assert.Equal("Str_GameNotify_Boost", Assert.Single(shown).Body.Split(" · ")[0]);
    }
}

public class MissingGameTests
{
    private const string Here = @"C:\Windows\System32\notepad.exe";
    private const string Away = @"Q:\Games\Away\away.exe";

    private static GameEntry Game(string path) =>
        new() { DisplayName = System.IO.Path.GetFileNameWithoutExtension(path), ExecutablePath = path };

    private static async Task<Harness> LoadAsync(params GameEntry[] games)
    {
        var h = new Harness(new AppSettings { FirstRunCompleted = true, Games = [.. games] });
        await h.Main.InitializeAsync();
        h.Main.GameFileExists = path => path == Here;
        return h;
    }

    [Fact]
    public async Task Looking_again_marks_the_games_whose_file_is_not_there()
    {
        var h = await LoadAsync(Game(Here), Game(Away));

        await h.Main.RefreshMissingGamesAsync();

        Assert.False(h.Main.Games[0].IsMissing);
        Assert.True(h.Main.Games[1].IsMissing);
    }

    [Fact]
    public async Task A_drive_that_came_back_clears_the_mark()
    {
        var h = await LoadAsync(Game(Away));
        await h.Main.RefreshMissingGamesAsync();
        Assert.True(h.Main.Games[0].IsMissing);

        h.Main.GameFileExists = _ => true;
        await h.Main.RefreshMissingGamesAsync();

        Assert.False(h.Main.Games[0].IsMissing);
    }

    [Fact]
    public async Task Starting_a_missing_game_marks_it_and_says_so()
    {
        var h = await LoadAsync(Game(Away));
        bool started = false;
        h.Main.StartGame = _ => started = true;

        h.Main.LaunchGame(h.Main.Games[0]);

        Assert.False(started);
        Assert.True(h.Main.Games[0].IsMissing);
        Assert.Equal("Str_Status_GameMissing", h.Main.StatusMessage);
    }

    [Fact]
    public async Task A_game_seen_running_is_not_missing()
    {
        var h = await LoadAsync(Game(Away));
        await h.Main.RefreshMissingGamesAsync();

        await h.Main.HandleRunningGamesAsync([Away]);

        Assert.False(h.Main.Games[0].IsMissing);
    }

    [Fact]
    public async Task The_mark_is_never_saved()
    {
        var h = await LoadAsync(Game(Away));
        await h.Main.RefreshMissingGamesAsync();
        h.Main.GameModeEnabled = !h.Main.GameModeEnabled;   // any change saves

        Assert.Equal(Away, Assert.Single(h.LastSaved.Games).ExecutablePath);
    }
}
