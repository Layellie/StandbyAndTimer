using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

/// <summary>One row of the Maintenance tab's cleanup box.</summary>
public interface ICleanupTarget
{
    /// <summary>Stable id (see CleanupTargetIds), persisted.</summary>
    string Id { get; }

    /// <summary>True for targets that must not run while a listed game runs (shader caches).</summary>
    bool BlockedWhileGameRuns { get; }

    /// <summary>What a clean would remove now. Never throws for single files; may throw OperationCanceledException.</summary>
    Task<CleanupScan> ScanAsync(CancellationToken ct);

    /// <summary>Removes what it can; files in use are skipped and counted.</summary>
    Task<CleanupResult> CleanAsync(CancellationToken ct);
}
