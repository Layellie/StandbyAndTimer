namespace StandbyAndTimer.Core.Models;

/// <summary>
/// What Game Mode does to a listed game's process while it runs. High and
/// AboveNormal both also get all cores and the EcoQoS opt-out; Unchanged
/// leaves the process alone (e.g. Process Lasso owns it).
/// </summary>
public enum GamePriority { High, AboveNormal, Unchanged }
