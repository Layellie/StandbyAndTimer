using System.Diagnostics;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// The monitor asks every second which listed games run. It only needs the
// listed names: the other few hundred processes are compared in place and
// never become strings.
public class WatchedProcessesTests
{
    private static readonly string Self = Process.GetCurrentProcess().ProcessName;

    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Only_the_watched_names_are_in_the_snapshot()
    {
        var snapshot = RunningGames.Take(Names(Self, "no-such-game-0123456789"));

        Assert.Equal([Self], snapshot.Names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Environment.ProcessId, snapshot.PidsOf(Self));
        Assert.True(snapshot.Contains(Environment.ProcessId));
        Assert.False(snapshot.Contains(4));   // System runs, but is not watched
    }

    [Fact]
    public void Watched_names_match_whatever_their_case()
    {
        var snapshot = RunningGames.Take(Names(Self.ToUpperInvariant()));

        Assert.Contains(Environment.ProcessId, snapshot.PidsOf(Self));
    }

    [Fact]
    public void The_full_class_filters_the_same_way()
    {
        var snapshot = RunningGames.TakeFull(Names(Self));

        Assert.NotNull(snapshot);
        Assert.Equal([Self], snapshot.Names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Environment.ProcessId, snapshot.PidsOf(Self));
    }

    [Fact]
    public void Nothing_watched_running_gives_an_empty_snapshot()
    {
        var snapshot = RunningGames.Take(Names("no-such-game-0123456789"));

        Assert.Empty(snapshot.Names);
    }

    private sealed class NoIdle : IIdleMonitor
    {
        public TimeSpan TimeSinceLastInput => TimeSpan.Zero;
    }

    [Fact]
    public async Task The_monitor_reports_a_running_listed_game_by_its_path()
    {
        string running = $@"C:\Games\Here\{Self}.exe", absent = @"C:\Games\Away\no-such-game-0123456789.exe";
        using var monitor = new MemoryMonitorService(NullProxy.Of<IStandbyPurgeService>(),
                                                     NullProxy.Of<IProcessOptimizationService>(), new NoIdle())
        {
            WatchedGamePaths = [absent, running],
        };
        var first = new TaskCompletionSource<MemorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotUpdated += (_, s) => first.TrySetResult(s);

        await monitor.StartAsync(CancellationToken.None);
        MemorySnapshot snapshot;
        try { snapshot = await first.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await monitor.StopAsync(); }

        Assert.Equal([running], snapshot.RunningGamePaths);
        Assert.Equal([absent, running], monitor.WatchedGamePaths);
    }
}
