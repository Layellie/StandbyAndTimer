namespace StandbyAndTimer.Core.Interfaces;

public interface IGameDetectionService : IDisposable
{
    /// <summary>Raised once per session per executable that stayed fullscreen for two polls. Payload is the absolute exe path.</summary>
    event EventHandler<string>? GameDetected;

    /// <summary>Updated by MainViewModel whenever the Games list or the dismissed list mutates; the service skips these exes.</summary>
    IReadOnlySet<string> KnownGamePaths { get; set; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
