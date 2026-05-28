using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IMemoryMonitorService : IDisposable
{
    /// <summary>Fired on a thread-pool thread every second with fresh memory data.</summary>
    event EventHandler<MemorySnapshot>? SnapshotUpdated;

    // Configurable thresholds — updated live from the ViewModel without restart.
    int StandbyLimitMb { get; set; }
    int FreeLimitMb    { get; set; }
    bool GameModeEnabled { get; set; }
    IReadOnlyList<string> GamePaths { get; set; }

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
