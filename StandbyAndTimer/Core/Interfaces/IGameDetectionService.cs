namespace StandbyAndTimer.Core.Interfaces;

public interface IGameDetectionService : IDisposable
{
    /// <summary>Raised once per session per detected fullscreen executable. Payload is the absolute exe path.</summary>
    event EventHandler<string>? GameDetected;

    /// <summary>Updated by MainViewModel whenever the Games list mutates; the service uses it to skip already-listed exes.</summary>
    IReadOnlySet<string> KnownGamePaths { get; set; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
