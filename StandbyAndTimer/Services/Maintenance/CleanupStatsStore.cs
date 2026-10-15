using System.IO;
using System.Text.Json;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// cleanup-history.json next to the log (%APPDATA%\StandbyAndTimer, removed on
/// uninstall), written like purge-history.json: temp file, then moved over the
/// old one; an unreadable file starts a fresh history instead of failing.
/// </summary>
internal sealed class CleanupStatsStore : ICleanupStatsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    // DI uses this one (it only sees public constructors).
    public CleanupStatsStore() : this(Path.Combine(Logger.DataDirectory, "cleanup-history.json")) { }

    // Tests point it at a temp file.
    internal CleanupStatsStore(string path) => _path = path;

    public CleanupStats Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new CleanupStats();
                return JsonSerializer.Deserialize<CleanupStats>(File.ReadAllText(_path), Options) ?? new CleanupStats();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.Warn($"Cleanup history unreadable, starting a new one: {ex.Message}");
                return new CleanupStats();
            }
        }
    }

    public void Save(CleanupStats stats)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(stats, Options));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Cleanup history not saved: {ex.Message}");
            }
        }
    }
}

/// <summary>Keeps the history in memory only (tests, UiRender, the inert view model).</summary>
internal sealed class InMemoryCleanupStatsStore : ICleanupStatsStore
{
    private CleanupStats _stats = new();
    public CleanupStats Load() => _stats;
    public void Save(CleanupStats stats) => _stats = stats;
}
