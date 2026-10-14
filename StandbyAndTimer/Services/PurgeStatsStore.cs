using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services;

/// <summary>
/// purge-history.json next to the log (%APPDATA%\StandbyAndTimer, removed
/// on uninstall). Written to a temp file and moved over the old one, so a
/// crash mid-write never leaves a half file; an unreadable file starts a
/// fresh history instead of failing.
/// </summary>
internal sealed class PurgeStatsStore : IPurgeStatsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    // DI uses this one (it only sees public constructors).
    public PurgeStatsStore() : this(Path.Combine(Logger.DataDirectory, "purge-history.json")) { }

    // Tests point it at a temp file.
    internal PurgeStatsStore(string path) => _path = path;

    public PurgeStats Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new PurgeStats();
                return JsonSerializer.Deserialize<PurgeStats>(File.ReadAllText(_path), Options) ?? new PurgeStats();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.Warn($"Purge history unreadable, starting a new one: {ex.Message}");
                return new PurgeStats();
            }
        }
    }

    public void Save(PurgeStats stats)
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
                Logger.Warn($"Purge history not saved: {ex.Message}");
            }
        }
    }
}

/// <summary>Keeps the history in memory only (tests, UiRender).</summary>
internal sealed class InMemoryPurgeStatsStore : IPurgeStatsStore
{
    private PurgeStats _stats = new();
    public PurgeStats Load() => _stats;
    public void Save(PurgeStats stats) => _stats = stats;
}
