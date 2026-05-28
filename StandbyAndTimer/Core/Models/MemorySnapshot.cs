namespace StandbyAndTimer.Core.Models;

/// <summary>Immutable snapshot of system memory at a point in time (all values in MB).</summary>
public sealed record MemorySnapshot(long TotalMb, long FreeMb, long StandbyMb);
