namespace StandbyAndTimer.Core.Models;

/// <summary>What a target holds now. Found = false when none of its folders exist.</summary>
public sealed record CleanupScan(bool Found, long Bytes, int Files)
{
    public static readonly CleanupScan NotFound = new(false, 0, 0);
}

/// <summary>What a clean did: bytes of the files actually deleted, files deleted, files skipped (in use / denied).</summary>
public sealed record CleanupResult(long FreedBytes, int Deleted, int Skipped)
{
    public static readonly CleanupResult Nothing = new(0, 0, 0);
}

/// <summary>One history entry (local time).</summary>
public sealed record CleanupRecord(DateTime At, string TargetId, long FreedBytes, int Skipped);

/// <summary>Persisted target ids: never rename (CleanupSelection and the history store them).</summary>
public static class CleanupTargetIds
{
    public const string NvidiaShader  = "nvidia-shader";
    public const string AmdShader     = "amd-shader";
    public const string UserTemp      = "user-temp";
    public const string WindowsTemp   = "windows-temp";
    public const string WindowsUpdate = "windows-update";
    public const string RecycleBin    = "recycle-bin";
    public const string Downloads     = "downloads";   // history only
    // 3.0.0 additions (spec §9.1)
    public const string DirectXShader        = "directx-shader";
    public const string SteamShader          = "steam-shader";
    public const string DriverLeftovers      = "driver-leftovers";
    public const string DeliveryOptimization = "delivery-optimization";
    public const string CrashDumps           = "crash-dumps";
    // round 2 (spec §10.2)
    public const string SystemCrashDumps     = "system-crash-dumps";
    public const string WindowsLogs          = "windows-logs";
    public const string WindowsOld           = "windows-old";

    /// <summary>Ticked when CleanupSelection was never saved.</summary>
    public static readonly IReadOnlyList<string> DefaultSelection = [UserTemp, WindowsTemp, WindowsUpdate];
}
