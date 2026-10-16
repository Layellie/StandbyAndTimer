using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

/// <summary>Where the Maintenance history lives between runs.</summary>
public interface ICleanupStatsStore
{
    /// <summary>The saved history, or an empty one (missing or unreadable file).</summary>
    CleanupStats Load();

    /// <summary>Writes the history; failures are logged, never thrown.</summary>
    void Save(CleanupStats stats);
}
