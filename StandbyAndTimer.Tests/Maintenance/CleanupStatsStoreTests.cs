using System.IO;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public sealed class CleanupStatsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-chist-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trip_through_the_file()
    {
        string path = Path.Combine(_dir, "cleanup-history.json");
        var stats = new CleanupStats();
        stats.Add(new CleanupRecord(new DateTime(2026, 10, 16, 20, 1, 2), CleanupTargetIds.NvidiaShader, 123456789, 4));

        new CleanupStatsStore(path).Save(stats);
        var back = new CleanupStatsStore(path).Load();

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(stats.Recent[0], back.Recent[0]);
        Assert.Equal(1, back.Of(CleanupTargetIds.NvidiaShader).Count);
        Assert.Equal(123456789, back.AllTime.FreedBytes);
    }

    [Fact]
    public void A_damaged_or_missing_file_starts_a_fresh_history()
    {
        string path = Path.Combine(_dir, "cleanup-history.json");
        Assert.Empty(new CleanupStatsStore(path).Load().Recent);

        File.WriteAllText(path, "{ broken");
        Assert.Equal(0, new CleanupStatsStore(path).Load().AllTime.Count);
    }
}
