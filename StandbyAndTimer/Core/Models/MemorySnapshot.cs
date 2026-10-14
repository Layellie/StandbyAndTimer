namespace StandbyAndTimer.Core.Models;

/// <param name="RunningGamePaths">Listed games running at this tick (empty when none are listed or running).</param>
/// <param name="IdleFor">Time since the last keyboard / mouse input at this tick.</param>
public sealed record MemorySnapshot(long TotalMb, long FreeMb, long StandbyMb,
                                    IReadOnlyList<string>? RunningGamePaths = null,
                                    TimeSpan IdleFor = default)
{
    public bool AnyGameRunning => RunningGamePaths is { Count: > 0 };

    /// <summary>Priority-0 standby pages: what a light purge frees.</summary>
    public long StandbyLowMb { get; init; }

    /// <summary>The modified page list: what a deep purge adds to the standby list before purging it.</summary>
    public long ModifiedMb { get; init; }

    /// <summary>
    /// What a purge of <paramref name="kind"/> would free now: light = the
    /// priority-0 standby pages, normal = the whole standby list, deep = the
    /// standby and modified lists (plus the system cache's working set, which
    /// is not counted here, so deep can free a little more).
    /// </summary>
    public long ClearableMb(PurgeKind kind) => kind switch
    {
        PurgeKind.Light => StandbyLowMb,
        PurgeKind.Deep  => StandbyMb + ModifiedMb,
        _               => StandbyMb,
    };
}
