namespace StandbyAndTimer.Core.Models;

/// <param name="RunningGamePaths">Listed games running at this tick (empty when none are listed or running).</param>
/// <param name="IdleFor">Time since the last keyboard / mouse input at this tick.</param>
public sealed record MemorySnapshot(long TotalMb, long FreeMb, long StandbyMb,
                                    IReadOnlyList<string>? RunningGamePaths = null,
                                    TimeSpan IdleFor = default)
{
    public bool AnyGameRunning => RunningGamePaths is { Count: > 0 };
}
