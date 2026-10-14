using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

/// <summary>Where the purge history lives between runs.</summary>
public interface IPurgeStatsStore
{
    /// <summary>The saved history, or an empty one (missing or unreadable file).</summary>
    PurgeStats Load();

    /// <summary>Writes the history; failures are logged, never thrown.</summary>
    void Save(PurgeStats stats);
}
