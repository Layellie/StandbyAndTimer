namespace StandbyAndTimer.Core.Models;

/// <summary>Which purge ran (IStandbyPurgeService.LightMode / DeepMode at the time).</summary>
public enum PurgeKind { Normal, Light, Deep }

/// <summary>What asked for the purge.</summary>
public enum PurgeTrigger { Manual, Auto, GameStart }

/// <param name="FreedMb">Free + zeroed memory after the purge minus before it (never negative).</param>
public sealed record PurgeResult(DateTime At, PurgeKind Kind, PurgeTrigger Trigger, long FreedMb);
