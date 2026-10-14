using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public sealed class MemoryMonitorTests
{
    private sealed class ThrowingOnceOptimizer : IProcessOptimizationService
    {
        public int Calls;
        public readonly TaskCompletionSource SecondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CheckAndOptimizeAsync(IReadOnlyList<string> executablePaths,
            Func<string, IReadOnlyList<int>>? pidsOf = null, Func<int, bool>? isRunning = null)
        {
            if (Interlocked.Increment(ref Calls) == 1)
                throw new InvalidOperationException("first tick fails");
            SecondCall.TrySetResult();
            return Task.CompletedTask;
        }

        public void Clear() { }
    }

    private sealed class NoIdle : IIdleMonitor
    {
        public TimeSpan TimeSinceLastInput => TimeSpan.Zero;
    }

    [Fact]
    public async Task A_failing_tick_does_not_stop_the_loop()
    {
        var optimizer = new ThrowingOnceOptimizer();
        using var monitor = new MemoryMonitorService(NullProxy.Of<IStandbyPurgeService>(), optimizer, new NoIdle())
        {
            GameModeEnabled = true,
            GamePaths       = [@"C:\Games\x.exe"],
        };

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            // Before the fix the exception ended the loop and this never came.
            await optimizer.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await monitor.StopAsync();
        }

        Assert.True(optimizer.Calls >= 2);
    }

    [Fact]
    public void Only_running_boost_games_are_scanned()
    {
        string cs2 = @"C:\Games\cs2.exe", val = @"D:\Riot\VALORANT.exe", elden = @"E:\Elden\eldenring.exe";

        var targets = MemoryMonitorService.BoostTargets([cs2, val, elden], [@"d:\riot\valorant.exe", @"C:\Other\notboosted.exe"]);

        Assert.Equal([val], targets);
    }

    [Fact]
    public void Nothing_running_means_nothing_to_scan() =>
        Assert.Empty(MemoryMonitorService.BoostTargets([@"C:\Games\cs2.exe"], []));
}
