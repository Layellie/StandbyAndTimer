namespace StandbyAndTimer.Core.Models;

/// <summary>What the tray icon, tooltip and menu labels show.</summary>
public sealed record TrayState(bool TimerActive, int PurgeCount, bool DeepPurge = false,
                               bool GameMode = false, TimerLockMode TimerLock = TimerLockMode.Unlocked);
