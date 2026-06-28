using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IMemoryMonitorService : IDisposable
{
    /// <summary>Fired on a thread-pool thread every second with fresh memory data.</summary>
    event EventHandler<MemorySnapshot>? SnapshotUpdated;

    // Configurable thresholds — updated live from the ViewModel without restart.
    int  StandbyLimitMb   { get; set; }
    int  FreeLimitMb      { get; set; }
    bool AutoPurgeEnabled { get; set; }
    bool GameModeEnabled  { get; set; }
    IReadOnlyList<string> GamePaths { get; set; }

    /// <summary>When true, threshold-triggered purge fires only after the user has been idle for <see cref="IdleThresholdMs"/>.</summary>
    bool AutoPurgeIdleOnly { get; set; }

    /// <summary>Idle window in milliseconds before an idle-gated purge is allowed to fire.</summary>
    int  IdleThresholdMs   { get; set; }

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
