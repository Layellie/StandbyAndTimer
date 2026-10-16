using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Tests.Maintenance;

public class CleanupStatsTests
{
    private static readonly DateTime Today = new(2026, 10, 16, 21, 0, 0);
    private static CleanupRecord R(DateTime at, string id, long mb, int skipped = 0) => new(at, id, mb * 1024 * 1024, skipped);

    [Fact]
    public void Totals_by_day_week_target_and_all_time()
    {
        var s = new CleanupStats();
        s.Add(R(Today.AddDays(-10), CleanupTargetIds.UserTemp, 400));
        s.Add(R(Today.AddDays(-3), CleanupTargetIds.NvidiaShader, 1000));
        s.Add(R(Today.AddHours(-2), CleanupTargetIds.UserTemp, 50));
        s.Add(R(Today, CleanupTargetIds.WindowsUpdate, 2000));

        Assert.Equal(2, s.LastDays(1, Today).Count);
        Assert.Equal(2050L * 1024 * 1024, s.LastDays(1, Today).FreedBytes);
        Assert.Equal(3050L * 1024 * 1024, s.LastDays(7, Today).FreedBytes);
        Assert.Equal(4, s.AllTime.Count);
        Assert.Equal(2, s.Of(CleanupTargetIds.UserTemp).Count);
        Assert.Equal(0, s.Of(CleanupTargetIds.AmdShader).Count);
        Assert.Equal(Today.AddDays(-10), s.Since);
        Assert.Equal(Today, s.Recent[0].At);
    }

    [Fact]
    public void Keeps_the_last_twenty_and_sixty_days_but_all_time_totals()
    {
        var s = new CleanupStats();
        for (int i = 100; i >= 0; i--) s.Add(R(Today.AddDays(-i), CleanupTargetIds.UserTemp, 1));

        Assert.Equal(CleanupStats.RecentMax, s.Recent.Count);
        Assert.Equal(CleanupStats.KeepDays, s.Days.Count);
        Assert.Equal(101, s.AllTime.Count);
    }
}
