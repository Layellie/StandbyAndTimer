namespace StandbyAndTimer.Core.Models;

/// <summary>Active power plan. Name is null when Windows would not give one.</summary>
public sealed record PowerPlanInfo(Guid Id, string? Name);

/// <summary>One page file. Drive is "C:".</summary>
public sealed record PageFileInfo(string Drive, long TotalMb, long InUseMb, bool SystemManaged);

/// <summary>Windows tools the Maintenance tab opens. New members go at the end.</summary>
public enum SystemTool
{
    OptimizeDrives, DiskCleanup, RepairSystemFiles, PowerOptions, VirtualMemory,
    // Windows pages behind the gaming rows (spec §9.3)
    GraphicsSettings, CaptureSettings, CoreIsolation, GameModeSettings,
    PowerSettings,
}

/// <summary>Windows 11 power mode (Settings → System → Power), set on top of the Balanced plan.</summary>
public enum PowerMode { Unknown, BestPowerEfficiency, Balanced, BestPerformance, NotOnThisPlan }

/// <summary>Windows settings that matter in games. Hags null = not set (Windows' default).</summary>
public sealed record GamingInfo(bool? Hags, bool BackgroundRecording, bool MemoryIntegrity, bool GameMode);

/// <summary>A top-level item of the Downloads folder.</summary>
public sealed record DownloadEntry(string Path, string Name, bool IsFolder, long Bytes, DateTime Modified);
